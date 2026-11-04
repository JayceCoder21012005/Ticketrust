namespace TicketRush.Application.Abstractions.Security;

/// <summary>Presale codes are stored only as a peppered hash (spec 05); the plain code is never kept.</summary>
public interface IAccessCodeHasher
{
    /// <summary>64 lowercase hex chars. Case and surrounding whitespace don't matter: buyers type codes by hand.</summary>
    string Hash(string code);
}
