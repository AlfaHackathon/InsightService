using AlfaHackaton.Result;
using AlfaHackaton.Result.Errors;
using Xunit;

internal static class ResultAssert
{
    public static T RequireContent<T>(this Result<T> result)
    {
        Assert.Null(result.Error);
        return result.Content!;
    }
    public static async Task<T> RequireContentAsync<T>(this Task<Result<T>> task) => (await task).RequireContent();
    public static async Task<Error> ErrorAsync<T>(Func<Task<Result<T>>> action) => Assert.IsType<Error>((await action()).Error);
    public static Error Error<T>(Func<Result<T>> action) => Assert.IsType<Error>(action().Error);
}
