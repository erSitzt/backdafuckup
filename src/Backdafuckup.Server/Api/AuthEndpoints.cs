using System.Security.Cryptography;
using Backdafuckup.Contracts;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Api;

/// <summary>
/// First-run setup: while no user exists, a one-time token is written to the log and to &lt;data&gt;/setup-token.
/// Whoever has access to the server console or file system can create the first administrator with it.
/// </summary>
public sealed class SetupTokenService(ServerPaths paths, ILogger<SetupTokenService> logger)
{
    private readonly Lock _lock = new();
    private string? _token;

    public string EnsureToken()
    {
        lock (_lock)
        {
            if (_token is not null)
            {
                return _token;
            }

            _token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            File.WriteAllText(paths.SetupTokenPath, _token);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(paths.SetupTokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            logger.LogWarning("No administrator exists yet. Open the web console and use the setup token {Token} (also stored in {Path}).", _token, paths.SetupTokenPath);
            return _token;
        }
    }

    public bool Consume(string token)
    {
        lock (_lock)
        {
            if (_token is null || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(_token), System.Text.Encoding.UTF8.GetBytes(token ?? string.Empty)))
            {
                return false;
            }

            _token = null;
            File.Delete(paths.SetupTokenPath);
            return true;
        }
    }
}

public static class AuthEndpoints
{
    public const string AdministratorRole = "Administrator";

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var setup = app.MapGroup("/api/setup").AllowAnonymous();
        setup.MapGet("/status", async (AppDbContext db, CancellationToken ct) => new SetupStatusDto(!await db.Users.AnyAsync(ct)));
        setup.MapPost("/", async (SetupRequestDto request, AppDbContext db, UserManager<AppUser> users, RoleManager<IdentityRole<Guid>> roles, SetupTokenService tokens, CancellationToken ct) =>
        {
            if (await db.Users.AnyAsync(ct))
            {
                return Results.Problem("Setup was already completed.", statusCode: StatusCodes.Status409Conflict);
            }

            if (!tokens.Consume(request.SetupToken.Trim()))
            {
                return Results.Problem("The setup token is invalid. It is shown in the server log and stored in the setup-token file.", statusCode: StatusCodes.Status403Forbidden);
            }

            if (!await roles.RoleExistsAsync(AdministratorRole))
            {
                await roles.CreateAsync(new IdentityRole<Guid>(AdministratorRole) { Id = Guid.CreateVersion7() });
            }

            var user = new AppUser { Id = Guid.CreateVersion7(), UserName = request.UserName };
            var result = await users.CreateAsync(user, request.Password);
            if (!result.Succeeded)
            {
                tokens.EnsureToken();
                return Results.ValidationProblem(result.Errors.ToDictionary(e => e.Code, e => new[] { e.Description }));
            }

            await users.AddToRoleAsync(user, AdministratorRole);
            return Results.NoContent();
        });

        var auth = app.MapGroup("/api/auth");
        auth.MapPost("/login", async (LoginRequestDto request, SignInManager<AppUser> signIn) =>
        {
            var result = await signIn.PasswordSignInAsync(request.UserName, request.Password, isPersistent: true, lockoutOnFailure: true);
            return result.Succeeded ? Results.NoContent()
                : result.IsLockedOut ? Results.Problem("Too many failed attempts. Try again in 15 minutes.", statusCode: StatusCodes.Status423Locked)
                : Results.Problem("Invalid user name or password.", statusCode: StatusCodes.Status401Unauthorized);
        }).AllowAnonymous();
        auth.MapPost("/logout", async (SignInManager<AppUser> signIn) =>
        {
            await signIn.SignOutAsync();
            return Results.NoContent();
        });
        auth.MapGet("/me", async (HttpContext http, UserManager<AppUser> users) =>
        {
            var user = await users.GetUserAsync(http.User);
            return user is null ? Results.Unauthorized() : Results.Ok(new CurrentUserDto(user.UserName ?? string.Empty, (await users.GetRolesAsync(user)).ToList()));
        });
    }
}
