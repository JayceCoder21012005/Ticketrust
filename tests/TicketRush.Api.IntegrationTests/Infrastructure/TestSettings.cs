namespace TicketRush.Api.IntegrationTests.Infrastructure;

/// <summary>Minimal configuration every host needs to pass options validation.</summary>
public static class TestSettings
{
    public static Dictionary<string, string?> For(string databaseConnectionString, string redisConnectionString) => new()
    {
        ["ConnectionStrings:ticketrush"] = databaseConnectionString,
        ["ConnectionStrings:redis"] = redisConnectionString,
        ["Redis:Environment"] = "test",
        ["Jwt:Issuer"] = "ticketrush-api",
        ["Jwt:Audience"] = "ticketrush-web",
        ["Jwt:SigningKey"] = "test-signing-key-that-is-long-enough-0123456789",
        ["AdmissionToken:SigningKey"] = "test-admission-key-long-enough-0123456789",
        ["Payment:Provider"] = "Mock",
        ["Payments:Mock:WebhookSecret"] = "test-mock-webhook-secret-0123456789",
        ["Email:FromAddress"] = "no-reply@ticketrush.dev",
        ["Email:FromName"] = "TicketRush",
        ["Storage:Provider"] = "Local",
        ["Storage:PublicBaseUrl"] = "http://localhost:3000/media",
        ["Frontend:BaseUrl"] = "http://localhost:3000",
        ["Catalog:AccessCodePepper"] = "test-access-code-pepper-0123456789abcdef",
    };
}
