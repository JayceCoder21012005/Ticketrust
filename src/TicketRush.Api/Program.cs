using TicketRush.Api;
using TicketRush.Application;
using TicketRush.Infrastructure;
using TicketRush.Infrastructure.Realtime;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddInfrastructure();
builder.Services.AddApplication();
builder.Services.AddApi();
builder.Services.AddTicketRushSignalR(builder.Configuration);

var app = builder.Build();

app.UseApiPipeline();
app.MapDefaultEndpoints();
app.MapApiEndpoints();

await app.RunAsync();

/// <summary>Entry point marker for <c>WebApplicationFactory</c>.</summary>
public partial class Program;
