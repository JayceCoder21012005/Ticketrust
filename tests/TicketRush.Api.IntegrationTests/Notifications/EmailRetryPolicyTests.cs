using System.ComponentModel.DataAnnotations;
using System.Net.Sockets;
using System.Text.Json;
using MailKit;
using MailKit.Net.Smtp;
using MimeKit;
using Scriban.Parsing;
using Scriban.Syntax;
using TicketRush.Infrastructure.Notifications;
using TicketRush.Infrastructure.Options;

namespace TicketRush.Api.IntegrationTests.Notifications;

/// <summary>
/// The email delivery worker's own retry rules (spec 09: an SMTP outage only delays email): a schedule of about a day, an immediate
/// dead-letter for a delivery that can never succeed, and the backoff of the outage breaker. Pure functions, no containers.
/// </summary>
public sealed class EmailRetryPolicyTests
{
    private static readonly EmailRetryPolicy Defaults = EmailRetryPolicy.From(new EmailDeliveryOptions());

    [Fact]
    public void The_defaults_are_30_seconds_doubling_to_one_hour_over_30_attempts()
    {
        var options = new EmailDeliveryOptions();

        options.BaseDelay.ShouldBe(TimeSpan.FromSeconds(30));
        options.MaxDelay.ShouldBe(TimeSpan.FromHours(1));
        options.MaxAttempts.ShouldBe(30);
    }

    [Fact]
    public void The_ceiling_doubles_from_the_base_and_never_passes_the_cap()
    {
        var previous = TimeSpan.Zero;
        for (var attempt = 1; attempt < Defaults.MaxAttempts; attempt++)
        {
            var ceiling = Defaults.NextDelay(attempt, random: 1);

            ceiling.ShouldBeGreaterThanOrEqualTo(previous, $"attempt {attempt}");
            ceiling.ShouldBeLessThanOrEqualTo(TimeSpan.FromHours(1));
            ceiling.ShouldBe(TimeSpan.FromMilliseconds(Math.Min(30_000 * Math.Pow(2, attempt - 1), 3_600_000)));
            previous = ceiling;
        }

        Defaults.NextDelay(1, random: 1).ShouldBe(TimeSpan.FromSeconds(30));
        Defaults.NextDelay(8, random: 1).ShouldBe(TimeSpan.FromHours(1), "30 s × 2^7 = 64 min is capped");
        Defaults.NextDelay(500, random: 1).ShouldBe(TimeSpan.FromHours(1), "a huge attempt count must not overflow");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(20)]
    public void Equal_jitter_keeps_each_delay_between_half_and_the_whole_ceiling(int attempt)
    {
        var ceiling = Defaults.NextDelay(attempt, random: 1);

        Defaults.NextDelay(attempt, random: 0).ShouldBe(ceiling / 2);
        Defaults.NextDelay(attempt, random: 0.5).ShouldBeInRange(ceiling / 2, ceiling);
        Defaults.NextDelay(attempt, random: -3).ShouldBe(ceiling / 2, "an out-of-range random is clamped");
        Defaults.NextDelay(attempt, random: 7).ShouldBe(ceiling);
    }

    [Fact]
    public void It_gives_up_exactly_at_MaxAttempts()
    {
        Defaults.IsExhausted(Defaults.MaxAttempts - 1).ShouldBeFalse();
        Defaults.IsExhausted(Defaults.MaxAttempts).ShouldBeTrue();
        Should.Throw<ArgumentOutOfRangeException>(() => Defaults.NextDelay(0, 0.5));
    }

    [Fact]
    public void A_delivery_keeps_being_retried_for_most_of_a_day()
    {
        // The waits between the 30 attempts: 29 delays. Equal jitter puts the total between half and all of the ceilings.
        var longest = Enumerable.Range(1, Defaults.MaxAttempts - 1).Aggregate(TimeSpan.Zero, (sum, attempt) => sum + Defaults.NextDelay(attempt, random: 1));
        var shortest = Enumerable.Range(1, Defaults.MaxAttempts - 1).Aggregate(TimeSpan.Zero, (sum, attempt) => sum + Defaults.NextDelay(attempt, random: 0));

        longest.ShouldBeInRange(TimeSpan.FromHours(22), TimeSpan.FromHours(24));
        shortest.ShouldBeGreaterThan(TimeSpan.FromHours(11));
    }

    [Fact]
    public void The_numbers_come_from_the_options()
    {
        var policy = EmailRetryPolicy.From(new EmailDeliveryOptions { BaseDelay = TimeSpan.FromSeconds(1), MaxDelay = TimeSpan.FromSeconds(5), MaxAttempts = 3 });

        policy.NextDelay(1, random: 1).ShouldBe(TimeSpan.FromSeconds(1));
        policy.NextDelay(4, random: 1).ShouldBe(TimeSpan.FromSeconds(5));
        policy.IsExhausted(3).ShouldBeTrue();
    }

    [Theory]
    [InlineData(0, 3600, 30)]
    [InlineData(30, 3600, 0)]
    [InlineData(60, 30, 30)]
    public void Options_reject_a_schedule_that_cannot_work(int baseSeconds, int maxSeconds, int maxAttempts)
    {
        var options = new EmailDeliveryOptions { BaseDelay = TimeSpan.FromSeconds(baseSeconds), MaxDelay = TimeSpan.FromSeconds(maxSeconds), MaxAttempts = maxAttempts };

        Validator.TryValidateObject(options, new ValidationContext(options), [], validateAllProperties: true).ShouldBeFalse();
        var defaults = new EmailDeliveryOptions();
        Validator.TryValidateObject(defaults, new ValidationContext(defaults), [], validateAllProperties: true).ShouldBeTrue();
    }

    // ---------------------------------------------------------------- what kind of failure

    public static TheoryData<Exception> RenderFailures() =>
    [
        new ArgumentException("Unknown email template 'x'."),
        new ScriptRuntimeException(new SourceSpan(), "missing member 'name'"),
        new JsonException("bad model"),
        new InvalidOperationException("Email template 'x' is invalid"),
    ];

    [Theory]
    [MemberData(nameof(RenderFailures))]
    public void A_template_or_model_that_cannot_render_is_permanent(Exception error) =>
        EmailFailures.OfRender(error).ShouldBe(EmailFailureKind.Permanent);

    public static TheoryData<Exception> PermanentSendFailures() =>
    [
        new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxUnavailable, "550 no such user"),
        new SmtpCommandException(SmtpErrorCode.MessageNotAccepted, SmtpStatusCode.TransactionFailed, "554 rejected as spam"),
        new ParseException("bad address", 0, 3),
    ];

    [Theory]
    [MemberData(nameof(PermanentSendFailures))]
    public void A_5xx_rejection_of_this_email_or_a_malformed_address_is_permanent(Exception error) =>
        EmailFailures.OfSend(error).ShouldBe(EmailFailureKind.Permanent);

    public static TheoryData<Exception> TransportFailures() =>
    [
        new SocketException((int)SocketError.ConnectionRefused),
        new IOException("connection reset"),
        new TimeoutException("send timed out"),
        new SmtpProtocolException("unexpected reply"),
        new ServiceNotConnectedException("not connected"),
        new MailKit.Security.AuthenticationException("535 bad credentials"),
        new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxBusy, "450 try later"),
        new SmtpCommandException(SmtpErrorCode.MessageNotAccepted, SmtpStatusCode.ErrorInProcessing, "451 local error"),
        new SmtpCommandException(SmtpErrorCode.UnexpectedStatusCode, SmtpStatusCode.ServiceNotAvailable, "421 closing"),
        // The server refuses OUR sender or the session (auth required): every email would fail the same way, so it is an outage of
        // the sending side, not a reason to drop this one delivery.
        new SmtpCommandException(SmtpErrorCode.SenderNotAccepted, SmtpStatusCode.MailboxNameNotAllowed, "553 sender rejected"),
        new SmtpCommandException(SmtpErrorCode.UnexpectedStatusCode, SmtpStatusCode.AuthenticationRequired, "530 auth required"),
    ];

    [Theory]
    [MemberData(nameof(TransportFailures))]
    public void Connection_timeout_protocol_and_4xx_failures_are_transport_failures(Exception error) =>
        EmailFailures.OfSend(error).ShouldBe(EmailFailureKind.Transport);

    [Fact]
    public void Any_other_send_failure_is_retried_without_counting_towards_the_outage_breaker() =>
        EmailFailures.OfSend(new InvalidOperationException("550 mailbox unavailable")).ShouldBe(EmailFailureKind.Retryable);

    // ---------------------------------------------------------------- outage breaker backoff

    [Fact]
    public void The_outage_pause_doubles_from_five_seconds_up_to_the_cap()
    {
        var max = TimeSpan.FromMinutes(5);
        var pauses = new List<TimeSpan>();
        var pause = TimeSpan.Zero;
        for (var i = 0; i < 9; i++)
        {
            pause = EmailOutageBackoff.Next(pause, max);
            pauses.Add(pause);
        }

        pauses.Select(p => p.TotalSeconds).ShouldBe([5, 10, 20, 40, 80, 160, 300, 300, 300]);
    }
}
