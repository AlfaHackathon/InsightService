using AlfaHackaton.Controllers.Controllers;
using InsightService.Contracts.Execution;
using InsightService.Services;
using Microsoft.AspNetCore.Mvc;

namespace InsightService.Controllers;

[ApiController]
[Route("insight-service/internal/projections/job")]
public sealed class JobProjectionController(JobProjectionService service) : BaseController
{
    [HttpPost]
    public async Task<IActionResult> ProjectAsync([FromBody] JobProjectionRequest request, CancellationToken ct)
    {
        return OkOrProblem<JobProjectionResponse>(await service.ProjectAsync(request, ct));
    }
}
