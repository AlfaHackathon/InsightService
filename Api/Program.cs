using AlfaHackaton.DapperContext;
using InsightService.Extensions;
using InsightService.Middleware;

var builder = WebApplication.CreateBuilder(args);
builder.AddInsightServices();
var app = builder.Build();
app.Services.UseMigrations();
app.UseInsightRequestHandling();
app.MapControllers();
app.Run();

public partial class Program;
