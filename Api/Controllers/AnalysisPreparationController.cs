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
    [ProducesResponseType(typeof(AnalysisReceipt), StatusCodes.Status200OK)]
    public async Task<IActionResult> CanonicalAsync([FromBody] AnalysisScope request)
    {
        return OkOrProblem<AnalysisReceipt>(await service.CanonicalCompanyAsync(request));
    }
    [HttpPost("applicability")]
    [ProducesResponseType(typeof(AnalysisReceipt), StatusCodes.Status200OK)]
    public async Task<IActionResult> ApplicabilityAsync([FromBody] AnalysisScope request, CancellationToken ct)
    {
        return OkOrProblem<AnalysisReceipt>(await service.ApplicabilityAsync(request, ct));
    }
    [HttpPost("prepare")]
    [ProducesResponseType(typeof(DraftPreparationResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> PrepareAsync([FromBody] DraftPreparation request)
    {
        return OkOrProblem<DraftPreparationResponse>(await service.PrepareAsync(request));
    }
    [HttpPost("depth")]
    public async Task<IActionResult> DepthAsync([FromBody] DepthRequest request, CancellationToken ct)
    {
        return OkOrProblem<object>(await service.DepthAsync(request, ct));
    }
    [HttpPost("validate-applicability")]
    public async Task<IActionResult> ValidateApplicabilityAsync([FromBody] ApplicabilityValidation request, CancellationToken ct)
    {
        return OkOrProblem<object>(await service.ValidateApplicabilityAsync(request, ct));
    }
}
