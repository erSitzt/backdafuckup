using System.Text;
using System.Text.Json;
using Backdafuckup.Contracts;
using Backdafuckup.Server.Data;
using Microsoft.EntityFrameworkCore;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Backdafuckup.Server.Api;

/// <summary>Linux credentials for restores over SSH (/api/credentials/linux) and the trusted SSH host keys (/api/ssh-host-keys).</summary>
public static class LinuxCredentialEndpoints
{
    public static void MapLinuxCredentialEndpoints(this IEndpointRouteBuilder app)
    {
        var linux = app.MapGroup("/api/credentials/linux");

        linux.MapGet("/", async (AppDbContext db, CredentialProtector protector, CancellationToken ct) =>
            (await db.Credentials.AsNoTracking().Where(c => c.Kind == CredentialKind.Linux).OrderBy(c => c.Username).ToListAsync(ct))
                .Select(c => ToDto(c, Unprotect(protector, c))).ToList());

        linux.MapPost("/", async (SaveLinuxCredentialDto request, AppDbContext db, CredentialProtector protector, TimeProvider time, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.UserName))
            {
                return Results.Problem("Enter the user name of the Linux account.", statusCode: StatusCodes.Status400BadRequest);
            }

            if (string.IsNullOrEmpty(request.Password) && string.IsNullOrWhiteSpace(request.PrivateKey))
            {
                return Results.Problem("Enter a password or a private key.", statusCode: StatusCodes.Status400BadRequest);
            }

            if (!string.IsNullOrWhiteSpace(request.PrivateKey))
            {
                try
                {
                    using var key = new MemoryStream(Encoding.UTF8.GetBytes(request.PrivateKey.Trim() + "\n"));
                    _ = new PrivateKeyFile(key, string.IsNullOrEmpty(request.Passphrase) ? null : request.Passphrase);
                }
                catch (Exception ex) when (ex is SshException or ArgumentException or InvalidOperationException or NotSupportedException)
                {
                    return Results.Problem($"The private key cannot be read: {ex.Message} (OpenSSH or PEM format; the passphrase if it has one).", statusCode: StatusCodes.Status400BadRequest);
                }
            }

            var secret = new LinuxSecret(
                string.IsNullOrEmpty(request.Password) ? null : request.Password,
                string.IsNullOrWhiteSpace(request.PrivateKey) ? null : request.PrivateKey.Trim() + "\n",
                string.IsNullOrEmpty(request.Passphrase) ? null : request.Passphrase,
                string.IsNullOrEmpty(request.SudoPassword) ? null : request.SudoPassword);
            var now = time.GetUtcNow();
            var credential = new Credential
            {
                Kind = CredentialKind.Linux,
                Username = request.UserName.Trim(),
                ProtectedSecret = protector.Protect(JsonSerializer.Serialize(secret)),
                Description = request.Description,
                CreatedUtc = now,
                UpdatedUtc = now,
            };
            db.Credentials.Add(credential);
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(credential, secret));
        });

        linux.MapDelete("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
            await db.Credentials.Where(c => c.Id == id && c.Kind == CredentialKind.Linux).ExecuteDeleteAsync(ct) > 0 ? Results.NoContent() : Results.NotFound());

        var hosts = app.MapGroup("/api/ssh-host-keys");

        hosts.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
            (await db.SshHostKeys.AsNoTracking().OrderBy(k => k.Host).ThenBy(k => k.Port).ToListAsync(ct))
                .Select(k => new SshHostKeyDto(k.Id, k.Host, k.Port, k.KeyType, k.Fingerprint, k.TrustedUtc, k.TrustedBy)).ToList());

        // Forgetting a key: the next connection trusts whatever key the host presents (after a reinstall).
        hosts.MapDelete("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
            await db.SshHostKeys.Where(k => k.Id == id).ExecuteDeleteAsync(ct) > 0 ? Results.NoContent() : Results.NotFound());
    }

    /// <summary>The secret of a Linux credential.</summary>
    public static LinuxSecret Unprotect(CredentialProtector protector, Credential credential) =>
        JsonSerializer.Deserialize<LinuxSecret>(protector.Unprotect(credential.ProtectedSecret)) ?? new LinuxSecret(null, null, null, null);

    private static LinuxCredentialDto ToDto(Credential c, LinuxSecret secret) =>
        new(c.Id, c.Username, c.Description, secret.PrivateKey is not null, secret.SudoPassword is not null, c.CreatedUtc);
}
