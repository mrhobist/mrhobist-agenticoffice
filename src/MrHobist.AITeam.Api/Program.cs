using System.Text.Json.Serialization;
using MrHobist.AITeam.Api.Auth;
using MrHobist.AITeam.Api.Config;
using MrHobist.AITeam.Api.Errors;
using MrHobist.AITeam.Api.Jobs;
using MrHobist.AITeam.Api.Projects;
using MrHobist.AITeam.Api.Runs;
using MrHobist.AITeam.Api.Scene;
using MrHobist.AITeam.Application.Abstractions;
using MrHobist.AITeam.Application.Runs;
using MrHobist.AITeam.Infrastructure;
using MrHobist.AITeam.Infrastructure.Storage;

var builder = WebApplication.CreateBuilder(args);

// Ag siniri (CLAUDE.md §3): yalniz loopback. Port ayarla degisebilir (ikinci kopya calistirmak icin), adres degismez.
var apiUrl = new Uri(builder.Configuration["AITeam:ApiUrl"] ?? "http://127.0.0.1:5080");
if (!apiUrl.IsLoopback)
{
    throw new InvalidOperationException("AITeam:ApiUrl yalniz loopback olabilir (CLAUDE.md §3).");
}

builder.WebHost.UseUrls(apiUrl.GetLeftPart(UriPartial.Authority));

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
// Giris: gomulu admin/admin + JWT tek sema, rol claim'i (docs/DOMAIN.md → Giris). Ileride LDAP IUserDirectory ile gelir.
builder.Services.AddAiTeamAuth(builder.Configuration);
builder.Services.AddExceptionHandler<ProblemMapping>();

var paths = StoragePaths.Discover(
    builder.Environment.ContentRootPath,
    builder.Configuration["AITeam:ConfigRoot"],
    builder.Configuration["AITeam:RunsRoot"]);
builder.Services.AddFileStorage(paths);
builder.Services.AddPythonRuntime(new Uri(builder.Configuration["AITeam:RuntimeUrl"] ?? "http://127.0.0.1:5090"));
builder.Services.AddSingleton<SceneEventBus>();
builder.Services.AddSingleton<ISceneEventPublisher>(sp => sp.GetRequiredService<SceneEventBus>());

// Tek yazici is kanali: uzun isler (analiz, dagitim) burada sirayla kosar; istek 202 ile doner (CLAUDE.md §2, Sapmalar).
builder.Services.AddSingleton<JobChannel>();
builder.Services.AddHostedService<JobWorker>();
builder.Services.AddHostedService<LimitResumer>(); // limit beklemesi biten calismalari surdurur

// UI gelistirme sunucusu ayri porttan gelir. Kokenler acik listedir; ikinci bir UI kopyasi icin AITeam:UiOrigins
// ("http://127.0.0.1:3005,http://localhost:3005") verilir. Loopback disi koken kabul edilmez (CLAUDE.md §3).
var uiOrigins = (builder.Configuration["AITeam:UiOrigins"] ?? "http://127.0.0.1:3000,http://localhost:3000")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (uiOrigins.Any(o => !Uri.TryCreate(o, UriKind.Absolute, out var u) || !u.IsLoopback))
{
    throw new InvalidOperationException("AITeam:UiOrigins yalniz loopback kokenler icerebilir (CLAUDE.md §3).");
}

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(uiOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.UseAuthentication();
app.UseAiTeamAuthGate();

// Sozlesme: /openapi/v1.json (CLAUDE.md §Belge haritasi).
app.MapOpenApi();
app.MapAuth();
app.MapConfig();
app.MapScene();
app.MapRuns();
app.MapProjects();
app.MapGet("/api/v1/jobs/health", (JobChannel q) => Results.Ok(new { status = "ok", pending = q.Pending }));

// Surec yeniden basladi: yarim kalan Running calismalar Interrupted (docs/DOMAIN.md). Isci henuz baslamadi, tek yazici biziz.
var interrupted = await app.Services.GetRequiredService<IRunService>().MarkInterruptedAsync(CancellationToken.None).ConfigureAwait(false);
if (interrupted > 0)
{
    Log.Interrupted(app.Logger, interrupted);
}

app.Run();
