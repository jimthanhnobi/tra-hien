namespace TeaPremium.Application.Results;

public sealed class Result<T>
{
    private readonly T? _value;
    private readonly Error? _error;

    internal Result(T value)
    {
        _value = value;
        IsSuccess = true;
    }

    internal Result(Error error)
    {
        _error = error;
        IsSuccess = false;
    }

    public bool IsSuccess { get; }

    public T Value => IsSuccess ? _value! : throw new InvalidOperationException("Result has no value.");

    public Error Error => !IsSuccess ? _error! : throw new InvalidOperationException("Result has no error.");

}

public static class Result
{
    public static Result<T> Success<T>(T value) => new(value);

    public static Result<T> Failure<T>(Error error) => new(error ?? throw new ArgumentNullException(nameof(error)));
}
