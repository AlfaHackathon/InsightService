using System.Net;
using AlfaHackaton.Controllers.Controllers;
using AlfaHackaton.Result.Errors;
using InsightService.Contracts.Calculations;
using InsightService.Services;
using Microsoft.AspNetCore.Mvc;
namespace InsightService.Controllers;

[ApiController]
[Route("insight-service/internal/calculations")]
public sealed class CalculationsController : BaseController
{
    [HttpPost("preview")]
    public IActionResult Preview([FromBody] Calculation[] calculations)
    {
        if (calculations.Length > 32)
        {
            return OkOrProblem<object>(new(new Error("At most 32 calculations may be previewed.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.BadRequest }));
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
                errorMessage = error.Title
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
