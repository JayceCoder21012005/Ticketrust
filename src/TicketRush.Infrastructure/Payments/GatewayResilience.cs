using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using TicketRush.Application.Abstractions.Payments;
using TicketRush.Domain.Primitives;

namespace TicketRush.Infrastructure.Payments;

/// <summary>
/// The outbound call policy of one payment provider (C11). From the outside in: retry (exponential backoff + jitter),
/// circuit breaker, per attempt timeout. Retries sit outside the breaker so every attempt counts toward it, and an
/// open circuit is never retried. Each provider gets its own instance, so one provider's outage does not open another's circuit.
/// Anything the caller did not cancel and the gateway did not reject on purpose
/// (<see cref="PaymentRejectedException"/>) is a transient fault.
/// </summary>
public sealed partial class GatewayResilience
{
    private readonly string _provider;
    private readonly ILogger _logger;
    private readonly CircuitBreakerStateProvider _state = new();
    private readonly ResiliencePipeline _pipeline;

    public GatewayResilience(string provider, GatewayResilienceOptions options, TimeProvider time, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _provider = provider;
        _logger = logger;
        var builder = new ResiliencePipelineBuilder { TimeProvider = time };
        if (options.RetryCount > 0)
        {
            // Polly rejects zero retries; "no retries" simply means no retry strategy.
            builder.AddRetry(RetryStrategy(options));
        }

        _pipeline = builder
            .AddCircuitBreaker(BreakerStrategy(options))
            .AddTimeout(new TimeoutStrategyOptions { Timeout = options.Timeout })
            .Build();
    }

    /// <summary>Current breaker state; <see cref="CircuitState.Open"/> means calls fail fast.</summary>
    public CircuitState CircuitState => _state.CircuitState;

    /// <exception cref="PaymentGatewayUnavailableException">Retries are exhausted, an attempt timed out or the circuit is open.</exception>
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken ct)
    {
        try
        {
            return await _pipeline.ExecuteAsync(async token => await call(token), ct);
        }
        catch (BrokenCircuitException ex)
        {
            throw new PaymentGatewayUnavailableException($"The {_provider} circuit breaker is open.", ex);
        }
        catch (Exception ex) when (IsTransient(ex, ct))
        {
            throw new PaymentGatewayUnavailableException($"The {_provider} gateway did not respond.", ex);
        }
    }

    /// <summary>
    /// Outages, timeouts and I/O errors. Not transient, and therefore never retried, never counted by the breaker and
    /// rethrown unchanged: a gateway refusal, a cancellation the caller requested (one it did not, such as an HttpClient timeout, is
    /// transient), and our own bugs or rule violations
    /// (<see cref="ArgumentException"/>, <see cref="DomainException"/>), which would otherwise be reported as an outage.
    /// </summary>
    private static bool IsTransient(Exception ex, CancellationToken callerToken) =>
        ex is not (PaymentRejectedException or ArgumentException or DomainException)
        && !(ex is OperationCanceledException && callerToken.IsCancellationRequested);

    private RetryStrategyOptions RetryStrategy(GatewayResilienceOptions options) => new()
    {
        MaxRetryAttempts = options.RetryCount,
        BackoffType = DelayBackoffType.Exponential,
        UseJitter = true,
        Delay = options.RetryBaseDelay,
        ShouldHandle = args => ValueTask.FromResult(
            args.Outcome.Exception is { } ex && IsTransient(ex, args.Context.CancellationToken) && ex is not BrokenCircuitException),
        OnRetry = args =>
        {
            LogRetry(_logger, _provider, args.AttemptNumber + 1, args.Outcome.Exception);
            return default;
        },
    };

    private CircuitBreakerStrategyOptions BreakerStrategy(GatewayResilienceOptions options) => new()
    {
        FailureRatio = options.BreakerFailureRatio,
        MinimumThroughput = options.BreakerMinimumThroughput,
        SamplingDuration = options.BreakerSamplingDuration,
        BreakDuration = options.BreakerBreakDuration,
        ShouldHandle = args => ValueTask.FromResult(args.Outcome.Exception is { } ex && IsTransient(ex, args.Context.CancellationToken)),
        StateProvider = _state,
        OnOpened = _ =>
        {
            LogOpened(_logger, _provider);
            return default;
        },
        OnClosed = _ =>
        {
            LogClosed(_logger, _provider);
            return default;
        },
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Payment gateway {Provider} call failed, retry {Attempt}")]
    private static partial void LogRetry(ILogger logger, string provider, int attempt, Exception? exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Payment gateway {Provider} circuit opened")]
    private static partial void LogOpened(ILogger logger, string provider);

    [LoggerMessage(Level = LogLevel.Information, Message = "Payment gateway {Provider} circuit closed")]
    private static partial void LogClosed(ILogger logger, string provider);
}
