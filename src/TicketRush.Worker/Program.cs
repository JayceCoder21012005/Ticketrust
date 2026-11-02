using TicketRush.Application;
using TicketRush.Infrastructure;
using TicketRush.Worker.Outbox;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddInfrastructure();
builder.Services.AddApplication();

// Each spec registers its own BackgroundService here.
builder.Services.AddHostedService<OutboxDispatcherService>();

await builder.Build().RunAsync();
