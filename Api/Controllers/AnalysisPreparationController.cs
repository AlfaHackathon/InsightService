using AlfaHackaton.Controllers.Controllers;
using InsightService.Contracts.Analyses;
using InsightService.Services;
using Microsoft.AspNetCore.Mvc;

namespace InsightService.Controllers;

[ApiController]
[Route("insight-service/internal/analyses")]
public sealed class AnalysisPreparationController(AnalysisPreparationService service) : BaseController
{
    [HttpPost("canonical-company")]
    public async Task<IActionResult> Canonical([FromBody] AnalysisScope request) => OkOrProblem<AnalysisReceipt>(await service.CanonicalCompany(request));
    [HttpPost("applicability")]
    public async Task<IActionResult> Applicability([FromBody] AnalysisScope request, CancellationToken ct) => OkOrProblem<AnalysisReceipt>(await service.Applicability(request, ct));
    [HttpPost("prepare")]
    public async Task<IActionResult> Prepare([FromBody] DraftPreparation request)
    {
        var result = await service.Prepare(request);
        return result.Error is null ? Ok(new
        {
            drafts = result.Content
        }) : OkOrProblem(result);
    }
    [HttpPost("depth")]
    public async Task<IActionResult> Depth([FromBody] DepthRequest request, CancellationToken ct) => OkOrProblem<object>(await service.Depth(request, ct));
    [HttpPost("validate-applicability")]
    public async Task<IActionResult> ValidateApplicability([FromBody] ApplicabilityValidation request, CancellationToken ct) => OkOrProblem<object>(await service.ValidateApplicability(request, ct));
}
