namespace TicketRush.Application.Abstractions.Notifications;

public sealed record RenderedEmail(string Subject, string Html, string Text)
{
    public EmailMessage ToMessage(Guid messageId, string to) => new(messageId, to, Subject, Html, Text);
}

/// <summary>Renders one of the email templates (<c>confirm-email</c>, <c>order-paid</c>, ...) with a model of camelCase members.</summary>
public interface IEmailRenderer
{
    RenderedEmail Render(string template, object model);
}
