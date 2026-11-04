using TicketRush.Application;
using TicketRush.Infrastructure;
using TicketRush.Infrastructure.Payments;
using TicketRush.Infrastructure.Realtime;
using TicketRush.Worker.Inventory;
using TicketRush.Worker.Outbox;
using TicketRush.Worker.Payments;
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
builder.Services.AddHostedService<HoldExpiryService>();
builder.Services.AddTicketRushSignalR(builder.Configuration);
builder.Services.AddHostedService<SeatDeltaBroadcaster>();
builder.Services.AddHostedService<QueueRoomStatusService>();

// Dev/demo only: delivers the mock payment gateway's signed webhooks (spec 08). Off unless Payments:Mock:Enabled.
if (builder.Configuration.GetValue<bool>($"{MockGatewayOptions.SectionName}:Enabled"))
{
    builder.Services.AddHostedService<MockWebhookDeliveryService>();
}

await builder.Build().RunAsync();
