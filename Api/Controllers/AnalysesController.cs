using System.Net;
using AlfaHackaton.Controllers.Controllers;
using AlfaHackaton.Result.Errors;
using InsightService.Contracts.Analyses;
using InsightService.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
namespace InsightService.Controllers;

[ApiController]
[Route("insight-service/internal")]
public sealed class AnalysesController(IAnalysisService service) : BaseController
{
    [HttpPost("analyses")]
    public async Task<IActionResult> Save([FromBody] AnalysisRequest request, CancellationToken ct) => OkOrProblem<AnalysisReceipt>(await service.Save(request, ct));
    [HttpGet("analyses")]
    public async Task<IActionResult> Get([FromQuery, BindRequired] Guid companyId,
        [FromQuery, BindRequired] string newsMaterialId, [FromQuery, BindRequired] string newsVersionId,
        [FromQuery] string? triggerMaterialId, [FromQuery] string? triggerVersionId)
    {
        if ((triggerMaterialId is null) != (triggerVersionId is null))
        {
            return OkOrProblem<AnalysisReceipt>(new(new Error("Both trigger identifiers are required.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.BadRequest }));
        }

        return OkOrProblem<AnalysisReceipt>(await service.GetAnalysis(companyId, newsMaterialId, newsVersionId,
            triggerMaterialId is null ? null : new(triggerMaterialId, triggerVersionId!)));
    }
    [HttpGet("effects")]
    public async Task<IActionResult> Snapshot([FromQuery, BindRequired] Guid companyId,
        [FromQuery, BindRequired] string newsMaterialId, [FromQuery] DateTimeOffset? asOf) => OkOrProblem<EffectSnapshot>(await service.GetSnapshot(companyId, newsMaterialId, asOf));
    [HttpGet("effects/{effectId}")]
    public async Task<IActionResult> Effect([FromRoute] string effectId) => OkOrProblem<EffectResponse>(await service.GetEffect(effectId));
}
