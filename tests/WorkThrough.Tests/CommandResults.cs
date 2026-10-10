using Xunit;

namespace WorkThrough.Tests;

/// <summary>Asserts audited operator-command outcomes.</summary>
internal static class CommandResults
{
    public static async Task SucceedsAsync(this Task<Result> command)
    {
        var result = await command;
        Assert.True(result.IsSuccess, result.Error?.Message);
    }

    public static async Task<T> SucceedsAsync<T>(this Task<Result<T>> command)
    {
        var result = await command;
        Assert.True(result.IsSuccess, result.Error?.Message);

        return result.Value;
    }

    public static async Task<Error> FailsAsync(this Task<Result> command, ErrorType type) => AssertFailure(await command, type);

    public static async Task<Error> FailsAsync<T>(this Task<Result<T>> command, ErrorType type) => AssertFailure(await command, type);

    private static Error AssertFailure(Result result, ErrorType type)
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(type, result.Error.Type);

        return result.Error;
    }
}
