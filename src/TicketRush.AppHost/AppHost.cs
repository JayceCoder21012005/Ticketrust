var builder = DistributedApplication.CreateBuilder(args);

// Secrets are generated once and persisted to the AppHost's user-secrets, never committed.
var jwtSigningKey = builder.AddParameter(
    "jwt-signing-key",
    new GenerateParameterDefault { MinLength = 64, Special = false },
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
    .WithEnvironment("Migrations__SeedDevData", "true");

var api = builder.AddProject<Projects.TicketRush_Api>("api")
    .WithReference(database)
    .WithReference(redis)
    .WithReference(mailpit)
    .WithEnvironment("Jwt__SigningKey", jwtSigningKey)
    .WaitFor(redis)
    .WaitForCompletion(migrations)
    .WithHttpHealthCheck("/alive");

builder.AddProject<Projects.TicketRush_Worker>("worker")
    .WithReference(database)
    .WithReference(redis)
    .WithReference(mailpit)
    .WithEnvironment("Jwt__SigningKey", jwtSigningKey)
    .WaitFor(redis)
    .WaitForCompletion(migrations);

var web = builder.AddJavaScriptApp("web", "../../web", "dev")
    .WithHttpEndpoint(env: "PORT")
    // next.config.ts rewrites /api/* to this URL, so the browser only ever talks to the web origin.
    .WithEnvironment("API_URL", api.GetEndpoint("http"))
    .WithReference(api)
    .WaitFor(api)
    .WithExternalHttpEndpoints();

api.WithEnvironment("Frontend__BaseUrl", web.GetEndpoint("http"));

builder.Build().Run();
