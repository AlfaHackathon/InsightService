using AlfaHackaton.DapperContext;
using InsightService.Extensions;
using InsightService.Middleware;

var builder = WebApplication.CreateBuilder(args);
builder.AddInsightServices();
builder.Services.AddPublicInsightReads();
builder.Services.AddServiceSwagger();
var app = builder.Build();
app.Services.UseMigrations();
app.UseInsightRequestHandling();
app.UsePublicInsightReads();
app.UseServiceSwagger();
app.MapControllers();
app.Run();

public partial class Program;
