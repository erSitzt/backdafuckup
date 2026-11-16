using Backdafuckup.Contracts;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Kubernetes;
using Backdafuckup.Server.Api;
using Backdafuckup.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Clusters;

/// <summary>Creates the HTTP handler for usage sources (null: the source's own, with its TLS rules); tests replace it.</summary>
public sealed class KubernetesUsageTransport(Func<HttpMessageHandler?> create)
{
    public KubernetesUsageTransport()
        : this(() => null)
    {
    }

    public HttpMessageHandler? Create() => create();
}

/// <summary>
/// The usage source of each Kubernetes cluster (a Prometheus API, spec 2026-11-12-1952-kubernetes-file-level-volumes): kept
/// on the connection, its password or token protected like credentials.
/// </summary>
public sealed class KubernetesUsageSources(IDbContextFactory<AppDbContext> dbFactory, CredentialProtector protector, KubernetesUsageTransport transport) : IKubernetesUsageSources
{
    public async Task<IKubernetesUsageSource?> ForAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.Connections.AsNoTracking().Where(c => c.Id == connectionId)
            .Select(c => new { c.UsageSource, c.UsageSecret }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return row?.UsageSource is { } config ? Create(config, row.UsageSecret) : null;
    }

    public PrometheusUsageSource Create(KubernetesUsageConfig config, string? protectedSecret) =>
        new(config, protectedSecret is null ? null : protector.Unprotect(protectedSecret), transport.Create());

    /// <summary><c>/api/connections/{id}/usage-source</c>: a cluster's usage source, its test and its removal.</summary>
    public static void Map(RouteGroupBuilder connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        connections.MapGet("/usage-queries", () => new KubernetesUsageQueries(
            KubernetesUsageConfig.DefaultPodVolumesQuery, KubernetesUsageConfig.DefaultContainerLayersQuery, KubernetesUsageConfig.DefaultClaimsQuery,
            KubernetesUsageConfig.DefaultContainerWritesQuery))
            .WithDescription("The default queries of a cluster's usage source.");

        connections.MapPost("/{id:guid}/usage-source/test", async (Guid id, SaveKubernetesUsageSourceDto request, AppDbContext db, KubernetesUsageSources sources, CancellationToken ct) =>
        {
            var connection = await db.Connections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
            if (Check(connection, request, out var config) is { } refused)
            {
                return refused;
            }

            using var source = sources.Create(config!, SecretOf(request, connection!, sources.Protector));
            var test = await source.TestAsync(ct);
            return Results.Ok(new KubernetesUsageTestDto(test.PodVolumes, test.ContainerLayers, test.Claims, test.ContainerWrites, test.Problems, test.Untrusted?.ToDto()));
        }).WithDescription("Runs the queries of a usage source (not saved) for the whole cluster: the series each found, what failed, and a certificate to trust.");

        connections.MapPut("/{id:guid}/usage-source", async (Guid id, SaveKubernetesUsageSourceDto request, AppDbContext db, ClusterAgents agents, KubernetesUsageSources sources, CancellationToken ct) =>
        {
            var connection = await db.Connections.Include(c => c.Credential).Include(c => c.Hosts).FirstOrDefaultAsync(c => c.Id == id, ct);
            if (Check(connection, request, out var config) is { } refused)
            {
                return refused;
            }

            connection!.UsageSecret = SecretOf(request, connection, sources.Protector);
            connection.UsageSource = config;
            await db.SaveChangesAsync(ct);
            return Results.Ok(InfrastructureEndpoints.WithAgent(connection, agents));
        }).WithDescription("Saves a cluster's usage source. A null secret keeps the stored one, an empty one removes it.");

        connections.MapDelete("/{id:guid}/usage-source", async (Guid id, AppDbContext db, ClusterAgents agents, CancellationToken ct) =>
        {
            var connection = await db.Connections.Include(c => c.Credential).Include(c => c.Hosts).FirstOrDefaultAsync(c => c.Id == id, ct);
            if (connection is null)
            {
                return Results.NotFound();
            }

            connection.UsageSource = null;
            connection.UsageSecret = null;
            await db.SaveChangesAsync(ct);
            return Results.Ok(InfrastructureEndpoints.WithAgent(connection, agents));
        }).WithDescription("Removes a cluster's usage source.");
    }

    internal CredentialProtector Protector => protector;

    private static IResult? Check(HypervisorConnection? connection, SaveKubernetesUsageSourceDto request, out KubernetesUsageConfig? config)
    {
        config = null;
        if (connection is null)
        {
            return Results.NotFound();
        }

        if (connection.Kind != HypervisorKind.Kubernetes)
        {
            return Results.Problem("Only Kubernetes clusters have usage numbers.", statusCode: StatusCodes.Status400BadRequest);
        }

        config = new KubernetesUsageConfig(
            request.Url.Trim(), Blank(request.Username), Blank(request.CaPem), Blank(request.PinnedSpkiSha256), Blank(request.Filter),
            Blank(request.PodVolumesQuery), Blank(request.ContainerLayersQuery), Blank(request.ClaimsQuery), Blank(request.ContainerWritesQuery));
        return KubernetesUsageConfig.Problem(config) is { } problem ? Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest) : null;
    }

    /// <summary>The protected secret to use: the request's (empty: none), or the stored one.</summary>
    private static string? SecretOf(SaveKubernetesUsageSourceDto request, HypervisorConnection connection, CredentialProtector protector) => request.Secret switch
    {
        null => connection.UsageSecret,
        "" => null,
        { } secret => protector.Protect(secret),
    };

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
