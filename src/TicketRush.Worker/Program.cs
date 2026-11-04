using TicketRush.Application;
using TicketRush.Infrastructure;
using TicketRush.Worker.Inventory;
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
builder.Services.AddSingleton<LiveInventoryEvents>();
builder.Services.AddHostedService<InventoryReconcilerService>();

await builder.Build().RunAsync();
