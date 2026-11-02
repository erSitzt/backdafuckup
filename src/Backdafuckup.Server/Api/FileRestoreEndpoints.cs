using Backdafuckup.Agent.Protocol;
using Backdafuckup.Contracts;
using Backdafuckup.Core.Operations;
using Backdafuckup.FileLevel;
using Backdafuckup.Server.Agents;
using Backdafuckup.Server.Data;
using Backdafuckup.Server.Engine;
using Backdafuckup.Server.FileRestore;
using Backdafuckup.Smb;
using Microsoft.EntityFrameworkCore;

namespace Backdafuckup.Server.Api;

/// <summary>File-level restore: browse a restore point and download files (/api/file-restore).</summary>
public static class FileRestoreEndpoints
{
    public static void MapFileRestoreEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/file-restore");

        group.MapPost("/mounts", async (OpenFileMountRequestDto request, FileRestoreMounts mounts, HttpContext http, CancellationToken ct) =>
            await Handle(async () => Results.Ok(mounts.ToDto(await mounts.OpenAsync(request.RestorePointId, User(http), ct)))));

        group.MapGet("/mounts", (FileRestoreMounts mounts, HttpContext http) =>
            Results.Ok(mounts.ForUser(User(http)).Select(mounts.ToDto).ToList()));

        group.MapGet("/mounts/{id:guid}", (Guid id, FileRestoreMounts mounts, HttpContext http) =>
            Handle(() => Task.FromResult(Results.Ok(mounts.ToDto(mounts.Get(id, User(http)))))));

        group.MapDelete("/mounts/{id:guid}", (Guid id, FileRestoreMounts mounts, HttpContext http) =>
            Handle(async () =>
            {
                await mounts.CloseAsync(mounts.Get(id, User(http)), $"closed by {User(http)}");
                return Results.NoContent();
            }));

        group.MapGet("/mounts/{id:guid}/entries", (Guid id, string volume, string? path, int? offset, int? limit, FileRestoreMounts mounts, HttpContext http, CancellationToken ct) =>
            Handle(async () =>
            {
                var mount = mounts.Get(id, User(http));
                return Results.Ok(await mounts.ListAsync(mount, volume, GuestPath.Parse(path), offset ?? 0, limit ?? 500, ct));
            }));

        group.MapGet("/mounts/{id:guid}/file", (Guid id, string volume, string path, FileRestoreMounts mounts, HttpContext http, CancellationToken ct) =>
            Handle(async () =>
            {
                var mount = mounts.Get(id, User(http));
                await mounts.StreamFileAsync(mount, volume, GuestPath.Parse(path), http.Response, ct);
                return Results.Empty;
            }));

        group.MapPost("/mounts/{id:guid}/downloads", (Guid id, FileDownloadRequestDto request, FileRestoreMounts mounts, FileRestoreDownloads downloads, HttpContext http, CancellationToken ct) =>
            Handle(async () => Results.Ok(await downloads.PrepareZipAsync(mounts.Get(id, User(http)), request, ct))));

        group.MapPost("/restores", (FileRestoreRequestDto request, FileRestoreMounts mounts, SessionQueue queue, AgentService agents, AgentRegistry registry, IDbContextFactory<AppDbContext> dbFactory, HttpContext http, CancellationToken ct) =>
            Handle(async () =>
            {
                var user = User(http);
                var paths = FileRestoreMounts.ParseSelection(request.Paths);
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var point = await db.RestorePoints.AsNoTracking().FirstOrDefaultAsync(r => r.Id == request.RestorePointId, ct)
                    ?? throw new KeyNotFoundException("The restore point no longer exists.");
                if (point.State != RestorePointState.Available)
                {
                    throw new InvalidOperationException($"The restore point of {point.VmName} is not available ({point.State}).");
                }

                // Started from the browser: check the selection now (404) and record the restore in its log.
                BrowseMount? mount = null;
                if (request.MountId is { } mountId)
                {
                    mount = mounts.Get(mountId, user);
                    if (mount.Point.Id != point.Id)
                    {
                        throw new ArgumentException("The file browser shows another restore point.");
                    }

                    await mounts.CheckSelectionAsync(mount, request.Volume, paths, ct);
                }

                string target;
                switch (request.Target.Kind)
                {
                    case FileRestoreTargetKindDto.Share:
                        var unc = UncPath.Parse(request.Target.SharePath ?? throw new ArgumentException("Enter the share as \\\\server\\share\\folder."));
                        if (!await db.Credentials.AnyAsync(c => c.Id == request.Target.CredentialId && c.Kind == CredentialKind.Windows, ct))
                        {
                            throw new ArgumentException("Choose a Windows credential with write access to the share.");
                        }

                        target = unc.ToString();
                        break;

                    case FileRestoreTargetKindDto.Agent:
                        target = await CheckAgentTargetAsync(request, point, mount, agents, registry, ct);
                        break;

                    case FileRestoreTargetKindDto.Ssh:
                        if (!await db.Credentials.AnyAsync(c => c.Id == request.Target.CredentialId && c.Kind == CredentialKind.Linux, ct))
                        {
                            throw new ArgumentException("Choose a Linux credential (Credentials → Linux).");
                        }

                        if (request.Target.Port is < 1 or > 65535)
                        {
                            throw new ArgumentException("The SSH port must be between 1 and 65535.");
                        }

                        var folder = request.Target.Folder;
                        if (!string.IsNullOrWhiteSpace(folder) && SshTargetPaths.Normalize(folder) is null)
                        {
                            throw new ArgumentException($"'{folder}' is not an absolute folder in the VM; enter it as /srv/restore.");
                        }

                        if (mount?.Image.Volumes.FirstOrDefault(v => v.Id == request.Volume) is { } linuxVolume)
                        {
                            if (linuxVolume.FileSystem is not ("ext" or "XFS"))
                            {
                                throw new ArgumentException($"{linuxVolume.FileSystem} files are restored into a VM through the agent, not over SSH.");
                            }

                            if (string.IsNullOrWhiteSpace(folder) && linuxVolume.MountPoint?.StartsWith('/') != true)
                            {
                                throw new ArgumentException("This volume had no mount point in the backup's /etc/fstab, so there is no original location; choose a folder in the VM.");
                            }
                        }

                        target = string.IsNullOrWhiteSpace(folder) ? $"the original location in {point.VmName} over SSH" : $"{SshTargetPaths.Normalize(folder)} in {point.VmName} over SSH";
                        break;

                    default:
                        throw new ArgumentException("This restore target is not supported.");
                }

                var sessionId = await queue.EnqueueFileRestoreAsync(request, point.VmName, user, ct);
                mount?.Log.Info(
                    $"{user} started restoring {string.Join(", ", paths.Take(5).Select(p => p.Name))}{(paths.Count > 5 ? $" and {paths.Count - 5:N0} more" : string.Empty)} " +
                    $"from {FileRestoreMounts.Describe(mount, request.Volume, paths[0].Parent!)} to {target} ({(request.Mode == FileRestoreModeDto.Keep ? "keep existing files" : "overwrite")}); see the session \"Restore files {point.VmName}\".");
                return Results.Ok(new StartedDto(sessionId));
            }));

        group.MapGet("/downloads/{token}", (string token, FileRestoreDownloads downloads, HttpContext http, CancellationToken ct) =>
            Handle(async () =>
            {
                await downloads.StreamZipAsync(token, User(http), http.Response, ct);
                return Results.Empty;
            }));
    }

    /// <summary>The VM's agent must be online and able to restore files; the volume must be a Windows one (409/400 otherwise).</summary>
    private static async Task<string> CheckAgentTargetAsync(FileRestoreRequestDto request, RestorePointEntity point, BrowseMount? mount, AgentService agents, AgentRegistry registry, CancellationToken ct)
    {
        var folder = request.Target.Folder?.Trim();
        if (!string.IsNullOrEmpty(folder) && !AgentTargetPaths.IsLocalAbsolute(folder.Length == 2 ? folder + "\\" : folder))
        {
            throw new ArgumentException($"'{folder}' is not a local folder in the VM; enter it as D:\\Restore.");
        }

        var vmId = request.Target.VmNativeId ?? point.VmNativeId;
        var agent = await agents.FindForVmAsync(vmId, ct)
            ?? throw new InvalidOperationException($"No backdafuckup agent is installed in {(vmId == point.VmNativeId ? point.VmName : "that VM")}. Install it under Guest Agents, or restore to a share.");
        var host = $"{agent.HostName} ({agent.IpAddresses ?? agent.RemoteAddress ?? "address unknown"})";
        if (!agents.IsOnline(agent.Id))
        {
            throw new InvalidOperationException($"The agent in {host} is offline since {agent.LastContactUtc.ToLocalTime():g}. Start the VM, or restore to a share.");
        }

        if (!registry.HasCapability(agent.Id, AgentProtocol.CapabilityRestoreFiles))
        {
            throw new InvalidOperationException($"The agent in {host} (version {agent.AgentVersion}) cannot restore files yet; it updates itself within a few minutes. Try again then.");
        }

        if (mount?.Image.Volumes.FirstOrDefault(v => v.Id == request.Volume) is { } volume)
        {
            if (volume.FileSystem is not ("NTFS" or "FAT"))
            {
                throw new ArgumentException($"{volume.FileSystem} files cannot be restored into a VM through the agent; restore them to a share.");
            }

            if (string.IsNullOrEmpty(folder) && (volume.MountPoint is null || !AgentTargetPaths.IsLocalAbsolute(volume.MountPoint.TrimEnd('\\') + "\\")))
            {
                throw new ArgumentException("This volume had no drive letter in the backup, so there is no original location; choose a folder in the VM.");
            }
        }

        return string.IsNullOrEmpty(folder) ? $"the original location in {agent.HostName}" : $"{folder} in {agent.HostName}";
    }

    private static string User(HttpContext http) => http.User.Identity?.Name ?? "user";

    /// <summary>Maps the expected failures to problem details; the response may already be streaming for downloads.</summary>
    private static async Task<IResult> Handle(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (MountNotFoundException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (MountLimitException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (KeyNotFoundException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (FileNotFoundException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (GuestFileUnsupportedException ex)
        {
            return Results.Problem($"{ex.Path} cannot be restored: {ex.Reason}.", statusCode: StatusCodes.Status409Conflict);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (IOException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }
}
