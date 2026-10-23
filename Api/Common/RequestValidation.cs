using InsightService.Models;
namespace InsightService.Common;

public static class RequestValidation
{
    public static void RequireCompany(Guid id)
    { if (id == Guid.Empty) throw new ApiException(400, "invalid_company", "companyId must be a nonempty application Guid."); }

    public static void RequireId(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200 || value != value.Trim() ||
            value.Any(c => char.IsControl(c) || c is '/' or '\\'))
            throw new ApiException(400, "invalid_id", $"{name} must be a nonempty bounded path-safe identifier.");
    }

}
