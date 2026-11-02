namespace Backdafuckup.Nbd;

/// <summary>
/// Constants of the NBD protocol, implemented from the public-domain specification
/// https://github.com/NetworkBlockDevice/nbd/blob/master/doc/proto.md (fixed newstyle negotiation, simple replies).
/// </summary>
public static class NbdProtocol
{
    public const int DefaultPort = 10809;

    // Handshake
    public const ulong NbdMagic = 0x4e42444d41474943; // "NBDMAGIC"
    public const ulong IHaveOpt = 0x49484156454F5054; // "IHAVEOPT"
    public const ulong OldStyleMagic = 0x00420281861253;
    public const ulong OptionReplyMagic = 0x0003e889045565a9;

    // Handshake flags (server) / client flags
    public const ushort FlagFixedNewstyle = 1 << 0;
    public const ushort FlagNoZeroes = 1 << 1;
    public const uint ClientFlagFixedNewstyle = 1 << 0;
    public const uint ClientFlagNoZeroes = 1 << 1;

    // Options
    public const uint OptExportName = 1;
    public const uint OptAbort = 2;
    public const uint OptList = 3;
    public const uint OptStartTls = 5;
    public const uint OptInfo = 6;
    public const uint OptGo = 7;

    // Option reply types
    public const uint RepAck = 1;
    public const uint RepServer = 2;
    public const uint RepInfo = 3;
    public const uint RepFlagError = 1u << 31;
    public const uint RepErrUnsup = RepFlagError | 1;
    public const uint RepErrPolicy = RepFlagError | 2;
    public const uint RepErrInvalid = RepFlagError | 3;
    public const uint RepErrPlatform = RepFlagError | 4;
    public const uint RepErrTlsReqd = RepFlagError | 5;
    public const uint RepErrUnknown = RepFlagError | 6;
    public const uint RepErrShutdown = RepFlagError | 7;
    public const uint RepErrBlockSizeReqd = RepFlagError | 8;
    public const uint RepErrTooBig = RepFlagError | 9;

    // Info types
    public const ushort InfoExport = 0;
    public const ushort InfoName = 1;
    public const ushort InfoDescription = 2;
    public const ushort InfoBlockSize = 3;

    // Transmission flags
    public const ushort TransHasFlags = 1 << 0;
    public const ushort TransReadOnly = 1 << 1;
    public const ushort TransSendFlush = 1 << 2;
    public const ushort TransSendFua = 1 << 3;
    public const ushort TransRotational = 1 << 4;
    public const ushort TransSendTrim = 1 << 5;
    public const ushort TransSendWriteZeroes = 1 << 6;
    public const ushort TransCanMultiConn = 1 << 8;

    // Transmission
    public const uint RequestMagic = 0x25609513;
    public const uint SimpleReplyMagic = 0x67446698;
    public const uint StructuredReplyMagic = 0x668e33ef;
    public const ushort CmdRead = 0;
    public const ushort CmdWrite = 1;
    public const ushort CmdDisc = 2;
    public const ushort CmdFlush = 3;

    public const int RequestHeaderSize = 28;
    public const int SimpleReplyHeaderSize = 16;

    /// <summary>Default maximum payload when the server does not announce block sizes (spec: 32 MiB).</summary>
    public const int DefaultMaxPayload = 32 * 1024 * 1024;

    public static string OptionReplyName(uint type) => type switch
    {
        RepAck => "ACK",
        RepServer => "SERVER",
        RepInfo => "INFO",
        RepErrUnsup => "ERR_UNSUP",
        RepErrPolicy => "ERR_POLICY",
        RepErrInvalid => "ERR_INVALID",
        RepErrPlatform => "ERR_PLATFORM",
        RepErrTlsReqd => "ERR_TLS_REQD",
        RepErrUnknown => "ERR_UNKNOWN",
        RepErrShutdown => "ERR_SHUTDOWN",
        RepErrBlockSizeReqd => "ERR_BLOCK_SIZE_REQD",
        RepErrTooBig => "ERR_TOO_BIG",
        _ => $"0x{type:x8}",
    };

    /// <summary>Maps NBD error numbers (errno values defined by the spec) to names.</summary>
    public static string ErrorName(uint error) => error switch
    {
        1 => "EPERM",
        5 => "EIO",
        12 => "ENOMEM",
        22 => "EINVAL",
        28 => "ENOSPC",
        75 => "EOVERFLOW",
        95 => "ENOTSUP",
        108 => "ESHUTDOWN",
        _ => $"errno {error}",
    };
}
