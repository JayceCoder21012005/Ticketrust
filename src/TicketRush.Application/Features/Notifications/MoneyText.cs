using System.Globalization;
using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Notifications;

/// <summary>Money as the buyer reads it in notices and emails. Culture independent, so it never depends on the host's ICU data.</summary>
public static class MoneyText
{
    /// <summary>Whole dong with dots, like the web (<c>1.250.000 ₫</c>); another currency shows its code.</summary>
    public static string Format(long amount, string currency)
    {
        var digits = amount.ToString("N0", CultureInfo.InvariantCulture).Replace(',', '.');
        return string.Equals(currency, Money.DefaultCurrency, StringComparison.OrdinalIgnoreCase) ? $"{digits} ₫" : $"{digits} {currency}";
    }
}
