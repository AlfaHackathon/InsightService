using System.Net;
using AlfaHackaton.Result;
using AlfaHackaton.Result.Errors;

namespace InsightService.Common;

public static class RequestValidation
{
    public static Result RequireCompany(Guid id)
    {
        if (id == Guid.Empty)
        {
            return new(new Error("companyId must be a nonempty application Guid.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.BadRequest });
        }

        return new();
    }

    public static Result RequireId(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 200 ||
            value != value.Trim() ||
            value.Any(c => char.IsControl(c) ||
                c is '/' or '\\'))
        {
            return new(new Error($"{name} must be a nonempty bounded path-safe identifier.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.BadRequest });
        }

        return new();
    }
}
