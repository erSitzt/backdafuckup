using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Backdafuckup.Contracts;
using Backdafuckup.Server.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backdafuckup.Server.Api;

/// <summary>
/// API tokens: <c>bdfu_</c> and 32 random bytes (base64url). A request with <c>Authorization: Bearer bdfu_…</c> acts as
/// the token's user; the catalog keeps only the token's SHA-256. Tokens are created in the console with a password
/// sign-in, never with a token.
/// </summary>
public static class ApiTokens
{
    public const string Scheme = "ApiToken";

    /// <summary>Forwards to <see cref="Scheme"/> for a bearer token, otherwise to the console's cookie.</summary>
    public const string SelectorScheme = "bdfu";

    public const string Prefix = "bdfu_";

    /// <summary>The claim that marks a request authenticated by a token (its value: the token's id).</summary>
    public const string TokenClaim = "bdfu:api-token";

    public static string NewToken() => Prefix + Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>The token of an <c>Authorization: Bearer bdfu_…</c> header, or null.</summary>
    public static string? FromHeader(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer " + Prefix, StringComparison.Ordinal) ? header["Bearer ".Length..].Trim() : null;
    }

    public static bool IsTokenRequest(this ClaimsPrincipal user) => user.HasClaim(c => c.Type == TokenClaim);

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static ApiTokenDto ToDto(ApiTokenEntity t) => new(t.Id, t.Name, t.Prefix, t.CreatedUtc, t.ExpiresUtc, t.LastUsedUtc, t.LastUsedFrom);

    public static AuthenticationBuilder AddApiTokens(this AuthenticationBuilder builder) =>
        builder
            .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(Scheme, null)
            .AddPolicyScheme(SelectorScheme, "Cookie or API token", options =>
                options.ForwardDefaultSelector = context => FromHeader(context.Request) is not null ? Scheme : IdentityConstants.ApplicationScheme);

    public static void MapApiTokenEndpoints(this IEndpointRouteBuilder app)
    {
        var tokens = app.MapGroup("/api/tokens");
        tokens.MapGet("/", async (HttpContext http, UserManager<AppUser> users, AppDbContext db, CancellationToken ct) =>
            users.GetUserId(http.User) is { } id && Guid.TryParse(id, out var userId)
                ? Results.Ok((await db.ApiTokens.AsNoTracking().Where(t => t.UserId == userId).OrderByDescending(t => t.CreatedUtc).ToListAsync(ct)).Select(ToDto).ToList())
                : Results.Unauthorized());

        tokens.MapPost("/", async (CreateApiTokenDto request, HttpContext http, UserManager<AppUser> users, AppDbContext db, TimeProvider time, ILogger<ApiTokenAuthenticationHandler> logger, CancellationToken ct) =>
        {
            if (http.User.IsTokenRequest())
            {
                return Results.Problem("API tokens are created in the console, signed in with a password, not with another token.", statusCode: StatusCodes.Status403Forbidden);
            }

            if (users.GetUserId(http.User) is not { } id || !Guid.TryParse(id, out var userId))
            {
                return Results.Unauthorized();
            }

            var name = request.Name?.Trim() ?? string.Empty;
            if (name.Length is 0 or > 100)
            {
                return Results.Problem("Give the token a name of 1 to 100 characters (what uses it, e.g. \"lab scripts\").", statusCode: StatusCodes.Status400BadRequest);
            }

            if (request.ExpiresInDays is < 1 or > 3650)
            {
                return Results.Problem("A token expires after 1 to 3650 days, or never.", statusCode: StatusCodes.Status400BadRequest);
            }

            var token = NewToken();
            var now = time.GetUtcNow();
            var entity = new ApiTokenEntity
            {
                UserId = userId,
                Name = name,
                Prefix = token[..(Prefix.Length + 8)],
                Hash = Hash(token),
                CreatedUtc = now,
                ExpiresUtc = request.ExpiresInDays is { } days ? now.AddDays(days) : null,
            };
            db.ApiTokens.Add(entity);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("API token {Name} ({Prefix}…) created by {User}, expires {Expires}.", entity.Name, entity.Prefix, http.User.Identity?.Name, entity.ExpiresUtc?.ToString("u") ?? "never");
            return Results.Ok(new CreatedApiTokenDto(ToDto(entity), token));
        });

        tokens.MapDelete("/{tokenId:guid}", async (Guid tokenId, HttpContext http, UserManager<AppUser> users, AppDbContext db, ILogger<ApiTokenAuthenticationHandler> logger, CancellationToken ct) =>
        {
            if (users.GetUserId(http.User) is not { } id || !Guid.TryParse(id, out var userId))
            {
                return Results.Unauthorized();
            }

            var token = await db.ApiTokens.FirstOrDefaultAsync(t => t.Id == tokenId && t.UserId == userId, ct);
            if (token is null)
            {
                return Results.NotFound();
            }

            db.ApiTokens.Remove(token);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("API token {Name} ({Prefix}…) deleted by {User}.", token.Name, token.Prefix, http.User.Identity?.Name);
            return Results.NoContent();
        });
    }
}

/// <summary>Authenticates <c>Authorization: Bearer bdfu_…</c> as the token's user (with the user's roles).</summary>
public sealed class ApiTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IDbContextFactory<AppDbContext> dbFactory,
    TimeProvider time) : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    private static readonly TimeSpan LastUsedPrecision = TimeSpan.FromMinutes(1);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (ApiTokens.FromHeader(Request) is not { } token)
        {
            return AuthenticateResult.NoResult();
        }

        var hash = ApiTokens.Hash(token);
        await using var db = await dbFactory.CreateDbContextAsync(Context.RequestAborted).ConfigureAwait(false);
        var found = await db.ApiTokens.AsNoTracking()
            .Where(t => t.Hash == hash)
            .Join(db.Users, t => t.UserId, u => u.Id, (t, u) => new { Token = t, User = u })
            .FirstOrDefaultAsync(Context.RequestAborted).ConfigureAwait(false);
        var now = time.GetUtcNow();
        if (found is null || found.Token.ExpiresUtc <= now)
        {
            return AuthenticateResult.Fail("The API token is not valid (unknown, deleted or expired).");
        }

        if (found.User.LockoutEnd > now)
        {
            return AuthenticateResult.Fail($"The user {found.User.UserName} of this API token is locked out.");
        }

        var roles = await db.UserRoles.Where(r => r.UserId == found.User.Id).Join(db.Roles, r => r.RoleId, role => role.Id, (_, role) => role.Name)
            .ToListAsync(Context.RequestAborted).ConfigureAwait(false);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, found.User.Id.ToString()),
            new(ClaimTypes.Name, found.User.UserName ?? string.Empty),
            new(ApiTokens.TokenClaim, found.Token.Id.ToString()),
        };
        claims.AddRange(roles.OfType<string>().Select(r => new Claim(ClaimTypes.Role, r)));

        if (found.Token.LastUsedUtc is not { } last || now - last > LastUsedPrecision)
        {
            var from = Context.Connection.RemoteIpAddress?.ToString();
            await db.ApiTokens.Where(t => t.Id == found.Token.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedUtc, now).SetProperty(t => t.LastUsedFrom, from), Context.RequestAborted).ConfigureAwait(false);
        }

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, ApiTokens.Scheme)), ApiTokens.Scheme));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer realm=\"backdafuckup\"";
        return Task.CompletedTask;
    }
}
