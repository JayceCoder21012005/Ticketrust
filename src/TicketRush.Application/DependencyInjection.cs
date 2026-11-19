using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TicketRush.Application.Abstractions.Messaging;
using TicketRush.Domain.Primitives;

namespace TicketRush.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly, includeInternalTypes: true);
        services.TryAddSingleton(TimeProvider.System);
        AddUseCaseHandlers(services);
        services.AddScoped<Features.Notifications.OrderNoticeSender>();
        services.TryAddSingleton<Features.Holds.IBestAvailablePicker, Features.Holds.BestAvailableSeatPicker>();
        services.AddScoped<Features.Waitlist.WaitlistOffers>();
        return services;
    }

    /// <summary>Registers an outbox consumer. One event type may have several handlers.</summary>
    public static IServiceCollection AddIntegrationEventHandler<TEvent, THandler>(this IServiceCollection services)
        where TEvent : IDomainEvent
        where THandler : class, IIntegrationEventHandler<TEvent>
    {
        services.AddScoped<THandler>();
        services.AddSingleton(new IntegrationEventHandlerRegistration(
            typeof(TEvent),
            typeof(THandler),
            (provider, message, context, ct) =>
                provider.GetRequiredService<THandler>().HandleAsync((TEvent)message, context, ct)));
        return services;
    }

    /// <summary>Every <c>*Handler</c> class under <c>Features</c> is a scoped use case.</summary>
    private static void AddUseCaseHandlers(IServiceCollection services)
    {
        var handlers = typeof(DependencyInjection).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false }
                && type.Name.EndsWith("Handler", StringComparison.Ordinal)
                && type.Namespace?.StartsWith("TicketRush.Application.Features", StringComparison.Ordinal) == true);

        foreach (var handler in handlers)
        {
            services.TryAddScoped(handler);
        }
    }
}
