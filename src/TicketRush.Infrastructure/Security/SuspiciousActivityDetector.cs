using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using TicketRush.Application.Abstractions.Security;
using TicketRush.Domain.Users;
using TicketRush.Infrastructure.Persistence;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Infrastructure.Security;

/// <summary>
/// Spec 12 velocity flags: counts queue joins per subnet per minute in Redis and phone numbers across accounts in the database,
/// and writes an <see cref="AccountFlag"/> (once per open user and reason) for the admin review. Never throws.
/// </summary>
public sealed partial class SuspiciousActivityDetector(
    AppDbContext db,
    IConnectionMultiplexer redis,
    RedisKeys keys,
    TimeProvider time,
    ILogger<SuspiciousActivityDetector> logger) : ISuspiciousActivityDetector
{
    public const int MaxJoinsPerSubnetPerMinute = 20;
    public const int MaxAccountsPerPhone = 3;

    public async Task QueueJoinedAsync(Guid userId, string? ipAddress, CancellationToken ct)
    {
        if (Subnet(ipAddress) is not { } subnet)
        {
            return;
        }

        try
        {
            var minute = time.GetUtcNow().ToUnixTimeSeconds() / 60;
            var key = keys.SubnetJoins(subnet, minute);
            var batch = redis.GetDatabase().CreateTransaction();
            var count = batch.StringIncrementAsync(key);
            _ = batch.KeyExpireAsync(key, TimeSpan.FromMinutes(2));
            await batch.ExecuteAsync().WaitAsync(ct);
            var joins = await count;
            if (joins > MaxJoinsPerSubnetPerMinute)
            {
                await RaiseAsync([userId], AccountFlag.SubnetJoinBurst, $"{joins} queue joins in one minute from {subnet}", ct);
            }
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or DbUpdateException or Npgsql.NpgsqlException)
        {
            LogFailed(ex, AccountFlag.SubnetJoinBurst);
        }
    }

    public async Task PhoneNumberSavedAsync(Guid userId, string? phoneNumber, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
        {
            return;
        }

        try
        {
            // The profile accepts 0xxxxxxxxx and +84xxxxxxxxx: both spellings of one number count together.
            var local = phoneNumber.StartsWith("+84", StringComparison.Ordinal) ? $"0{phoneNumber[3..]}" : phoneNumber;
            var international = local.StartsWith('0') ? $"+84{local[1..]}" : local;
            var holders = await db.Users.AsNoTracking()
                .Where(u => u.PhoneNumber == local || u.PhoneNumber == international)
                .Select(u => u.Id).Take(50).ToListAsync(ct);
            if (holders.Count > MaxAccountsPerPhone)
            {
                await RaiseAsync(holders, AccountFlag.SharedPhone, $"phone number on {holders.Count} accounts", ct);
            }
        }
        catch (Exception ex) when (ex is DbUpdateException or Npgsql.NpgsqlException or TimeoutException)
        {
            LogFailed(ex, AccountFlag.SharedPhone);
        }
    }

    /// <summary>The /24 of an IPv4 address, the /48 of an IPv6 one; <c>null</c> when there is no usable address.</summary>
    public static string? Subnet(string? ipAddress)
    {
        if (!IPAddress.TryParse(ipAddress, out var address))
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24");
        }

        Array.Clear(bytes, 6, bytes.Length - 6);
        return $"{new IPAddress(bytes)}/48";
    }

    private async Task RaiseAsync(IReadOnlyCollection<Guid> userIds, string reason, string detail, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        foreach (var userId in userIds)
        {
            var flag = AccountFlag.Raise(userId, reason, detail, now);
            var inserted = await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO account_flags (id, user_id, reason, detail, created_at)
                VALUES ({flag.Id}, {flag.UserId}, {flag.Reason}, {flag.Detail}, {flag.CreatedAt})
                ON CONFLICT (user_id, reason) WHERE resolved_at IS NULL DO NOTHING
                """,
                ct);
            if (inserted > 0)
            {
                LogFlagged(userId, reason);
            }
        }
    }

    [LoggerMessage(EventId = 1221, Level = LogLevel.Warning, Message = "Account {UserId} flagged for review: {Reason}.")]
    private partial void LogFlagged(Guid userId, string reason);

    [LoggerMessage(EventId = 1222, Level = LogLevel.Warning, Message = "Velocity check {Reason} skipped: a store did not answer.")]
    private partial void LogFailed(Exception exception, string reason);
}
