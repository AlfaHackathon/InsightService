using AlfaHackaton.Controllers.Controllers;
using InsightService.Contracts.Projections;
using InsightService.Services;
using Microsoft.AspNetCore.Mvc;

namespace InsightService.Controllers;

[ApiController]
[Route("insight-service/internal/projections")]
public sealed class ProjectionsController(ProjectionService service) : BaseController
{
    [HttpPost("groups")]
    [ProducesResponseType(typeof(GroupProjectionResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GroupsAsync([FromBody] ProjectionRequest request, CancellationToken ct)
    {
        return OkOrProblem<GroupProjectionResponse>(await service.GroupsAsync(request, ct));
    }
    [HttpPost("canonical-facts")]
    [ProducesResponseType(typeof(CanonicalProjection), StatusCodes.Status200OK)]
    public async Task<IActionResult> CanonicalAsync([FromBody] ProjectionRequest request)
    {
        return OkOrProblem<CanonicalProjection>(await service.CanonicalAsync(request));
    }
    [HttpPost("role")]
    [ProducesResponseType(typeof(RoleProjectionResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> RoleAsync([FromBody] RoleProjectionRequest request, CancellationToken ct)
    {
        return OkOrProblem<RoleProjectionResponse>(await service.RoleAsync(request, ct));
    }
}
