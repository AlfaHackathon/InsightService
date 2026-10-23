using AlfaHackaton.Controllers.Controllers;
using InsightService.Contracts.Questions;
using InsightService.Contracts.SourceReactions;
using InsightService.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
namespace InsightService.Controllers;

[ApiController]
[Route("insight-service/internal")]
public sealed class SourceReactionsController(IAnalysisService service) : BaseController
{
    [HttpGet("question-recipients")]
    public async Task<IActionResult> Recipients([FromQuery, BindRequired] DateTimeOffset asOf, [FromQuery] string? after, [FromQuery] string? materialId) => OkOrProblem<RecipientPage>(await service.Recipients(asOf, after, materialId));
    [HttpPost("source-changes")]
    public async Task<IActionResult> React([FromBody] SourceChangeRequest change) => OkOrProblem<ReactionPage>(await service.React(change));
}
