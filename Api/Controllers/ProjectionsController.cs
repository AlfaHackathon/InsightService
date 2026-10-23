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
    public async Task<IActionResult> Groups([FromBody] ProjectionRequest request, CancellationToken ct) => OkOrProblem<GroupProjectionResponse>(await service.Groups(request, ct));
    [HttpPost("canonical-facts")]
    public async Task<IActionResult> Canonical([FromBody] ProjectionRequest request) => OkOrProblem<CanonicalProjection>(await service.Canonical(request));
    [HttpPost("role")]
    public async Task<IActionResult> Role([FromBody] RoleProjectionRequest request, CancellationToken ct) => OkOrProblem<RoleProjectionResponse>(await service.Role(request, ct));
}
