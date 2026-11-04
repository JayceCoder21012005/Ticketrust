using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Testcontainers.Redis;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Api.IntegrationTests.Infrastructure;

/// <summary>Spec 07, R3: production Redis must run with AOF; the guard warns when it does not and never crashes the host.</summary>
public sealed class RedisDurabilityCheckTests(ContainersFixture containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeProbe(bool? aof = null, Exception? fail = null) : IRedisPersistenceProbe
    {
        public int Calls { get; private set; }

        public Task<bool?> IsAofEnabledAsync(CancellationToken ct)
        {
            Calls++;
            return fail is null ? Task.FromResult(aof) : Task.FromException<bool?>(fail);
        }
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "test";

        public string ContentRootPath { get; set; } = "";

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private sealed class CapturingLogger : ILogger<RedisDurabilityCheck>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Levels.Add(logLevel);
    }

    private static async Task<CapturingLogger> RunAsync(IRedisPersistenceProbe probe, string environment)
    {
        var logger = new CapturingLogger();
        await new RedisDurabilityCheck(probe, new FakeEnvironment(environment), logger).CheckAsync(Ct);
        return logger;
    }

    [Fact]
    public async Task Warns_in_production_when_aof_is_off()
    {
        var logger = await RunAsync(new FakeProbe(aof: false), Environments.Production);

        logger.Levels.ShouldBe([LogLevel.Warning]);
    }

    [Fact]
    public async Task Does_not_warn_when_aof_is_on()
    {
        var logger = await RunAsync(new FakeProbe(aof: true), Environments.Production);

        logger.Levels.ShouldNotContain(LogLevel.Warning);
    }

    [Fact]
    public async Task Skips_the_probe_in_development()
    {
        var probe = new FakeProbe(aof: false);

        var logger = await RunAsync(probe, Environments.Development);

        probe.Calls.ShouldBe(0);
        logger.Levels.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unknown_state_is_only_informational()
    {
        var logger = await RunAsync(new FakeProbe(aof: null), Environments.Production);

        logger.Levels.ShouldBe([LogLevel.Information]);
    }

    [Fact]
    public async Task A_denied_or_failing_probe_never_throws_and_is_only_informational()
    {
        var logger = await RunAsync(new FakeProbe(fail: new RedisServerException("NOPERM this user has no permissions to run the 'info' command")), Environments.Production);

        logger.Levels.ShouldBe([LogLevel.Information]);
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        var probe = new FakeProbe(fail: new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(() => RunAsync(probe, Environments.Production));
    }

    [Fact]
    public async Task The_probe_reports_aof_off_on_a_default_redis()
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(containers.RedisConnectionString);

        (await new RedisPersistenceProbe(redis).IsAofEnabledAsync(Ct)).ShouldBe(false);
    }

    [Fact]
    public async Task The_probe_reports_aof_on_when_redis_runs_with_appendonly_yes()
    {
        await using var container = new RedisBuilder("redis:7.4").WithCommand("--appendonly", "yes").Build();
        await container.StartAsync(Ct);
        await using var redis = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());

        (await new RedisPersistenceProbe(redis).IsAofEnabledAsync(Ct)).ShouldBe(true);
    }

    [Fact]
    public async Task Both_hosts_get_the_guard_from_AddInfrastructure()
    {
        await using var factory = new ApiFactory(await containers.CreateDatabaseAsync(), containers.RedisConnectionString);

        factory.Services.GetServices<IHostedService>().OfType<RedisDurabilityCheck>().ShouldHaveSingleItem();
    }
}
