using Npgsql;

namespace TicketRush.Infrastructure.Persistence;

/// <summary>Recognises the Postgres errors the use cases react to. Only Infrastructure looks at Npgsql exceptions.</summary>
internal static class PostgresErrors
{
    private const string UniqueViolation = "23505";
    private const string DeadlockDetected = "40P01";
    private const string SerializationFailure = "40001";

    /// <summary>A unique violation of exactly <paramref name="constraint"/> (index or key name), directly or wrapped by EF.</summary>
    public static bool IsUniqueViolation(Exception exception, string constraint) =>
        Find(exception) is { SqlState: UniqueViolation } postgres && string.Equals(postgres.ConstraintName, constraint, StringComparison.Ordinal);

    /// <summary>The transaction lost a deadlock or serialization race; running it again may succeed.</summary>
    public static bool IsContention(Exception exception) =>
        Find(exception) is { SqlState: DeadlockDetected or SerializationFailure };

    private static PostgresException? Find(Exception? exception)
    {
        for (; exception is not null; exception = exception.InnerException)
        {
            if (exception is PostgresException postgres)
            {
                return postgres;
            }
        }

        return null;
    }
}
