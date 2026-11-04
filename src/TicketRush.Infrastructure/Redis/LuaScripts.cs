using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;

namespace TicketRush.Infrastructure.Redis;

/// <summary>
/// Runs the embedded <c>Redis/Scripts/*.lua</c> files. Calls go out as EVALSHA; when the server has not seen the
/// script yet (first call, restart, <c>SCRIPT FLUSH</c>) it falls back to EVAL, which also caches it.
/// </summary>
public static class LuaScripts
{
    private sealed record Script(string Text, byte[] Sha1);

    private static readonly ConcurrentDictionary<string, Script> Cache = new(StringComparer.Ordinal);

    public static async Task<RedisResult> RunAsync(IDatabase db, string name, RedisKey[] keys, RedisValue[] args)
    {
        var script = Cache.GetOrAdd(name, Load);
        try
        {
            return await db.ScriptEvaluateAsync(script.Sha1, keys, args);
        }
        catch (RedisServerException ex) when (ex.Message.StartsWith("NOSCRIPT", StringComparison.Ordinal))
        {
            return await db.ScriptEvaluateAsync(script.Text, keys, args);
        }
    }

    private static Script Load(string name)
    {
        var assembly = typeof(LuaScripts).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith($".{name}.lua", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = reader.ReadToEnd();
#pragma warning disable CA5350 // Redis identifies scripts by SHA-1 (EVALSHA); this is addressing, not security.
        return new Script(text, SHA1.HashData(Encoding.UTF8.GetBytes(text)));
#pragma warning restore CA5350
    }
}
