using Backdafuckup.Server.Agents;
using Backdafuckup.Server.Api;
using Backdafuckup.Server.Clusters;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Backdafuckup.Server.Hosting;
using Backdafuckup.Server.Movers;
using Backdafuckup.Server.Telemetry;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Scalar.AspNetCore;
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

    // Authentication: local accounts with the console's cookie, or an API token (Authorization: Bearer bdfu_…) for scripts;
    // API calls get 401/403 instead of redirects.
    var authentication = builder.Services.AddAuthentication(ApiTokens.SelectorScheme);
    authentication.AddIdentityCookies();
    authentication.AddApiTokens();
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
    // Every endpoint needs a sign-in or a full API token; a metrics-only token (the scrape token) reads /metrics and nothing else.
    var fullAccess = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireAssertion(c => !c.User.IsMetricsToken()).Build();
    builder.Services.AddAuthorizationBuilder()
        .SetDefaultPolicy(fullAccess)
        .SetFallbackPolicy(fullAccess)
        .AddPolicy(MetricsEndpoint.Policy, policy => policy.RequireAuthenticatedUser());

    // Prometheus metrics, pulled from /metrics: nothing is sent anywhere.
    var metricsEnabled = builder.Configuration.GetValue("Metrics:Enabled", true);
    if (metricsEnabled)
    {
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("backdafuckup", serviceVersion: ProductInfo.Current.Version, autoGenerateServiceInstanceId: false))
            .WithMetrics(metrics => metrics
            .AddMeter(BackupMetrics.MeterName, "System.Runtime", "Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel", "System.Net.Http", "Npgsql")
            .AddPrometheusExporter());
    }

    builder.Services.AddSignalR();
    builder.Services.AddProblemDetails();
    builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "backdafuckup API";
        document.Info.Version = ProductInfo.Current.Version;
        document.Info.Description = "The API of the backdafuckup web console. Scripts send an API token (created in the console under API tokens) as " +
            "Authorization: Bearer bdfu_…; they act as the token's user. In the browser, requests use the console's session cookie, and requests that " +
            "change something need the header X-BDFU-Request: 1 (not needed with a token).";
        return Task.CompletedTask;
    }));
    builder.Services.AddHealthChecks();
    builder.Services.AddResponseCompression(options =>
    {
        // JSON only: the console's static files come precompressed from the static asset manifest.
        options.EnableForHttps = true;
        options.MimeTypes = ["application/json"];
        options.Providers.Add<BrotliCompressionProvider>();
        options.Providers.Add<GzipCompressionProvider>();
    });
    builder.Services.Configure<BrotliCompressionProviderOptions>(options => options.Level = System.IO.Compression.CompressionLevel.Fastest);
    builder.Services.Configure<GzipCompressionProviderOptions>(options => options.Level = System.IO.Compression.CompressionLevel.Fastest);

    var app = builder.Build();
    app.Logger.LogInformation("backdafuckup {Version} starting.", ProductInfo.Current.Describe());

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

    // Compressed JSON, except where a response can carry a secret (setup, sign-in, API tokens): the length of a compressed
    // secret next to attacker-chosen text can leak it (BREACH).
    app.UseWhen(context => !ResponseCompressionPaths.CarriesSecrets(context.Request.Path), branch => branch.UseResponseCompression());
    app.UseStatusCodePages();
    app.UseRouting();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseMiddleware<CsrfHeaderMiddleware>();

    // The web console from the static web asset manifest: names without fingerprint (blazor.webassembly.js,
    // dotnet.js) map to the fingerprinted files, with compression and caching. Needed before the login.
    app.MapStaticAssets().AllowAnonymous();
    app.MapHealthChecks("/health").AllowAnonymous();
    if (metricsEnabled)
    {
        app.MapPrometheusScrapingEndpoint(MetricsEndpoint.Path).RequireAuthorization(MetricsEndpoint.Policy);
    }
    app.MapGet("/api/about", () => ProductInfo.Current.ToDto());
    app.MapOpenApi().AllowAnonymous();

    // API reference for signed-in users (the console links to it). The page and its script come from the package, not
    // from a CDN, so it also works without internet access.
    // No telemetry, no AI chat, no links to Scalar's cloud services: an on-premises product sends nothing outside.
    app.MapScalarApiReference("/api-docs", options =>
    {
        options.WithTitle("backdafuckup API").WithOpenApiRoutePattern("/openapi/{documentName}.json");
        options.Telemetry = false;
        options.Agent = new ScalarAgentOptions { Disabled = true };
        options.Mcp = new ScalarMcpOptions { Disabled = true };
        options.ShowDeveloperTools = DeveloperToolsVisibility.Never;
        options.HideClientButton = true;
        options.DisableDefaultFonts();

        // "Generate SDK" leads to Scalar's cloud service; it has no option of its own.
        options.CustomCss = ".generate-sdk-button, .selected-client-action { display: none !important; }";
    });
    app.MapAuthEndpoints();
    app.MapApiTokenEndpoints();
    app.MapInfrastructureEndpoints();
    app.MapOperationEndpoints();
    app.UseWebSockets();
    app.MapAgentEndpoints();
    app.MapMoverEndpoints();
    app.MapClusterAgentEndpoints();
    app.MapMoverImageEndpoints();
    app.MapFileRestoreEndpoints();
    app.MapLinuxCredentialEndpoints();
    app.MapSqlEndpoints();
    app.MapAdEndpoints();
    app.MapTapeEndpoints();
    app.MapTapeAgentEndpoints();
    app.MapTapeCatalogEndpoints();
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
