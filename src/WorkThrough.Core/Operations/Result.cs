using System.Diagnostics.CodeAnalysis;

namespace WorkThrough;

public enum ErrorType
{
    Validation, NotFound, Conflict
}

/// <summary>An expected operator-command failure. Messages are safe to show to the authorized operator.</summary>
public sealed record Error(ErrorType Type, string Message)
{
    public static Error Validation(string message) => new(ErrorType.Validation, message);
    public static Error NotFound(string message) => new(ErrorType.NotFound, message);
    public static Error Conflict(string message) => new(ErrorType.Conflict, message);
}

/// <summary>Outcome of an audited operator command. A failure committed no change, receipt or audit entry.</summary>
public class Result
{
    private protected Result(Error? error) => Error = error;

    public Error? Error { get; }

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    public static Result Success() => new(null);
    public static Result Failure(Error error) => new(error ?? throw new ArgumentNullException(nameof(error)));
    public static Result<T> Success<T>(T value) => new(value, null);
    public static Result<T> Failure<T>(Error error) => new(default, error ?? throw new ArgumentNullException(nameof(error)));
}

public sealed class Result<T> : Result
{
    private readonly T? value;

    internal Result(T? value, Error? error)
        : base(error)
    {
        this.value = value;
    }

    public T Value => IsSuccess ? value! : throw new InvalidOperationException($"A failed result has no value: {Error.Message}");
}
