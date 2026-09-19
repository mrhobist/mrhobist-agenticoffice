using System.Text.Json.Serialization;
using MrHobist.AITeam.Api.Config;
using MrHobist.AITeam.Api.Errors;
using MrHobist.AITeam.Api.Scene;
using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Infrastructure;
using MrHobist.AITeam.Infrastructure.Storage;

var builder = WebApplication.CreateBuilder(args);

// Ag siniri (CLAUDE.md §3): yalniz loopback.
builder.WebHost.UseUrls("http://127.0.0.1:5080");

// Enum'lar JSON'da adiyla tasinir (CLAUDE.md §5). Govdede eksik/null birakilan non-nullable alan
// sessizce null olmaz, 400 request.invalid olur: "kismi guncelleme" diye bir sey yoktur (docs/API.md).
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
    o.SerializerOptions.RespectNullableAnnotations = true;
    o.SerializerOptions.RespectRequiredConstructorParameters = true;
});

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
builder.Services.AddSingleton<ISceneEventPublisher>(sp => sp.GetRequiredService<SceneEventBus>());

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
