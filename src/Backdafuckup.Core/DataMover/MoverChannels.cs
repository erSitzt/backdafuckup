namespace Backdafuckup.Core.DataMover;

/// <summary>
/// What a mover pod needs to reach the server for one volume: the URL of its channel, where it reports the end of a
/// restore, the one-time token, and the server certificate's SPKI pins (empty: system validation).
/// </summary>
public sealed record MoverEndpoint(Uri DataUrl, Uri ResultUrl, string Token, IReadOnlyList<string> ServerPins);

/// <summary>
/// What a mover reports to <see cref="MoverEndpoint.ResultUrl"/>: after a restore the data bytes it wrote and flushed; an
/// error whenever it fails (a backup mover that cannot read its device reports it here instead of sending data).
/// </summary>
public sealed record MoverResult(long DataBytes, string? Error = null);

/// <summary>A backup channel: the mover sends the volume to <see cref="Endpoint"/>; the server reads it as a sequential disk.</summary>
public interface IMoverBackupChannel : ISequentialDiskReader
{
    MoverEndpoint Endpoint { get; }

    /// <summary>Fails the channel from outside, e.g. when the mover's pod cannot start.</summary>
    void Abort(Exception error);
}

/// <summary>A restore channel: the mover fetches the image from <see cref="Endpoint"/>, writes it and reports the end.</summary>
public interface IMoverRestoreChannel : IAsyncDisposable
{
    MoverEndpoint Endpoint { get; }

    /// <summary>Completes with the data bytes the mover wrote, once it flushed them to the volume; fails with its error.</summary>
    Task<long> Completion { get; }

    /// <summary>Fails the channel from outside, e.g. when the mover's pod cannot start.</summary>
    void Abort(Exception error);
}

/// <summary>Opens the server's side of the channels mover pods connect to.</summary>
public interface IMoverChannels
{
    /// <param name="description">For messages: the volume (cluster, namespace, PVC).</param>
    /// <param name="capacity">The volume's size; the mover sends exactly this many bytes.</param>
    /// <param name="connectTimeout">How long to wait for the mover (default: the server's); longer when its volume is first copied from a snapshot.</param>
    Task<IMoverBackupChannel> OpenBackupAsync(string description, long capacity, CancellationToken cancellationToken, TimeSpan? connectTimeout = null);

    Task<IMoverRestoreChannel> OpenRestoreAsync(string description, IDiskImageSource image, CancellationToken cancellationToken, TimeSpan? connectTimeout = null);
}

/// <summary>What a cluster needs to start movers: the image, and the pull secret (its name, and its content when the server creates it).</summary>
public sealed record MoverDeployment(string Image, string? PullSecretName, string? DockerConfigJson)
{
    /// <summary>The name of the pull secret the server creates in the work namespace.</summary>
    public const string CreatedSecretName = "backdafuckup-registry";
}

/// <summary>The mover image in a registry the clusters can pull from (the server pushes it there when it is not yet).</summary>
public interface IMoverImageSource
{
    /// <param name="log">Gets a line per step of a push.</param>
    Task<MoverDeployment> EnsurePushedAsync(Action<string>? log, CancellationToken cancellationToken);
}
