using System.Diagnostics.CodeAnalysis;

namespace TicketRush.Domain.Primitives;

/// <summary>Outcome of a use case. Expected failures are values, not exceptions.</summary>
public class Result
{
    protected Result(Error? error) => Error = error;

    public Error? Error { get; }

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    [MemberNotNullWhen(true, nameof(Error))]
    public bool IsFailure => !IsSuccess;

    public static Result Success() => new(null);

    public static Result Failure(Error error) => new(error);

    public static Result<T> Success<T>(T value) => Result<T>.Success(value);

    public static Result<T> Failure<T>(Error error) => Result<T>.Failure(error);

#pragma warning disable CA2225 // Failure(Error) is the named alternative.
    public static implicit operator Result(Error error) => Failure(error);
#pragma warning restore CA2225
}

public sealed class Result<T> : Result
{
    private readonly T? _value;

    private Result(T? value, Error? error)
        : base(error) => _value = value;

    /// <summary>The value of a successful result. Throws when read on a failure.</summary>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Cannot read the value of a failed result ({Error.Code}).");

    public static Result<T> Success(T value) => new(value, null);

    public static new Result<T> Failure(Error error) => new(default, error);

#pragma warning disable CA2225 // Success/Failure are the named alternatives.
    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(Error error) => Failure(error);
#pragma warning restore CA2225
}
