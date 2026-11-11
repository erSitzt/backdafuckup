using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Backdafuckup.Contracts;
using Backdafuckup.Core.DataMover;
using Backdafuckup.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Movers;

/// <summary>A push of the mover image: where it went and its digest, by which the pods reference it.</summary>
public sealed record MoverPush(string Registry, string Repository, string Tag, string Digest, string Version, DateTimeOffset PushedUtc)
{
    public string Reference => $"{Registry}/{Repository}@{Digest}";
}

/// <summary>
/// The registry the mover image goes to (settings stored encrypted), pushing the image the server ships, and what the
/// clusters need to pull it. A server update ships a new image; the next backup pushes it when it is not there yet.
/// </summary>
public sealed partial class MoverRegistry(
    IDbContextFactory<AppDbContext> dbFactory, CredentialProtector protector, MoverImage image, RegistryTransport transport, TimeProvider time, ILogger<MoverRegistry> logger)
    : IMoverImageSource
{
    /// <summary>
    /// Starts like the Docker client's User-Agent, because proxies in front of registries (e.g. HAProxy before Nexus) often
    /// pass Docker clients only; the comment names backdafuckup in the proxy's log.
    /// </summary>
    public static readonly string DefaultUserAgent = $"docker/27.5.1 (compatible; backdafuckup/{ProductVersion()})";

    private const string ConfigKey = "mover.registry";
    private const string PushedKey = "mover.pushed";
    private readonly SemaphoreSlim _push = new(1, 1);

    public async Task<MoverImageDto> DescribeAsync(CancellationToken cancellationToken)
    {
        var config = await GetConfigAsync(cancellationToken).ConfigureAwait(false);
        var pushed = await GetPushedAsync(cancellationToken).ConfigureAwait(false);
        var shipped = image.TryGet(out var problem);
        return new MoverImageDto
        {
            Registry = config.Registry,
            Repository = config.Repository,
            UserName = config.UserName,
            HasPassword = config.Password is not null,
            PullSecret = config.PullSecret,
            PullUserName = config.PullUserName,
            HasPullPassword = config.PullPassword is not null,
            ExistingSecret = config.ExistingSecret,
            UserAgent = config.UserAgent,
            DefaultUserAgent = DefaultUserAgent,
            Shipped = shipped is null ? null : new MoverImageBuildDto(shipped.Version, shipped.ManifestDigest, shipped.Tag, shipped.Size),
            ShippedProblem = problem,
            Pushed = pushed is null ? null : new MoverImagePushDto(pushed.Reference, pushed.Tag, pushed.Digest, pushed.Version, pushed.PushedUtc),
            UpToDate = IsCurrent(pushed, config, shipped),
        };
    }

    /// <summary>Checks and stores the settings; throws <see cref="ArgumentException"/> with the field's problem.</summary>
    public async Task SaveAsync(SaveMoverImageDto request, CancellationToken cancellationToken)
    {
        var current = await GetConfigAsync(cancellationToken).ConfigureAwait(false);
        var registry = request.Registry.Trim();
        if (registry.Length == 0 || registry.Contains('/', StringComparison.Ordinal) || !Uri.TryCreate($"https://{registry}/", UriKind.Absolute, out var url) || url.UserInfo.Length > 0)
        {
            throw new ArgumentException($"The registry '{request.Registry}' is not a host name with an optional port (e.g. registry.corp.example:8443, without https:// and path).");
        }

        var repository = request.Repository.Trim().Trim('/');
        if (!RepositoryName().IsMatch(repository))
        {
            throw new ArgumentException($"The repository '{request.Repository}' is not a valid image repository (lower-case letters, digits, '.', '_', '-', parts separated by '/', e.g. backdafuckup/mover).");
        }

        var mode = request.PullSecret switch
        {
            MoverPullSecretModes.Create or MoverPullSecretModes.Existing or MoverPullSecretModes.None => request.PullSecret,
            _ => throw new ArgumentException($"Unknown pull secret mode '{request.PullSecret}' (Create, Existing or None)."),
        };
        var existing = request.ExistingSecret?.Trim();
        if (mode == MoverPullSecretModes.Existing && (existing is null || !SecretName().IsMatch(existing)))
        {
            throw new ArgumentException($"'{request.ExistingSecret}' is not the name of a Kubernetes secret (lower-case letters, digits, '-' and '.').");
        }

        var userAgent = NullIfEmpty(request.UserAgent);
        if (userAgent is not null && (userAgent.Length > 200 || userAgent.Any(char.IsControl)))
        {
            throw new ArgumentException("The User-Agent must be one line of at most 200 characters.");
        }

        static string? Secret(string? update, string? stored) => update is null ? stored : update.Length == 0 ? null : update;
        await SetAsync(ConfigKey, protector.Protect(JsonSerializer.Serialize(new MoverRegistryConfig(
            registry,
            repository,
            NullIfEmpty(request.UserName),
            Secret(request.Password, current.Password),
            mode,
            NullIfEmpty(request.PullUserName),
            Secret(request.PullPassword, current.PullPassword),
            mode == MoverPullSecretModes.Existing ? existing : null,
            userAgent))), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Pushes the shipped image to the registry; returns a line per step.</summary>
    public async Task<IReadOnlyList<string>> PushAsync(CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        await _push.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PushLockedAsync(lines.Add, cancellationToken).ConfigureAwait(false);
            return lines;
        }
        finally
        {
            _push.Release();
        }
    }

    /// <summary>The image and pull secret for a cluster; pushes the shipped image first when the registry does not have it yet.</summary>
    public async Task<MoverDeployment> EnsurePushedAsync(Action<string>? log, CancellationToken cancellationToken)
    {
        await _push.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var config = await GetConfigAsync(cancellationToken).ConfigureAwait(false);
            var pushed = await GetPushedAsync(cancellationToken).ConfigureAwait(false);
            if (!IsCurrent(pushed, config, image.TryGet(out _)))
            {
                pushed = await PushLockedAsync(log, cancellationToken).ConfigureAwait(false);
            }

            return Deployment(config, pushed!);
        }
        finally
        {
            _push.Release();
        }
    }

    /// <summary>The image by digest, and the pull secret: an existing one, none, or one created from the pull (else the push) credentials.</summary>
    internal static MoverDeployment Deployment(MoverRegistryConfig config, MoverPush pushed) => config.PullSecret switch
    {
        MoverPullSecretModes.Existing => new MoverDeployment(pushed.Reference, config.ExistingSecret, null),
        MoverPullSecretModes.Create when (config.PullUserName ?? config.UserName) is { } user => new MoverDeployment(
            pushed.Reference,
            MoverDeployment.CreatedSecretName,
            DockerConfigJson(config.Registry, user, (config.PullUserName is null ? config.Password : config.PullPassword) ?? string.Empty)),
        _ => new MoverDeployment(pushed.Reference, null, null),
    };

    private static string DockerConfigJson(string registry, string userName, string password) => JsonSerializer.Serialize(new
    {
        auths = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [registry] = new { username = userName, password, auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{password}")) },
        },
    });

    private static bool IsCurrent(MoverPush? pushed, MoverRegistryConfig config, MoverImageBundle? shipped) =>
        pushed is not null && shipped is not null && pushed.Digest == shipped.ManifestDigest
        && string.Equals(pushed.Registry, config.Registry, StringComparison.OrdinalIgnoreCase) && pushed.Repository == config.Repository;

    private async Task<MoverPush> PushLockedAsync(Action<string>? log, CancellationToken cancellationToken)
    {
        var config = await GetConfigAsync(cancellationToken).ConfigureAwait(false);
        if (config.Registry.Length == 0)
        {
            throw new RegistryException("No registry is set for the mover image (Kubernetes clusters → Mover image).");
        }

        var shipped = image.TryGet(out var problem) ?? throw new RegistryException(problem!);
        log?.Invoke($"Pushing the mover image {shipped.Version} to {config.Registry}/{config.Repository}:{shipped.Tag}.");
        using var client = new RegistryClient(transport.Create(), config.Registry, config.UserName, config.Password, config.UserAgent ?? DefaultUserAgent);
        var digest = await client.PushAsync(config.Repository, [shipped.Config, .. shipped.Layers], shipped.Manifest, MoverImageBundle.ManifestMediaType, shipped.Tag, log, cancellationToken)
            .ConfigureAwait(false);
        var pushed = new MoverPush(config.Registry, config.Repository, shipped.Tag, digest, shipped.Version, time.GetUtcNow());
        await SetAsync(PushedKey, JsonSerializer.Serialize(pushed), cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Mover image {Version} pushed: {Reference}", shipped.Version, pushed.Reference);
        return pushed;
    }

    internal async Task<MoverRegistryConfig> GetConfigAsync(CancellationToken cancellationToken)
    {
        var stored = await GetAsync(ConfigKey, cancellationToken).ConfigureAwait(false);
        return stored is null
            ? new MoverRegistryConfig(string.Empty, "backdafuckup/mover", null, null, MoverPullSecretModes.Create, null, null, null, null)
            : JsonSerializer.Deserialize<MoverRegistryConfig>(protector.Unprotect(stored))!;
    }

    private async Task<MoverPush?> GetPushedAsync(CancellationToken cancellationToken) =>
        await GetAsync(PushedKey, cancellationToken).ConfigureAwait(false) is { } json ? JsonSerializer.Deserialize<MoverPush>(json) : null;

    private async Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return (await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, cancellationToken).ConfigureAwait(false))?.Value;
    }

    private async Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var stored = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            db.Settings.Add(new AppSetting { Key = key, Value = value });
        }
        else
        {
            stored.Value = value;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The product version without the commit (<c>0.3.0</c>).</summary>
    private static string ProductVersion() =>
        (typeof(MoverRegistry).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0").Split('+')[0];

    /// <summary>A repository name of the distribution spec.</summary>
    [GeneratedRegex(@"^[a-z0-9]+((\.|_|__|-+)[a-z0-9]+)*(/[a-z0-9]+((\.|_|__|-+)[a-z0-9]+)*)*$")]
    private static partial Regex RepositoryName();

    /// <summary>A DNS-1123 subdomain, as Kubernetes object names.</summary>
    [GeneratedRegex(@"^[a-z0-9]([-a-z0-9]*[a-z0-9])?(\.[a-z0-9]([-a-z0-9]*[a-z0-9])?)*$")]
    private static partial Regex SecretName();
}

/// <summary>The stored settings (encrypted as a whole: they hold the passwords).</summary>
internal sealed record MoverRegistryConfig(
    string Registry, string Repository, string? UserName, string? Password, string PullSecret, string? PullUserName, string? PullPassword, string? ExistingSecret,
    string? UserAgent = null);
