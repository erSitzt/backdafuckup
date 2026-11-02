using System.Net.Security;
using System.Security.Authentication;

namespace Backdafuckup.Nbd;

public enum NbdTlsMode
{
    /// <summary>Never use TLS (fails if the server requires it).</summary>
    Disabled,

    /// <summary>Use TLS; fail if the server does not support it.</summary>
    Required,
}

public sealed record NbdConnectOptions
{
    public required string Host { get; init; }

    public int Port { get; init; } = NbdProtocol.DefaultPort;

    public required string ExportName { get; init; }

    public NbdTlsMode TlsMode { get; init; } = NbdTlsMode.Required;

    /// <summary>Host name used for SNI during the TLS handshake (defaults to <see cref="Host"/>).</summary>
    public string? TlsTargetHost { get; init; }

    /// <summary>Server certificate validation (e.g. public-key pinning). Required when TLS is used.</summary>
    public RemoteCertificateValidationCallback? ServerCertificateValidation { get; init; }

    public SslProtocols TlsProtocols { get; init; } = SslProtocols.Tls12 | SslProtocols.Tls13;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum number of requests in flight on one connection.</summary>
    public int MaxInFlight { get; init; } = 8;
}

/// <summary>Error reported by an NBD server or a protocol violation.</summary>
public class NbdException : IOException
{
    public NbdException(string message)
        : base(message)
    {
    }

    public NbdException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>The server rejected an option during negotiation.</summary>
public sealed class NbdOptionException(uint option, uint replyType, string? serverMessage)
    : NbdException($"NBD server rejected option {option} with {NbdProtocol.OptionReplyName(replyType)}" +
                   (string.IsNullOrEmpty(serverMessage) ? string.Empty : $": {serverMessage}"))
{
    public uint Option { get; } = option;

    public uint ReplyType { get; } = replyType;

    public string? ServerMessage { get; } = serverMessage;
}

/// <summary>The server returned an error for a transmission request.</summary>
public sealed class NbdCommandException(ushort command, uint error, long offset, int length)
    : NbdException($"NBD command {command} at offset {offset} (+{length}) failed: {NbdProtocol.ErrorName(error)}")
{
    public uint Error { get; } = error;
}
