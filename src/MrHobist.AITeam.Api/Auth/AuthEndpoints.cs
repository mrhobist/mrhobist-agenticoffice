using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using MrHobist.AITeam.Api.Errors;
using MrHobist.AITeam.Domain;

namespace MrHobist.AITeam.Api.Auth;

/// <summary><c>POST /auth/login</c> govdesi.</summary>
public sealed record LoginRequest(string Username, string Password);

/// <summary>Giris yapan kullanici. <see cref="Role"/> claim'i yetkiyi tasir (CLAUDE.md: JWT tek sema, ayrimi rol claim'i yapar).</summary>
public sealed record UserInfo(string Id, string Name, string Role);

public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt, UserInfo User);

/// <summary>
/// Giris hazirligi (kullanici karari 2026-09-19): kodda gomulu tek kullanici <c>admin / admin</c>, JWT tek sema (HS256),
/// rol claim'i. Ileride LDAP ya da tam kullanici mimarisi bu siniri degistirmeden <see cref="IUserDirectory"/> ile gelir.
/// Host yalniz loopback dinler (CLAUDE.md §3); bu yuzden sabit bir gelistirme anahtari kabul edilebilir, ama
/// <c>AITeam:JwtKey</c> ile degistirilebilir.
/// </summary>
public static class AuthSetup
{
    public const string Scheme = JwtBearerDefaults.AuthenticationScheme;

    /// <summary>Gelistirme anahtari: yalniz loopback host. Uretimde <c>AITeam:JwtKey</c> zorunlu sayilmali.</summary>
    private const string DevKey = "mrhobist-aiteam-dev-key-2026-09-19-loopback-only-min-32-bytes";

    public static SymmetricSecurityKey KeyFrom(IConfiguration config)
        => new(Encoding.UTF8.GetBytes(config["AITeam:JwtKey"] ?? DevKey));

    public static IServiceCollection AddAiTeamAuth(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<IUserDirectory, EmbeddedUserDirectory>();
        services.AddAuthentication(Scheme).AddJwtBearer(o =>
        {
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = "mrhobist-aiteam",
                ValidateAudience = false,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(1),
                IssuerSigningKey = KeyFrom(config),
            };
            // EventSource baslik gonderemez: SSE icin belirtec sorgu parametresiyle gelir (yalniz o yolda).
            o.Events = new JwtBearerEvents
            {
                OnMessageReceived = ctx =>
                {
                    if (ctx.Request.Path.StartsWithSegments("/api/v1/scene/events") && ctx.Request.Query.TryGetValue("access_token", out var t))
                    {
                        ctx.Token = t;
                    }

                    return Task.CompletedTask;
                },
            };
        });
        // AddAuthorization YOK: yetki kapisi UseAiTeamAuthGate; hicbir uc RequireAuthorization kullanmaz (tek sema, rol claim'i).
        return services;
    }

    public static IEndpointRouteBuilder MapAuth(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/auth");

        g.MapPost("/login", (LoginRequest body, IUserDirectory users, IConfiguration config) =>
        {
            var user = users.Authenticate(body.Username, body.Password)
                ?? throw new DomainException(ErrorCodes.AuthInvalidCredentials, "Kullanici adi ya da sifre yanlis.");
            var expires = DateTimeOffset.UtcNow.AddHours(12);
            var token = new JwtSecurityToken(
                issuer: "mrhobist-aiteam",
                claims:
                [
                    new Claim(JwtRegisteredClaimNames.Sub, user.Id),
                    new Claim(ClaimTypes.Name, user.Name),
                    new Claim(ClaimTypes.Role, user.Role),
                ],
                expires: expires.UtcDateTime,
                signingCredentials: new SigningCredentials(KeyFrom(config), SecurityAlgorithms.HmacSha256));
            return new LoginResponse(new JwtSecurityTokenHandler().WriteToken(token), expires, user);
        }).AllowAnonymous();

        g.MapGet("/me", (ClaimsPrincipal me) => new UserInfo(
            me.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? me.FindFirstValue(ClaimTypes.NameIdentifier) ?? "?",
            me.Identity?.Name ?? "?",
            me.FindFirstValue(ClaimTypes.Role) ?? "user"));

        return app;
    }

    /// <summary>/api/v1 altinda kimlik zorunlu; login, saglik probu ve OPTIONS haric. 401 Problem Details + <c>auth.required</c>.</summary>
    public static IApplicationBuilder UseAiTeamAuthGate(this IApplicationBuilder app)
        => app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path;
            var open = HttpMethods.IsOptions(ctx.Request.Method)
                || !path.StartsWithSegments("/api/v1")
                || path.StartsWithSegments("/api/v1/auth/login")
                || path.StartsWithSegments("/api/v1/jobs/health") // canlilik probu kimliksiz
                || path.StartsWithSegments("/api/v1/progress"); // runtime'in arac bildirimi: tek kullanimlik belirtec yetkidir (ProgressRegistry)
            if (!open && ctx.User.Identity?.IsAuthenticated != true)
            {
                // Ayni Problem Details sekli (errorCode + traceId): ProblemMapping tek kaynak.
                await ProblemMapping.Result(StatusCodes.Status401Unauthorized, ErrorCodes.AuthRequired, "Giris gerekli.").ExecuteAsync(ctx).ConfigureAwait(false);
                return;
            }

            await next().ConfigureAwait(false);
        });
}

/// <summary>Kullanici kaynagi. Bugun kodda gomulu; ileride LDAP / kullanici deposu bu arayuzu uygular.</summary>
public interface IUserDirectory
{
    UserInfo? Authenticate(string username, string password);
}

/// <summary>Gomulu tek kullanici: <c>admin / admin</c>, rol <c>owner</c>. Sifre karsilastirmasi sabit zamanli.</summary>
public sealed class EmbeddedUserDirectory : IUserDirectory
{
    private static readonly (string User, string Pass, UserInfo Info)[] Users =
    [
        ("admin", "admin", new UserInfo("admin", "Admin", "owner")),
    ];

    public UserInfo? Authenticate(string username, string password)
    {
        foreach (var (user, pass, info) in Users)
        {
            if (string.Equals(user, username?.Trim(), StringComparison.OrdinalIgnoreCase)
                && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(pass), Encoding.UTF8.GetBytes(password ?? "")))
            {
                return info;
            }
        }

        return null;
    }
}
