using AlfaHackaton.Result;
using InsightService.Models;

namespace InsightService.Common;

public static class RequestValidation
{
    public static Result RequireCompany(Guid id)
    {
        if (id == Guid.Empty)
        {
            return new(new ApiError(400, "invalid_company", "companyId must be a nonempty application Guid."));
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
            return new(new ApiError(400, "invalid_id", $"{name} must be a nonempty bounded path-safe identifier."));
        }

        return new();
    }
}
