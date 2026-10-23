using AlfaHackaton.Controllers.Controllers;
using InsightService.Contracts.Calculations;
using InsightService.Models;
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
        if (calculations.Length > 32) throw new ApiException(400, "calculation_budget", "At most 32 calculations may be previewed.");
        return Ok(new
        {
            results = calculations.Select(c =>
        {
            try { var result = DecimalCalculator.Calculate(c); return (object)new { valid = true, result = result.Result, unit = c.Unit, period = c.Period, arithmeticOnly = true }; }
            catch (ApiException e) { return new { valid = false, code = e.Code, message = e.Message }; }
        }).ToArray()
        });
    }
}
