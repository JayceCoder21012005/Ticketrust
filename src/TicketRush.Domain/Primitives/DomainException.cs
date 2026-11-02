namespace TicketRush.Domain.Primitives;

/// <summary>
/// Thrown when code tries to break an aggregate invariant (e.g. an illegal state transition).
/// It signals a programming error or a lost race; expected failures are returned as <see cref="Result"/>.
/// </summary>
public sealed class DomainException : Exception
{
    private const string DefaultCode = "domain.invariant_violated";

    public DomainException(string code, string message)
        : base(message) => Code = code;

    public DomainException()
        : this(DefaultCode, "A domain invariant was violated.")
    {
    }

    public DomainException(string message)
        : this(DefaultCode, message)
    {
    }

    public DomainException(string message, Exception innerException)
        : base(message, innerException) => Code = DefaultCode;

    public string Code { get; }
}
