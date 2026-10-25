using InsightService.Contracts.Analyses;
using InsightService.Contracts.Reads;
using InsightService.Repositories;
using Microsoft.AspNetCore.Mvc;

namespace InsightService.Controllers;

[ApiController]
[Route("insight-service/internal/read/companies/{companyId:guid}")]
public sealed class InsightReadsController(InsightReadRepository repository) : ControllerBase
{
    [HttpGet("personal")]
    [ProducesResponseType(typeof(InsightReadPage<PersonalInsightRead>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Personal(Guid companyId, [FromQuery] InsightReadOptions options) => Ok(await repository.PersonalPage(companyId, options));

    [HttpGet("personal/{id}")]
    [ProducesResponseType(typeof(PersonalInsightRead), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PersonalDetail(Guid companyId, string id)
    {
        var result = await repository.PersonalDetail(companyId, id);
        return result is null ? NotFound(new { code = "insight_not_found" }) : Ok(result);
    }

    [HttpGet("personal/{id}/history")]
    [ProducesResponseType(typeof(InsightReadPage<EffectRevision>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> History(Guid companyId, string id, [FromQuery] int limit = 10, [FromQuery] string? cursor = null)
    {
        var result = await repository.PersonalHistory(companyId, id, limit, cursor);
        return result is null ? NotFound(new { code = "insight_not_found" }) : Ok(result);
    }

    [HttpGet("general")]
    [ProducesResponseType(typeof(InsightReadPage<GeneralInsightRead>), StatusCodes.Status200OK)]
    public async Task<IActionResult> General(Guid companyId, [FromQuery] InsightReadOptions options) => Ok(await repository.GeneralPage(companyId, options));

    [HttpGet("general/{id}")]
    [ProducesResponseType(typeof(GeneralInsightRead), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GeneralDetail(Guid companyId, string id)
    {
        var result = await repository.GeneralDetail(companyId, id);
        return result is null ? NotFound(new { code = "insight_not_found" }) : Ok(result);
    }
}
