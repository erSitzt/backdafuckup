using System.Security.Cryptography;
using Backdafuckup.Agent.Protocol;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Hosting;
using Backdafuckup.Core.Tls;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Agents;

/// <summary>
/// Settings the agents need: the enrollment secret (stored protected), the address under which the guests reach the
/// server, and the pin of the server certificate.
/// </summary>
public sealed class AgentSettingsStore(IDbContextFactory<AppDbContext> dbFactory, CredentialProtector protector, ServerPaths paths, IConfiguration configuration)
{
    private const string SecretKey = "agent.enrollmentSecret";
    private const string ServerUrlKey = "agent.serverUrl";
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string> GetEnrollmentSecretAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var stored = await db.Settings.FirstOrDefaultAsync(s => s.Key == SecretKey, cancellationToken).ConfigureAwait(false);
            if (stored is not null)
            {
                return protector.Unprotect(stored.Value);
            }

            var secret = NewSecret();
            db.Settings.Add(new AppSetting { Key = SecretKey, Value = protector.Protect(secret) });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return secret;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>New enrollment secret: enrolled agents keep working with their keys, new installations need the new one.</summary>
    public async Task<string> RenewEnrollmentSecretAsync(CancellationToken cancellationToken)
    {
        var secret = NewSecret();
        await SetAsync(SecretKey, protector.Protect(secret), cancellationToken).ConfigureAwait(false);
        return secret;
    }

    /// <summary>The address agents use, e.g. <c>https://backup.example.local:9555/</c>; null until configured.</summary>
    public async Task<string?> GetServerUrlAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return (await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == ServerUrlKey, cancellationToken).ConfigureAwait(false))?.Value
            ?? configuration["Agent:ServerUrl"];
    }

    public Task SetServerUrlAsync(string url, CancellationToken cancellationToken) => SetAsync(ServerUrlKey, url, cancellationToken);

    /// <summary>
    /// SPKI pin of the server's own self-signed certificate. Empty if Kestrel uses a certificate from the configuration;
    /// the agents then rely on Windows certificate validation (internal CA or public certificate).
    /// </summary>
    public IReadOnlyList<string> ServerPins()
    {
        if (configuration.GetSection("Agent:ServerPins").Get<string[]>() is { Length: > 0 } configured)
        {
            return configured;
        }

        if (configuration.GetSection("Kestrel:Endpoints").Exists() || !File.Exists(paths.CertificatePath))
        {
            return [];
        }

        // Read only: the certificate is created and renewed by the Kestrel setup at startup.
        using var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(paths.CertificatePath, password: null);
        return [CertificatePinning.SpkiSha256(certificate)];
    }

    /// <summary>The <c>agent.json</c> for a new installation.</summary>
    public async Task<AgentConfig> CreateAgentConfigAsync(CancellationToken cancellationToken)
    {
        var url = await GetServerUrlAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The address under which the guests reach the backup server is not set (Guest agents → Settings).");
        return new AgentConfig
        {
            ServerUrl = url.TrimEnd('/') + "/",
            ServerPins = [.. ServerPins()],
            EnrollmentSecret = await GetEnrollmentSecretAsync(cancellationToken).ConfigureAwait(false),
        };
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

    private static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_');
}
