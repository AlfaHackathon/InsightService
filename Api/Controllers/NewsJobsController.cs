using AlfaHackaton.Controllers.Controllers;
using InsightService.Common;
using InsightService.Contracts.NewsJobs;
using InsightService.Repositories.Interfaces;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
namespace InsightService.Controllers;

[ApiController]
[Route("insight-service/internal")]
public sealed class NewsJobsController(INewsJobRepository store) : BaseController
{
    [HttpPost("news-jobs")]
    public async Task<IActionResult> Create([FromBody] NewsJobRequest request) => OkOrProblem<NewsJob>(await store.Create(request));
    [HttpGet("source-cursor")]
    public async Task<IActionResult> Cursor() => OkOrProblem<SourceCursor>(await store.GetSourceCursor());
    [HttpPost("source-cursor")]
    public async Task<IActionResult> SaveCursor([FromBody] SourceCursor cursor) => OkOrProblem<SourceCursor>(await store.SaveSourceCursor(cursor));
    [HttpPost("news-jobs/claim")]
    public async Task<IActionResult> Claim([FromBody] JobClaim request) => Content(JsonSerializer.Serialize(await store.Claim(request), AnalysisProtocol.Json), "application/json");
    [HttpGet("news-jobs/{jobId}")]
    public async Task<IActionResult> Get([FromRoute] string jobId) => OkOrProblem<NewsJob>(await store.Get(jobId));
    [HttpPost("news-jobs/{jobId}")]
    public async Task<IActionResult> Update([FromRoute] string jobId, [FromBody] JobUpdate request) => OkOrProblem<NewsJob>(await store.Update(jobId, request));
    [HttpPost("news-jobs/{jobId}/retry")]
    public async Task<IActionResult> Retry([FromRoute] string jobId) => OkOrProblem<NewsJob>(await store.Retry(jobId));
}
