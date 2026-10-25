using AlfaHackaton.Controllers.Controllers;
using InsightService.Contracts.Execution;
using InsightService.Services;
using Microsoft.AspNetCore.Mvc;

namespace InsightService.Controllers;

[ApiController]
[Route("insight-service/internal/execution")]
public sealed class ExecutionController(CompanyExecutionService companies, SourceJobPlanningService sources) : BaseController
{
    [HttpPost("company-plan")]
    [ProducesResponseType(typeof(CompanyPlanResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> CompanyPlanAsync([FromBody] CompanyPlanRequest request, CancellationToken ct)
    {
        return OkOrProblem<CompanyPlanResponse>(await companies.PlanAsync(request, ct));
    }
    [HttpPost("source-jobs")]
    [ProducesResponseType(typeof(SourceJobsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> SourceJobsAsync([FromBody] SourceJobsRequest request, CancellationToken ct)
    {
        return OkOrProblem<SourceJobsResponse>(await sources.SourceJobsAsync(request, ct));
    }
    [HttpPost("company-watch")]
    [ProducesResponseType(typeof(CompanyWatchResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> CompanyWatchAsync([FromBody] CompanyWatchRequest request, CancellationToken ct)
    {
        return OkOrProblem<CompanyWatchResponse>(await sources.CompanyWatchAsync(request, ct));
    }
}
