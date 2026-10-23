using AlfaHackaton.Result;
using InsightService.Models;
using Xunit;

internal static class ResultAssert
{
    public static T RequireContent<T>(this Result<T> result)
    {
        Assert.Null(result.Error);
        return result.Content!;
    }
    public static async Task<T> RequireContentAsync<T>(this Task<Result<T>> task) => (await task).RequireContent();
    public static async Task<ApiError> ErrorAsync<T>(Func<Task<Result<T>>> action) => Assert.IsType<ApiError>((await action()).Error);
    public static ApiError Error<T>(Func<Result<T>> action) => Assert.IsType<ApiError>(action().Error);
}
