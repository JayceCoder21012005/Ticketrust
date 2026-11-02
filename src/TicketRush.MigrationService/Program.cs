using TicketRush.Infrastructure;
using TicketRush.MigrationService;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddPersistence();
builder.Services.Configure<MigrationOptions>(builder.Configuration.GetSection(MigrationOptions.SectionName));
builder.Services.AddHostedService<MigrationWorker>();

await builder.Build().RunAsync();
