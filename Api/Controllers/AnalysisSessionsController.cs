using AlfaHackaton.Controllers.Controllers;
using InsightService.Contracts.Analyses;
using InsightService.Services;
using Microsoft.AspNetCore.Mvc;

namespace InsightService.Controllers;

[ApiController]
[Route("insight-service/internal/analyses/session")]
public class AnalysisSessionsController(AnalysisSessionService sessions) : BaseController
{
    [HttpPost]
    [ProducesResponseType(typeof(AnalysisSessionResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> OpenAsync([FromBody] AnalysisRequest request)
    {
        return OkOrProblem(await sessions.OpenAsync(request));
    }
}
