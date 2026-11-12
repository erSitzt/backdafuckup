using Backdafuckup.Contracts;
using Backdafuckup.Core.Hypervisor;
using Backdafuckup.Core.Repository;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Api;

public static class OperationEndpoints
{
    public static void MapOperationEndpoints(this IEndpointRouteBuilder app)
    {
        MapJobs(app.MapGroup("/api/jobs"));
        MapSessions(app.MapGroup("/api/sessions"));
        MapRestorePoints(app.MapGroup("/api/restore-points"));

        app.MapPost("/api/restores", async (RestoreRequestDto request, AppDbContext db, SessionQueue queue, HttpContext http, CancellationToken ct) =>
        {
            var point = await db.RestorePoints.FindAsync([request.RestorePointId], ct);
            if (point is not { State: RestorePointState.Available })
            {
                return Results.Problem("The restore point does not exist or is not available.", statusCode: StatusCodes.Status404NotFound);
            }

            if (request.Mode == RestoreModeDto.OriginalLocation && request.TargetConnectionId is { } target && target != point.ConnectionId)
            {
                return Results.Problem("Restoring to the original location requires the original pool.", statusCode: StatusCodes.Status400BadRequest);
            }

            // A namespace goes into a cluster: its own, or another one (alternate location) with its StorageClasses mapped.
            if (point.ObjectKind == RestorePointObjectKinds.KubernetesNamespace
                && (request.TargetConnectionId ?? point.ConnectionId) is { } cluster
                && await db.Connections.FindAsync([cluster], ct) is not { Kind: Core.Hypervisor.HypervisorKind.Kubernetes })
            {
                return Results.Problem("A namespace can only be restored into a Kubernetes cluster: the target connection is none (or no longer exists).", statusCode: StatusCodes.Status400BadRequest);
            }

            var id = await queue.EnqueueRestoreAsync(request, point.VmName, http.User.Identity?.Name ?? "user", ct);
            return Results.Ok(new StartedDto(id));
        });

        app.MapGet("/api/dashboard", async (AppDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var since = time.GetUtcNow().AddHours(-24);
            var recent = await db.Sessions.Where(s => s.Kind == SessionKind.Backup && s.EndedUtc >= since).ToListAsync(ct);
            var running = await db.Sessions.Where(s => s.State == SessionState.Running || s.State == SessionState.Stopping || s.State == SessionState.Queued)
                .OrderBy(s => s.CreatedUtc).ToListAsync(ct);
            var latest = await db.Sessions.OrderByDescending(s => s.Kind == SessionKind.SqlBackup && s.EndedUtc != null ? s.EndedUtc : s.CreatedUtc).Take(10).ToListAsync(ct);
            var vms = await db.Inventory.Where(i => i.Kind == InventoryKind.Vm).Select(i => i.NativeId).Distinct().ToListAsync(ct);
            var protectedVms = await db.RestorePoints.Where(r => r.State == RestorePointState.Available).Select(r => r.VmNativeId).Distinct().ToListAsync(ct);
            return new DashboardDto(
                recent.Count(s => s.State == SessionState.Succeeded),
                recent.Count(s => s.State == SessionState.Warning),
                recent.Count(s => s.State is SessionState.Failed or SessionState.Stopped),
                vms.Count(protectedVms.Contains),
                vms.Count(v => !protectedVms.Contains(v)),
                running.Select(s => s.ToDto()).ToList(),
                latest.Select(s => s.ToDto()).ToList(),
                await InfrastructureEndpoints.ListRepositoriesAsync(db, ct));
        });
    }

    private static void MapJobs(RouteGroupBuilder jobs)
    {
        jobs.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
        {
            var running = await RunningJobIdsAsync(db, ct);
            return (await JobQuery(db).OrderBy(j => j.Name).ToListAsync(ct)).Select(j => j.ToDto(running.Contains(j.Id)));
        });

        jobs.MapGet("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
            await JobQuery(db).FirstOrDefaultAsync(j => j.Id == id, ct) is { } job
                ? Results.Ok(job.ToDto((await RunningJobIdsAsync(db, ct)).Contains(id)))
                : Results.NotFound());

        jobs.MapPost("/", async (SaveJobDto request, AppDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var problem = await ValidateAsync(request, null, db, ct);
            if (problem is not null)
            {
                return problem;
            }

            var job = new BackupJob { Name = request.Name };
            Apply(job, request, time);
            db.Jobs.Add(job);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/jobs/{job.Id}", new { job.Id });
        });

        jobs.MapPut("/{id:guid}", async (Guid id, SaveJobDto request, AppDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var job = await db.Jobs.Include(j => j.Objects).FirstOrDefaultAsync(j => j.Id == id, ct);
            if (job is null)
            {
                return Results.NotFound();
            }

            var problem = await ValidateAsync(request, id, db, ct);
            if (problem is not null)
            {
                return problem;
            }

            if (job.RepositoryId != request.RepositoryId && (await RunningJobIdsAsync(db, ct)).Contains(id))
            {
                return Results.Problem("The repository cannot be changed while the job is running.", statusCode: StatusCodes.Status409Conflict);
            }

            db.JobObjects.RemoveRange(job.Objects);
            job.Objects = [];
            Apply(job, request, time);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        jobs.MapDelete("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            if ((await RunningJobIdsAsync(db, ct)).Contains(id))
            {
                return Results.Problem("Stop the job before deleting it.", statusCode: StatusCodes.Status409Conflict);
            }

            // Restore points stay available for restores; they just lose the link to the job.
            await db.RestorePoints.Where(r => r.JobId == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.JobId, (Guid?)null), ct);
            await db.Jobs.Where(j => j.Id == id).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });

        jobs.MapPost("/{id:guid}/run", async (Guid id, RunJobDto request, SessionQueue queue, HttpContext http, CancellationToken ct) =>
        {
            try
            {
                var sessionId = await queue.EnqueueBackupAsync(id, request.ActiveFull ? SessionRunMode.ActiveFull : SessionRunMode.Incremental, http.User.Identity?.Name ?? "user", ct);
                return Results.Ok(new StartedDto(sessionId));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        jobs.MapPost("/{id:guid}/enabled/{enabled:bool}", async (Guid id, bool enabled, AppDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var job = await db.Jobs.FindAsync([id], ct);
            if (job is null)
            {
                return Results.NotFound();
            }

            job.Enabled = enabled;
            job.NextRunUtc = enabled ? ScheduleCalculator.Next(job.Schedule, time.GetUtcNow()) : null;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    private static void MapSessions(RouteGroupBuilder sessions)
    {
        sessions.MapGet("/", async (AppDbContext db, int? limit, string? kind, Guid? jobId, CancellationToken ct) =>
        {
            var query = db.Sessions.AsQueryable();
            if (Enum.TryParse<SessionKind>(kind, true, out var k))
            {
                query = query.Where(s => s.Kind == k);
            }

            if (jobId is { } job)
            {
                query = query.Where(s => s.JobId == job);
            }

            // A day session of SQL Server backups counts from its last run (it is reopened by every run of the day).
            return (await query.OrderByDescending(s => s.Kind == SessionKind.SqlBackup && s.EndedUtc != null ? s.EndedUtc : s.CreatedUtc)
                .Take(Math.Clamp(limit ?? 100, 1, 1000)).ToListAsync(ct)).Select(s => s.ToDto());
        });

        sessions.MapGet("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
            await db.Sessions.Include(s => s.Tasks).FirstOrDefaultAsync(s => s.Id == id, ct) is { } session
                ? Results.Ok(new SessionDetailDto(session.ToDto(), session.Tasks.OrderBy(t => t.ObjectName).Select(t => t.ToDto()).ToList()))
                : Results.NotFound());

        sessions.MapGet("/{id:guid}/log", async (Guid id, long? afterId, AppDbContext db, CancellationToken ct) =>
            (await db.SessionLog.Where(l => l.SessionId == id && l.Id > (afterId ?? 0)).OrderBy(l => l.Id).Take(5000).ToListAsync(ct)).Select(l => l.ToDto()));

        sessions.MapPost("/{id:guid}/stop", async (Guid id, SessionQueue queue, CancellationToken ct) =>
            await queue.RequestStopAsync(id, ct) ? Results.NoContent() : Results.Problem("The session is not running.", statusCode: StatusCodes.Status409Conflict));
    }

    /// <summary>A namespace's restore point: the entity, its Kubernetes descriptor and its objects; or the problem why not.</summary>
    private static async Task<(RestorePointEntity? Point, KubernetesNamespaceDescriptor? Space, byte[]? Content, IResult? Problem)> NamespaceAsync(
        Guid id, AppDbContext db, RepositoryManager repositories, CancellationToken ct)
    {
        var point = await db.RestorePoints.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.State == RestorePointState.Available, ct);
        if (point is null)
        {
            return (null, null, null, Results.Problem("The restore point does not exist or is not available.", statusCode: StatusCodes.Status404NotFound));
        }

        var repository = await repositories.GetAsync(point.RepositoryId, ct);
        await using var reader = await repository.OpenRestorePointAsync(id, ct);
        if (reader.Manifest is not { Kubernetes: { } space, Metadata: { } metadata })
        {
            return (point, null, null, Results.Problem("The restore point is not one of a Kubernetes namespace.", statusCode: StatusCodes.Status400BadRequest));
        }

        await using var blob = await reader.OpenBlobAsync(metadata, ct);
        using var memory = new MemoryStream();
        await blob.CopyToAsync(memory, ct);
        return (point, space, memory.ToArray(), null);
    }

    private static void MapRestorePoints(RouteGroupBuilder points)
    {
        points.MapGet("/{id:guid}/kubernetes", async (Guid id, AppDbContext db, RepositoryManager repositories, CancellationToken ct) =>
        {
            var point = await db.RestorePoints.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.State == RestorePointState.Available, ct);
            if (point is null)
            {
                return Results.Problem("The restore point does not exist or is not available.", statusCode: StatusCodes.Status404NotFound);
            }

            var repository = await repositories.GetAsync(point.RepositoryId, ct);
            await using var reader = await repository.OpenRestorePointAsync(id, ct);
            if (reader.Manifest is not { Kubernetes: { } space, Metadata: { } metadata })
            {
                return Results.Problem("The restore point is not one of a Kubernetes namespace.", statusCode: StatusCodes.Status400BadRequest);
            }

            byte[] content;
            await using (var blob = await reader.OpenBlobAsync(metadata, ct))
            {
                using var memory = new MemoryStream();
                await blob.CopyToAsync(memory, ct);
                content = memory.ToArray();
            }

            return Results.Ok(new KubernetesRestorePointDto(
                space.Cluster,
                space.Namespace,
                space.KubernetesVersion,
                [.. space.Volumes.Select(v => new KubernetesVolumeDto(v.Claim, v.DataPath, v.Reason, v.StorageClass, v.SizeBytes, v.VolumeMode))],
                [.. Backdafuckup.Kubernetes.KubernetesRestorePoints.Objects(content).Select(o => new KubernetesObjectDto(o.ApiVersion, o.Kind, o.Name, o.Namespace, o.Controlled, o.App, o.Needs))],
                [.. space.Hooks.Select(h => new KubernetesHookDto(h.Phase, h.Pod, h.Container, h.Command, h.ExitCode, h.Error, h.Seconds))]));
        }).WithDescription("A restore point of a Kubernetes namespace: its volumes (with or without data), its objects (with their app and, for workloads, what a restore of them brings along) and its hooks.");

        points.MapGet("/{id:guid}/kubernetes/compare", async (Guid id, AppDbContext db, RepositoryManager repositories, ConnectionManager connections, TimeProvider time, CancellationToken ct) =>
        {
            var (point, space, content, problem) = await NamespaceAsync(id, db, repositories, ct);
            if (problem is not null)
            {
                return problem;
            }

            if (point!.ConnectionId is not { } cluster)
            {
                return Results.Problem("The cluster of this restore point is no longer connected.", statusCode: StatusCodes.Status409Conflict);
            }

            try
            {
                await using var session = await connections.OpenAsync(cluster, ct);
                var live = await ((Backdafuckup.Kubernetes.IKubernetesNamespaceReader)session).ReadObjectsAsync(space!.Namespace, ct);
                return Results.Ok(new KubernetesComparisonDto(space.Namespace, time.GetUtcNow(),
                    [.. Backdafuckup.Kubernetes.KubernetesCompare.Compare(content!, live).Select(c => new KubernetesObjectStateDto(c.ApiVersion, c.Kind, c.Name, c.App, c.Controlled, c.State))]));
            }
            catch (Exception ex) when (ex is Backdafuckup.Kubernetes.KubernetesOperationException or HttpRequestException or k8s.Autorest.HttpOperationException)
            {
                return Results.Problem($"The namespace {space!.Namespace} could not be read in its cluster: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
            }
        }).WithDescription("Compares the objects of a namespace's restore point with the live namespace: same, changed, missing (deleted since) or new (made since).");

        points.MapPost("/{id:guid}/kubernetes/restore-plan", async (
            Guid id, KubernetesRestorePlanRequestDto request, AppDbContext db, RepositoryManager repositories, ConnectionManager connections, CancellationToken ct) =>
        {
            var (point, space, content, problem) = await NamespaceAsync(id, db, repositories, ct);
            if (problem is not null)
            {
                return problem;
            }

            var targetId = request.TargetConnectionId ?? point!.ConnectionId;
            if (targetId is not { } cluster || await db.Connections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == targetId, ct) is not { Kind: Core.Hypervisor.HypervisorKind.Kubernetes } connection)
            {
                return Results.Problem("A namespace can only be restored into a Kubernetes cluster: the target connection is none (or no longer exists).", statusCode: StatusCodes.Status400BadRequest);
            }

            var target = string.IsNullOrWhiteSpace(request.TargetNamespace) ? space!.Namespace : request.TargetNamespace.Trim();
            var other = cluster != point!.ConnectionId && !string.Equals(connection.PlatformId, point.PlatformId, StringComparison.Ordinal);
            try
            {
                await using var session = await connections.OpenAsync(cluster, ct);
                var plan = await ((Backdafuckup.Kubernetes.IKubernetesRestorePlanner)session).PlanRestoreAsync(space!, content!, target, request.StorageClassMap, other, ct);
                return Results.Ok(new KubernetesRestorePlanDto(
                    cluster, plan.Cluster, plan.TargetNamespace, plan.OtherCluster, plan.NamespaceExists, plan.StorageClasses, plan.DefaultStorageClass,
                    [.. plan.Volumes.Select(v => new KubernetesVolumePlanDto(v.Claim, v.SourceStorageClass, v.TargetStorageClass, v.Path, v.Reason, v.SizeBytes))],
                    plan.Warnings, plan.Errors));
            }
            catch (Exception ex) when (ex is Backdafuckup.Kubernetes.KubernetesOperationException or HttpRequestException or k8s.Autorest.HttpOperationException)
            {
                return Results.Problem($"Cluster {connection.DisplayName} could not be read for the plan: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
            }
        }).WithDescription("Previews a namespace restore into a cluster (the restore point's or another one, with StorageClasses mapped): each PVC's class and data path there (block, files, objects only, missing), warnings and what would stop it. Writes nothing.");

        points.MapGet("/{id:guid}/kubernetes/object", async (Guid id, string kind, string name, bool? live, AppDbContext db, RepositoryManager repositories, ConnectionManager connections, CancellationToken ct) =>
        {
            var (point, space, content, problem) = await NamespaceAsync(id, db, repositories, ct);
            if (problem is not null)
            {
                return problem;
            }

            string? current = null;
            if (live == true && point!.ConnectionId is { } cluster)
            {
                try
                {
                    await using var session = await connections.OpenAsync(cluster, ct);
                    var objects = await ((Backdafuckup.Kubernetes.IKubernetesNamespaceReader)session).ReadObjectsAsync(space!.Namespace, ct);
                    current = objects.FirstOrDefault(o => (string?)o["kind"] == kind && (string?)o["metadata"]?["name"] == name) is { } found
                        ? Backdafuckup.Kubernetes.KubernetesCompare.Show(found)
                        : null;
                }
                catch (Exception ex) when (ex is Backdafuckup.Kubernetes.KubernetesOperationException or HttpRequestException or k8s.Autorest.HttpOperationException)
                {
                    current = $"(the cluster could not be read: {ex.Message})";
                }
            }

            return Results.Ok(new KubernetesObjectDetailDto(kind, name, Backdafuckup.Kubernetes.KubernetesCompare.Show(content!, kind, name), current));
        }).WithDescription("An object of a namespace's restore point as stored and (live=true) as the cluster has it now; Secrets show their keys, never their values.");

        points.MapGet("/", async (AppDbContext db, Guid? jobId, string? vmId, string? platformId, CancellationToken ct) =>
        {
            var query = db.RestorePoints.Include(r => r.Disks).Where(r => r.State == RestorePointState.Available);
            if (jobId is { } job)
            {
                query = query.Where(r => r.JobId == job);
            }

            if (!string.IsNullOrEmpty(vmId))
            {
                query = query.Where(r => r.VmNativeId == vmId);
            }

            // Namespaces of the same name in two clusters: the cluster's platform id (BackupVmDto.PlatformId) tells them apart.
            if (!string.IsNullOrEmpty(platformId))
            {
                query = query.Where(r => r.PlatformId == platformId);
            }

            return (await query.OrderBy(r => r.VmName).ThenByDescending(r => r.CreatedUtc).ToListAsync(ct)).Select(r => r.ToDto());
        });

        points.MapGet("/vms", async (AppDbContext db, TimeProvider time, string? q, Guid[]? jobId, Guid? repositoryId, string[]? content, string? consistency,
            string[]? state, int? olderThanDays, CancellationToken ct) =>
        {
            var filter = new BackupVms.Filter(q, jobId, repositoryId, content, consistency, state, olderThanDays);
            return BackupVms.Validate(filter) is { } problem
                ? Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest)
                : Results.Ok(await BackupVms.ListAsync(db, filter, time.GetUtcNow(), ct));
        }).WithSummary("One row per VM: its jobs, the newest restore point in each and whether it missed a scheduled run")
          .WithDescription("Filters (all optional, repeatable where noted): q (part of the VM's or a job's name), jobId (repeatable; 00000000-0000-0000-0000-000000000000 = restore points without a job, i.e. of a deleted job), repositoryId, content (repeatable: ad, sql), consistency (application, crash, warning), state (repeatable: stale, nopoint, ok), olderThanDays. A VM keeps only the jobs that match. The restore points of a VM: GET /api/restore-points?vmId=…&jobId=… (for a namespace also &platformId=… of its row).");

        points.MapGet("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            if (await db.RestorePoints.Include(r => r.Disks).FirstOrDefaultAsync(r => r.Id == id, ct) is not { } point)
            {
                return Results.NotFound();
            }

            // A restore point with memory has no guest details: the wizard must still know a DC, which never resumes.
            return Results.Ok(point.MemoryImageBytes is > 0 && point.DomainController != true
                ? point.ToDto() with { DomainControllerEvidence = await DomainControllerEvidence.FindAsync(db, point, ct) }
                : point.ToDto());
        });

        points.MapPost("/{id:guid}/verify", async (Guid id, bool? full, AppDbContext db, RepositoryManager repositories, CancellationToken ct) =>
        {
            var point = await db.RestorePoints.FindAsync([id], ct);
            if (point is null)
            {
                return Results.NotFound();
            }

            var repository = await repositories.GetAsync(point.RepositoryId, ct);
            var result = await repository.VerifyAsync(id, full == true ? VerifyDepth.Full : VerifyDepth.Quick, null, ct);
            return Results.Ok(new VerifyResultDto(result.Ok, result.CheckedChunks, result.Problems));
        });

        points.MapDelete("/{id:guid}", async (Guid id, AppDbContext db, RepositoryManager repositories, ConnectionManager connections, SessionQueue queue, CancellationToken ct) =>
        {
            var point = await db.RestorePoints.Include(r => r.Disks).FirstOrDefaultAsync(r => r.Id == id, ct);
            if (point is null)
            {
                return Results.NotFound();
            }

            var repository = await repositories.GetAsync(point.RepositoryId, ct);
            try
            {
                await repository.DeleteRestorePointAsync(id, ct);
            }
            catch (RestorePointPinnedException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }

            var tokens = point.Disks.Where(d => d.ChangeTokenValue is not null).Select(d => new ChangeToken(d.ChangeTokenKind!, d.ChangeTokenValue!)).ToList();
            if (tokens.Count > 0 && point.ConnectionId is { } connectionId)
            {
                try
                {
                    await using var session = await connections.OpenAsync(connectionId, ct);
                    await session.ReleaseChangeTokensAsync(tokens, Core.Operations.NullOperationLog.Instance, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The next backup of this VM simply makes a full read.
                }
            }

            db.RestorePoints.Remove(point);
            await db.SaveChangesAsync(ct);
            var repositoryName = (await db.Repositories.FindAsync([point.RepositoryId], ct))?.Name ?? "repository";
            await queue.EnqueueMaintenanceAsync(point.RepositoryId, repositoryName, "Restore point deleted", ct);
            return Results.NoContent();
        });
    }

    private static IQueryable<BackupJob> JobQuery(AppDbContext db) =>
        db.Jobs.Include(j => j.Objects).Include(j => j.Connection).Include(j => j.Repository);

    private static async Task<HashSet<Guid>> RunningJobIdsAsync(AppDbContext db, CancellationToken ct) =>
        (await db.Sessions.Where(s => s.JobId != null && (s.State == SessionState.Queued || s.State == SessionState.Running || s.State == SessionState.Stopping))
            .Select(s => s.JobId!.Value).ToListAsync(ct)).ToHashSet();

    private static async Task<IResult?> ValidateAsync(SaveJobDto request, Guid? id, AppDbContext db, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors["name"] = ["A name is required."];
        }
        else if (await db.Jobs.AnyAsync(j => j.Name == request.Name && j.Id != id, ct))
        {
            errors["name"] = ["A job with this name already exists."];
        }

        var connectionKind = await db.Connections.Where(c => c.Id == request.ConnectionId).Select(c => (HypervisorKind?)c.Kind).FirstOrDefaultAsync(ct);
        var cluster = connectionKind == HypervisorKind.Kubernetes;
        if (request.Objects.Count == 0)
        {
            errors["objects"] = [cluster ? "Select at least one namespace." : "Select at least one VM."];
        }

        // Namespaces by labels: only on a cluster, and only a selector the cluster understands.
        foreach (var selector in request.Objects.Where(o => KubernetesLabelSelector.IsJobObject(o.NativeId)))
        {
            if (!cluster)
            {
                errors["objects"] = ["Only namespaces of a Kubernetes cluster are chosen by labels."];
            }
            else if (KubernetesLabelSelector.Parse(KubernetesLabelSelector.Of(selector.NativeId)).Error is { } invalid)
            {
                errors["objects"] = [invalid];
            }
        }

        // A namespace has no guest and no memory: its consistency comes from its hooks.
        if (cluster && (request.GuestProcessing || request.Objects.Any(o => o.WithMemory)))
        {
            errors["guestProcessing"] = ["Namespaces of a Kubernetes cluster have no guest processing and no snapshots with memory; hooks in their pods make them application-consistent."];
        }

        if (request.RetentionPoints is < 1 or > 10_000)
        {
            errors["retentionPoints"] = ["Keep between 1 and 10000 restore points."];
        }

        if (connectionKind is null)
        {
            errors["connectionId"] = ["Unknown pool or cluster."];
        }

        if (!await db.Repositories.AnyAsync(r => r.Id == request.RepositoryId, ct))
        {
            errors["repositoryId"] = ["Unknown repository."];
        }

        if (!Enum.TryParse<Backdafuckup.Core.DataMover.DiskTransportMode>(request.TransportMode, true, out _) || int.TryParse(request.TransportMode, out _))
        {
            errors["transportMode"] = [$"Unknown transport mode '{request.TransportMode}'. Use Automatic, NbdOnly or HttpExportOnly."];
        }

        if (!Enum.TryParse<GuestProcessingMode>(request.GuestProcessingMode, true, out var guestMode) || int.TryParse(request.GuestProcessingMode, out _))
        {
            errors["guestProcessingMode"] = [$"Unknown guest processing mode '{request.GuestProcessingMode}'. Use Vss or SqlSnapshot."];
        }
        else if (request.GuestProcessing && guestMode == GuestProcessingMode.SqlSnapshot && request.SqlCredentialId is null)
        {
            errors["sqlCredentialId"] = ["The SQL Server snapshot needs an account with the right to back up the databases (sysadmin): a Windows account or a SQL Server login."];
        }
        else if (request.GuestProcessing && guestMode == GuestProcessingMode.SqlSnapshot
                 && !await db.Credentials.AnyAsync(c => c.Id == request.SqlCredentialId && (c.Kind == CredentialKind.Windows || c.Kind == CredentialKind.SqlServer), ct))
        {
            errors["sqlCredentialId"] = ["Unknown account: choose a Windows account or a SQL Server login under Credentials."];
        }

        if (request.Schedule.Kind.Equals("Weekly", StringComparison.OrdinalIgnoreCase) && request.Schedule.Days.Count == 0)
        {
            errors["schedule"] = ["Select at least one weekday."];
        }

        if (request.SqlBackup is { Enabled: true } sqlBackup)
        {
            if (!SqlBackupSettingsDto.IsValidInterval(sqlBackup.LogIntervalMinutes))
            {
                errors["sqlBackup.logIntervalMinutes"] = ["Back up the transaction logs every 5, 15 or 30 minutes, or every 60 to 1440 minutes."];
            }

            if (request.SqlCredentialId is null)
            {
                errors["sqlCredentialId"] = ["SQL Server backups need an account with the right to back up the databases (sysadmin): a Windows account or a SQL Server login."];
            }
            else if (!await db.Credentials.AnyAsync(c => c.Id == request.SqlCredentialId && (c.Kind == CredentialKind.Windows || c.Kind == CredentialKind.SqlServer), ct))
            {
                errors["sqlCredentialId"] = ["Unknown account: choose a Windows account or a SQL Server login under Credentials."];
            }

            foreach (var (name, schedule) in new[] { ("fullSchedule", sqlBackup.FullSchedule), ("systemSchedule", sqlBackup.SystemSchedule) })
            {
                if (schedule is not null && schedule.Kind.Equals("Weekly", StringComparison.OrdinalIgnoreCase) && schedule.Days.Count == 0)
                {
                    errors[$"sqlBackup.{name}"] = ["Select at least one weekday."];
                }
            }
        }

        return errors.Count == 0 ? null : Results.ValidationProblem(errors);
    }

    private static void Apply(BackupJob job, SaveJobDto request, TimeProvider time)
    {
        job.Name = request.Name.Trim();
        job.Description = request.Description;
        job.Enabled = request.Enabled;
        job.ConnectionId = request.ConnectionId;
        job.RepositoryId = request.RepositoryId;
        job.RetentionPoints = request.RetentionPoints;
        job.Schedule = request.Schedule.FromDto();
        job.MaxParallelVms = Math.Clamp(request.MaxParallelVms, 1, 16);
        job.NbdConnectionsPerDisk = Math.Clamp(request.NbdConnectionsPerDisk, 1, 12);
        job.MaxParallelDisks = Math.Clamp(request.MaxParallelDisks, 1, 8);
        job.GuestProcessing = request.GuestProcessing;
        job.GuestFailurePolicy = Enum.TryParse<GuestFailurePolicy>(request.GuestFailurePolicy, true, out var policy) ? policy : GuestFailurePolicy.CrashConsistentWithWarning;
        job.GuestCredentialId = request.GuestCredentialId;
        job.GuestProcessingMode = Enum.TryParse<GuestProcessingMode>(request.GuestProcessingMode, true, out var mode) && !int.TryParse(request.GuestProcessingMode, out _) ? mode : GuestProcessingMode.Vss;
        job.SqlBackup = request.SqlBackup.FromDto(job.Schedule.TimeZoneId);
        job.SqlCredentialId = job.GuestProcessingMode == GuestProcessingMode.SqlSnapshot || job.SqlBackup.Enabled ? request.SqlCredentialId : null;
        job.SqlSnapshotCopyOnly = request.SqlSnapshotCopyOnly;
        job.RetryCount = Math.Clamp(request.RetryCount, 0, 10);
        job.TransportMode = Enum.TryParse<Backdafuckup.Core.DataMover.DiskTransportMode>(request.TransportMode, true, out var transport) ? transport : Backdafuckup.Core.DataMover.DiskTransportMode.Automatic;
        job.Objects = request.Objects.Select((o, i) => new BackupJobObject { JobId = job.Id, NativeId = o.NativeId, CachedName = o.Name, Order = i, WithMemory = o.WithMemory }).ToList();
        job.NextRunUtc = job.Enabled ? ScheduleCalculator.Next(job.Schedule, time.GetUtcNow()) : null;
    }
}

/// <summary>Live updates for the web console.</summary>
[Authorize]
public sealed class LiveHub : Hub<ILiveClient>
{
    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, LiveGroups.Dashboard);
        await base.OnConnectedAsync();
    }

    public Task JoinSession(Guid sessionId) => Groups.AddToGroupAsync(Context.ConnectionId, LiveGroups.Session(sessionId));

    public Task LeaveSession(Guid sessionId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, LiveGroups.Session(sessionId));
}

public sealed class SignalRLiveNotifier(IHubContext<LiveHub, ILiveClient> hub) : ILiveNotifier
{
    public Task SessionUpdatedAsync(SessionSummaryDto session) =>
        Task.WhenAll(hub.Clients.Group(LiveGroups.Dashboard).SessionUpdated(session), hub.Clients.Group(LiveGroups.Session(session.Id)).SessionUpdated(session));

    public Task TaskUpdatedAsync(SessionTaskDto task) => hub.Clients.Group(LiveGroups.Session(task.SessionId)).TaskUpdated(task);

    public Task LogEntryUpsertedAsync(LogEntryDto entry) => hub.Clients.Group(LiveGroups.Session(entry.SessionId)).LogEntryUpserted(entry);
}

/// <summary>Rejects state-changing API requests without the custom CSRF header.</summary>
public sealed class CsrfHeaderMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        // The agent interface (/api/agent/*) authenticates with the agent key, and scripts with an API token, not with a
        // cookie a browser sends on its own: no CSRF header needed for them.
        if (request.Path.StartsWithSegments("/api") && !request.Path.StartsWithSegments("/api/agent") && !context.User.IsTokenRequest()
            && !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method)
            && !request.Headers.ContainsKey(ApiHeaders.Csrf))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return context.Response.WriteAsJsonAsync(new { title = $"Missing {ApiHeaders.Csrf} header." });
        }

        return next(context);
    }
}
