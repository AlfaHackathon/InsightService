using InsightService.Contracts.Calculations;
using InsightService.Models;
using InsightService.Services;
using Microsoft.AspNetCore.Mvc;
namespace InsightService.Controllers;

[ApiController]
[Route("insight-service/internal/calculations")]
public sealed class CalculationsController : ApiControllerBase
{
    [HttpPost("preview")]
    public IActionResult Preview([FromBody] Calculation[] calculations)
    {
        if (calculations.Length > 32)
        {
            return OkOrProblem<object>(new(new ApiError(400, "calculation_budget", "At most 32 calculations may be previewed.")));
        }
        return Ok(new
        {
            results = calculations.Select(PreviewCalculation).ToArray()
        });
    }
    private static object PreviewCalculation(Calculation calculation)
    {
        var result = DecimalCalculator.Calculate(calculation);
        if (result.Error is { } error)
        {
            return new
            {
                valid = false,
                code = error is ApiError apiError ? apiError.Code : "invalid_calculation",
                message = error.Title
            };
        }
        return new
        {
            valid = true,
            result = result.Content.Result,
            unit = calculation.Unit,
            period = calculation.Period,
            arithmeticOnly = true
        };
    }
}
