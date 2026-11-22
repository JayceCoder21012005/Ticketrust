namespace TicketRush.Api.IntegrationTests.Infrastructure;

/// <summary>Production reverse proxy (spec 07): SignalR must reach the api directly, not through Next's rewrites.</summary>
public sealed class DeployConfigTests
{
    [Fact]
    public void Caddy_routes_hubs_straight_to_the_api_before_the_web_app()
    {
        // `next start` in standalone output does not proxy WebSocket upgrades (vercel/next.js#44209).
        var text = DeployFiles.Caddyfile();
        var hubs = text.IndexOf("handle /hubs/*", StringComparison.Ordinal);
        var api = text.IndexOf("reverse_proxy api:8080", StringComparison.Ordinal);
        var web = text.IndexOf("reverse_proxy web:3000", StringComparison.Ordinal);

        hubs.ShouldBeGreaterThanOrEqualTo(0);
        api.ShouldBeGreaterThan(hubs);
        web.ShouldBeGreaterThan(api);
    }

    [Fact]
    public void Caddy_routes_payment_webhooks_straight_to_the_api_before_the_web_app()
    {
        // The webhook signature covers the exact body bytes. Through the Next rewrite, a body just over the 64 KB limit sometimes came back 502
        // instead of 413 ("Failed to proxy ... EPIPE": the api answers and closes while Next is still writing), so the route bypasses web.
        var text = DeployFiles.Caddyfile();
        var handle = text.IndexOf("handle /api/v1/webhooks/*", StringComparison.Ordinal);
        var web = text.IndexOf("reverse_proxy web:3000", StringComparison.Ordinal);

        handle.ShouldBeGreaterThanOrEqualTo(0);
        var api = text.IndexOf("reverse_proxy api:8080", handle, StringComparison.Ordinal);
        var close = text.IndexOf('}', handle);
        api.ShouldBeGreaterThan(handle);
        api.ShouldBeLessThan(close, "the api upstream must be inside the webhook handle block");
        web.ShouldBeGreaterThan(close);
    }

    [Fact]
    public void The_production_compose_points_the_mock_redirect_at_the_public_host()
    {
        // Without it the mock session's redirectUrl defaults to http://localhost:3000, unreachable for a real buyer.
        var compose = RepoFiles.Read("docker-compose.prod.yml");

        compose.ShouldContain("Payments__Mock__WebBaseUrl: https://${PUBLIC_HOST}");
    }

    [Fact]
    public void The_production_compose_can_switch_to_MoMo_with_keys_from_the_env_file_only()
    {
        // Spec 18: the provider and MoMo's keys come from .env; the IPN goes to the public host, which Caddy routes to the api.
        var compose = RepoFiles.Read("docker-compose.prod.yml");
        compose.ShouldContain("Payment__Provider: ${PAYMENT_PROVIDER:-Mock}");
        compose.ShouldContain("Payments__MoMo__SecretKey: ${MOMO_SECRET_KEY:-}");
        compose.ShouldContain("Payments__MoMo__AccessKey: ${MOMO_ACCESS_KEY:-}");
        compose.ShouldContain("Payments__MoMo__PartnerCode: ${MOMO_PARTNER_CODE:-}");
        compose.ShouldContain("Payments__MoMo__IpnUrl: ${MOMO_IPN_URL:-https://${PUBLIC_HOST}/api/v1/webhooks/payments/momo}");
        compose.ShouldContain("NEXT_PUBLIC_PAYMENT_PROVIDER: ${WEB_PAYMENT_PROVIDER:-mock}");

        // .env.example may name MoMo's public sandbox keys in comments, never set a secret.
        var example = RepoFiles.Read(".env.example");
        example.ShouldContain("MOMO_SECRET_KEY=");
        foreach (var line in example.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("MOMO_SECRET_KEY=", StringComparison.Ordinal)))
        {
            line.ShouldBe("MOMO_SECRET_KEY=");
        }
    }
}
