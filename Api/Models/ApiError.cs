using System.Net;
using AlfaHackaton.Result.Errors;
namespace InsightService.Models;

public sealed class ApiError : Error
{
    public ApiError(int statusCode, string code, string message)
        : base(message,
            statusCode >= 500 ? ErrorSource.External : ErrorSource.Client,
            statusCode switch
            {
                400 or 409 or 422 => ErrorType.CorruptedInfo,
                401 => ErrorType.Unauthorized,
                403 => ErrorType.NoRights,
                404 => ErrorType.NotFound,
                _ => ErrorType.Undefined
            })
    {
        Code = code;
        HttpStatusCode = (HttpStatusCode)statusCode;
    }

    public string Code
    {
        get;
    }
    public int StatusCode => (int)HttpStatusCode!.Value;
}
