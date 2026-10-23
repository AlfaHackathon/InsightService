using InsightService.Contracts.NewsJobs;
using InsightService.Models;
using InsightService.Repositories;
using System.Text.Json;
using InsightService;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Xunit;

public sealed class NewsJobsTests : IDisposable
{
    private readonly PgTestDatabase database = new();
    private NewsJobRepository Store() => new(database.Context);
    [Fact]
    public async Task SourceCursorSurvivesRestartAndRejectsConcurrentAdvance()
    {
        var initial = (await Store().GetSourceCursor());
        var first = (await Store().SaveSourceCursor(initial with { AfterQuestion = "question-64" }));
        Assert.Equal(first, (await Store().GetSourceCursor()));
        await Assert.ThrowsAsync<ApiException>(async () => await Store().SaveSourceCursor(initial with { Sequence = 1 }));
        Assert.Equal(1, (await Store().SaveSourceCursor(first with { Sequence = 1, AfterQuestion = null })).Sequence);
    }
    [Fact]
    public async Task PartialBatchCanBeClaimedByAnotherWorkerAfterReopeningStore()
    {
        await Store().Create(new("partial-job", "news", "1", DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(new { })));
        var first = (await Store().Claim(new("worker-a")))!;
        var checkpoint = JsonSerializer.SerializeToElement(new { companies = new[] { "a" }, pendingCompanyIds = new[] { "b" } });
        Assert.Equal("partial", (await Store().Update("partial-job", new("worker-a", first.Version, "partial", checkpoint))).Status);
        var second = (await Store().Claim(new("worker-b")))!;
        Assert.Equal("b", second.Checkpoint.GetProperty("pendingCompanyIds")[0].GetString());
        await Assert.ThrowsAsync<ApiException>(async () => await Store().Update("partial-job", new("worker-a", first.Version, "completed", checkpoint)));
        Assert.Equal("completed", (await Store().Update("partial-job", new("worker-b", second.Version, "completed", checkpoint))).Status);
    }
    [Fact]
    public async Task A_job_has_one_lease_and_durable_checkpoint_and_immutable_request()
    {
        var request = new NewsJobRequest("job-1", "news", "1", DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(new { companyBudget = 12 }));
        var created = (await Store().Create(request));
        Assert.Equal("queued", created.Status);
        var leased = (await Store().Claim(new("worker-a")))!;
        Assert.Null((await Store().Claim(new("worker-b"))));
        await Assert.ThrowsAsync<ApiException>(async () => await Store().Update("job-1", new("worker-b", leased.Version, "running", leased.Checkpoint)));
        var checkpoint = JsonSerializer.SerializeToElement(new { companies = new[] { "finished-a" } });
        var saved = (await Store().Update("job-1", new("worker-a", leased.Version, "running", checkpoint)));
        Assert.Equal("finished-a", (await Store().Get("job-1")).Checkpoint.GetProperty("companies")[0].GetString());
        await Assert.ThrowsAsync<ApiException>(async () => await Store().Update("job-1", new("worker-a", leased.Version, "completed", checkpoint)));
        Assert.Equal("completed", (await Store().Update("job-1", new("worker-a", saved.Version, "completed", checkpoint))).Status);
        Assert.Null((await Store().Claim(new("worker-a"))));
        Assert.Equal("completed", (await Store().Create(request)).Status);
        await Assert.ThrowsAsync<ApiException>(async () => await Store().Create(request with { NewsVersionId = "2" }));
    }
    public void Dispose() => database.Dispose();
    private sealed class EnvironmentStub(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "test";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
