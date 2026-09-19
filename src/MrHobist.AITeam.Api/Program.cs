using System.Text.Json.Serialization;
using MrHobist.AITeam.Api.Config;
using MrHobist.AITeam.Api.Errors;
using MrHobist.AITeam.Api.Scene;
using MrHobist.AITeam.Infrastructure;
using MrHobist.AITeam.Infrastructure.Storage;

var builder = WebApplication.CreateBuilder(args);

// Ag siniri (CLAUDE.md §3): yalniz loopback.
builder.WebHost.UseUrls("http://127.0.0.1:5080");

// Enum'lar JSON'da adiyla tasinir (CLAUDE.md §5).
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemMapping>();

var paths = StoragePaths.Discover(
    builder.Environment.ContentRootPath,
    builder.Configuration["AITeam:ConfigRoot"],
    builder.Configuration["AITeam:RunsRoot"]);
builder.Services.AddFileStorage(paths);
builder.Services.AddPythonRuntime(new Uri(builder.Configuration["AITeam:RuntimeUrl"] ?? "http://127.0.0.1:5090"));
builder.Services.AddSingleton<SceneEventBus>();

// UI gelistirme sunucusu ayri porttan gelir; ikisi de loopback.
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins("http://127.0.0.1:3000", "http://localhost:3000")
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();

// Sozlesme: /openapi/v1.json (CLAUDE.md §Belge haritasi).
app.MapOpenApi();
app.MapConfig();
app.MapScene();

app.Run();
