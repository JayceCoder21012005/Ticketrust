using TicketRush.Application;
using TicketRush.Infrastructure;
using TicketRush.Worker.Outbox;
using TicketRush.Worker.Queue;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddInfrastructure();
builder.Services.AddApplication();

// Each spec registers its own BackgroundService here.
builder.Services.AddHostedService<OutboxDispatcherService>();
builder.Services.AddSingleton<LiveSales>();
builder.Services.AddHostedService<QueueOpenerService>();
builder.Services.AddHostedService<QueueAdmitterService>();

await builder.Build().RunAsync();
