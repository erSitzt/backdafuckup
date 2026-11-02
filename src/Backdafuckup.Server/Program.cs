using Backdafuckup.Server.Api;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Backdafuckup.Server.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory,
    });

    var paths = ServerPaths.Resolve(builder.Configuration, builder.Environment);
    builder.Configuration.AddServerConfigFiles(paths);
    builder.Services.AddSingleton(paths);

    builder.Host.UseSystemd();
    builder.Host.UseWindowsService(o => o.ServiceName = "backdafuckup");

    builder.Host.UseSerilog((ctx, services, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
        .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteTo.File(
            Path.Combine(paths.LogDirectory, "backdafuckup-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30,
            fileSizeLimitBytes: 50 * 1024 * 1024,
            rollOnFileSizeLimit: true));

    builder.WebHost.ConfigureServerKestrel(builder.Configuration, paths);

    // Catalog database, credentials, engine.
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddCatalog(builder.Configuration, paths);
    builder.Services.AddSingleton<ILiveNotifier, SignalRLiveNotifier>();
    builder.Services.AddBackupEngine(builder.Configuration);
    builder.Services.AddSingleton<SetupTokenService>();

    // Authentication: local accounts, cookie; API calls get 401/403 instead of redirects.
    builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
    builder.Services.ConfigureApplicationCookie(options =>
    {
        options.Cookie.Name = "bdfu.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
        options.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
    });
    builder.Services.AddIdentityCore<AppUser>(options =>
        {
            options.Password.RequiredLength = 12;
            options.Password.RequireNonAlphanumeric = false;
            options.Password.RequireDigit = false;
            options.Password.RequireUppercase = false;
            options.Password.RequireLowercase = false;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            options.User.RequireUniqueEmail = false;
        })
        .AddRoles<IdentityRole<Guid>>()
        .AddEntityFrameworkStores<AppDbContext>()
        .AddSignInManager()
        .AddDefaultTokenProviders();
    builder.Services.AddAuthorizationBuilder()
        .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

    builder.Services.AddSignalR();
    builder.Services.AddProblemDetails();
    builder.Services.AddOpenApi();
    builder.Services.AddHealthChecks();

    var app = builder.Build();

    if (!builder.Configuration.GetValue("Catalog:SkipMigration", false))
    {
        await app.Services.MigrateCatalogAsync(app.Logger, CancellationToken.None);
    }

    await using (var scope = app.Services.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (!await db.Users.AnyAsync())
        {
            scope.ServiceProvider.GetRequiredService<SetupTokenService>().EnsureToken();
        }
    }

    app.UseSerilogRequestLogging();
    app.UseExceptionHandler();
    app.UseStatusCodePages();
    app.UseBlazorFrameworkFiles();
    app.UseStaticFiles();
    app.UseRouting();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseMiddleware<CsrfHeaderMiddleware>();

    app.MapHealthChecks("/health").AllowAnonymous();
    app.MapOpenApi().AllowAnonymous();
    app.MapAuthEndpoints();
    app.MapInfrastructureEndpoints();
    app.MapOperationEndpoints();
    app.MapHub<LiveHub>("/hubs/live");
    app.MapFallbackToFile("index.html").AllowAnonymous();

    await app.RunAsync();
    return 0;
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "backdafuckup terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

/// <summary>Entry point marker for WebApplicationFactory in tests.</summary>
public partial class Program;
