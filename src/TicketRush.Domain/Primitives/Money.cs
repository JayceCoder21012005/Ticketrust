namespace TicketRush.Domain.Primitives;

/// <summary>
/// Monetary amount in the currency's smallest unit. VND has no minor unit, so <see cref="Amount"/>
/// is whole dong and stored as <c>bigint</c>. Never use floating point for money.
/// </summary>
public sealed record Money
{
    public const string DefaultCurrency = "VND";

    public Money(long amount, string currency = DefaultCurrency)
    {
        if (amount < 0)
        {
            throw new DomainException("money.negative", "Money cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
        {
            throw new DomainException("money.invalid_currency", "Currency must be an ISO 4217 code.");
        }

        Amount = amount;
        Currency = currency.ToUpperInvariant();
    }

    public long Amount { get; }

    public string Currency { get; }

    public static Money Zero(string currency = DefaultCurrency) => new(0, currency);

    public static Money operator +(Money left, Money right) => Add(left, right);

    public static Money operator *(Money money, int quantity) => Multiply(money, quantity);

    public static Money Add(Money left, Money right)
    {
        if (!string.Equals(left.Currency, right.Currency, StringComparison.Ordinal))
        {
            throw new DomainException("money.currency_mismatch", "Cannot combine different currencies.");
        }

        return new Money(checked(left.Amount + right.Amount), left.Currency);
    }

    public static Money Multiply(Money money, int quantity) =>
        new(checked(money.Amount * quantity), money.Currency);

    public override string ToString() => $"{Amount} {Currency}";
}
