var builder = DistributedApplication.CreateBuilder(args);

// Secrets are generated once and persisted to the AppHost's user-secrets, never committed.
var jwtSigningKey = builder.AddParameter(
    "jwt-signing-key",
    new GenerateParameterDefault { MinLength = 64, Special = false },
    secret: true,
    persist: true);

// Signs admission tokens (spec 06); must differ from the access-token key.
var admissionTokenSigningKey = builder.AddParameter(
    "admission-token-signing-key",
    new GenerateParameterDefault { MinLength = 64, Special = false },
    secret: true,
    persist: true);

// Presale codes are stored as SHA-256(pepper ‖ code) (spec 05).
var accessCodePepper = builder.AddParameter(
    "access-code-pepper",
    new GenerateParameterDefault { MinLength = 48, Special = false },
    secret: true,
    persist: true);

// Shared by the web's POST /api/revalidate and the worker that calls it (spec 05).
var revalidateSecret = builder.AddParameter(
    "revalidate-secret",
    new GenerateParameterDefault { MinLength = 48, Special = false },
    secret: true,
    persist: true);

var postgres = builder.AddPostgres("postgres")
    .WithImageTag("17")
    .WithDataVolume("ticketrush-postgres-data")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithPgAdmin();
var database = postgres.AddDatabase("ticketrush");

var redis = builder.AddRedis("redis")
    .WithImageTag("7.4")
    .WithDataVolume("ticketrush-redis-data")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithRedisInsight();

var mailpit = builder.AddMailPit("mailpit");

var migrations = builder.AddProject<Projects.TicketRush_MigrationService>("migrations")
    .WithReference(database)
    .WaitFor(database)
    .WithEnvironment("Migrations__SeedDevData", "true")
    .WithEnvironment("Catalog__AccessCodePepper", accessCodePepper);

var api = builder.AddProject<Projects.TicketRush_Api>("api")
    .WithReference(database)
    .WithReference(redis)
    .WithReference(mailpit)
    .WithEnvironment("Jwt__SigningKey", jwtSigningKey)
    .WithEnvironment("AdmissionToken__SigningKey", admissionTokenSigningKey)
    .WithEnvironment("Catalog__AccessCodePepper", accessCodePepper)
    .WaitFor(redis)
    .WaitForCompletion(migrations)
    .WithHttpHealthCheck("/alive");

var worker = builder.AddProject<Projects.TicketRush_Worker>("worker")
    .WithReference(database)
    .WithReference(redis)
    .WithReference(mailpit)
    .WithEnvironment("Jwt__SigningKey", jwtSigningKey)
    .WithEnvironment("AdmissionToken__SigningKey", admissionTokenSigningKey)
    .WithEnvironment("Catalog__AccessCodePepper", accessCodePepper)
    .WaitFor(redis)
    .WaitForCompletion(migrations);

var web = builder.AddJavaScriptApp("web", "../../web", "dev")
    .WithHttpEndpoint(env: "PORT")
    // next.config.ts rewrites /api/* to this URL, so the browser only ever talks to the web origin.
    .WithEnvironment("API_URL", api.GetEndpoint("http"))
    .WithEnvironment("REVALIDATE_SECRET", revalidateSecret)
    .WithReference(api)
    .WaitFor(api)
    .WithExternalHttpEndpoints();

api.WithEnvironment("Frontend__BaseUrl", web.GetEndpoint("http"));
// Mock payment gateway (spec 08, dev only): the redirect goes to the web page, the worker delivers webhooks to the api.
api.WithEnvironment("Payments__Mock__WebBaseUrl", web.GetEndpoint("http"));
worker
    .WithEnvironment("Payments__Mock__WebhookUrl", ReferenceExpression.Create($"{api.GetEndpoint("http")}/api/v1/webhooks/payments/mock"))
    .WithEnvironment("Frontend__RevalidateUrl", ReferenceExpression.Create($"{web.GetEndpoint("http")}/api/revalidate"))
    .WithEnvironment("Frontend__RevalidateSecret", revalidateSecret);

builder.Build().Run();
