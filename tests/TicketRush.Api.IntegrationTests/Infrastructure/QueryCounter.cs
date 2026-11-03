using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace TicketRush.Api.IntegrationTests.Infrastructure;

/// <summary>Records the SQL of every reader command EF sends, to prove how many queries a request costs.</summary>
public sealed class QueryCounter : DbCommandInterceptor
{
    private readonly ConcurrentQueue<string> _commands = new();

    public void Reset() => _commands.Clear();

    public IReadOnlyList<string> Matching(Func<string, bool> predicate) => _commands.Where(predicate).ToList();

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        _commands.Enqueue(command.CommandText);
        return ValueTask.FromResult(result);
    }
}
