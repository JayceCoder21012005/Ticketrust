using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace TicketRush.Infrastructure.Persistence;

/// <summary>
/// Encrypts a ticket's QR secret with ASP.NET Data Protection before it reaches <c>tickets.qr_secret</c> (spec 08). The
/// key ring is the one the app already configures (Redis, see <c>DependencyInjection.PersistDataProtectionKeysToRedis</c>),
/// so every api and worker instance reads secrets another one wrote.
/// </summary>
internal sealed class ProtectedBytesConverter(IDataProtector protector)
    : ValueConverter<byte[], byte[]>(plain => protector.Protect(plain), protectedBytes => protector.Unprotect(protectedBytes));

internal static class TicketSecretProtector
{
    /// <summary>Changing it makes every stored secret unreadable, so it is versioned rather than renamed casually.</summary>
    public const string Purpose = "TicketRush.Tickets.QrSecret.v1";

    public static IDataProtector Create(IDataProtectionProvider? provider) =>
        provider?.CreateProtector(Purpose) ?? new MissingProtector();

    /// <summary>Stands in when the context was built without Data Protection (design time, migrations): fails loudly on use.</summary>
    private sealed class MissingProtector : IDataProtector
    {
        public IDataProtector CreateProtector(string purpose) => this;

        public byte[] Protect(byte[] plaintext) => throw Missing();

        public byte[] Unprotect(byte[] protectedData) => throw Missing();

        private static InvalidOperationException Missing() =>
            new("AppDbContext was created without an IDataProtectionProvider, so ticket secrets cannot be encrypted or read.");
    }
}

/// <summary>
/// EF caches the model per context type, but the ticket converter captures one provider's protector. Keying the cache on
/// the provider keeps each host (and each test host) on its own key ring.
/// </summary>
internal sealed class AppModelCacheKeyFactory(ModelCacheKeyFactoryDependencies dependencies) : ModelCacheKeyFactory(dependencies)
{
    public override object Create(DbContext context, bool designTime) =>
        (base.Create(context, designTime), (context as AppDbContext)?.DataProtection);
}
