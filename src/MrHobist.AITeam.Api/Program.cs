using MrHobist.AITeam.Api;
using MrHobist.AITeam.Api.Scene;

var builder = WebApplication.CreateBuilder(args);

// Ag siniri (CLAUDE.md §3): yalniz loopback.
builder.WebHost.UseUrls("http://127.0.0.1:5080");

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<ConfigRoot>();
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
app.MapScene();

app.Run();
