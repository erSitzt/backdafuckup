using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Backdafuckup.Tape.Format;
using Konscious.Security.Cryptography;

namespace Backdafuckup.Tape.Crypto;

/// <summary>
/// Argon2id parameters of a tape key. The defaults (256 MiB, 3 passes, 4 lanes) make guessing a passphrase expensive;
/// they are written into every tape's label, so the recovery command derives the same key.
/// </summary>
public sealed record TapeKdfParameters(int MemoryKiB, int Iterations, int Parallelism, byte[] Salt)
{
    public const string Name = "argon2id";

    public static TapeKdfParameters CreateDefault() => new(256 * 1024, 3, 4, RandomNumberGenerator.GetBytes(16));
}

/// <summary>
/// Keys of tape sets. A passphrase gives the key-encryption key (KEK, Argon2id); the KEK gives every set its own key
/// (HKDF-SHA256 with the set's id), one for software encryption and one for the drive. Only the KEK's fingerprint is
/// ever shown or written on tape.
/// </summary>
public static class TapeKeys
{
    public const string SoftwarePurpose = "bdfu-tape-sw-v1";
    public const string HardwarePurpose = "bdfu-tape-lto-v1";

    public static byte[] DeriveKek(string passphrase, TapeKdfParameters parameters)
    {
        if (string.IsNullOrEmpty(passphrase))
        {
            throw new ArgumentException("The passphrase is empty.", nameof(passphrase));
        }

        var bytes = Encoding.UTF8.GetBytes(passphrase);
        try
        {
            using var argon = new Argon2id(bytes)
            {
                Salt = parameters.Salt,
                MemorySize = parameters.MemoryKiB,
                Iterations = parameters.Iterations,
                DegreeOfParallelism = parameters.Parallelism,
            };
            return argon.GetBytes(32);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>The KEK's fingerprint: HMAC-SHA256(KEK, "bdfu-tape-key-id"), first 8 bytes, as 3F2A-91C0-7B11-02DE.</summary>
    public static string Fingerprint(ReadOnlySpan<byte> kek)
    {
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(kek, "bdfu-tape-key-id"u8, mac);
        var hex = Convert.ToHexString(mac[..8]);
        return $"{hex[..4]}-{hex[4..8]}-{hex[8..12]}-{hex[12..]}";
    }

    /// <summary>The key of one set, for <paramref name="purpose"/> (<see cref="SoftwarePurpose"/> or <see cref="HardwarePurpose"/>).</summary>
    public static byte[] SetKey(ReadOnlySpan<byte> kek, Guid setId, string purpose)
    {
        Span<byte> salt = stackalloc byte[16];
        setId.TryWriteBytes(salt, bigEndian: true, out _);
        var key = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, kek, key, salt, Encoding.ASCII.GetBytes(purpose));
        return key;
    }

    /// <summary>
    /// The key-associated data (U-KAD) the drive keeps with every encrypted block: "BDFU", the fingerprint (8 bytes) and
    /// the set (16 bytes). It names the key a tape needs without revealing it.
    /// </summary>
    public static byte[] KeyAssociatedData(string fingerprint, Guid setId)
    {
        var result = new byte[4 + 8 + 16];
        "BDFU"u8.CopyTo(result);
        Convert.FromHexString(fingerprint.Replace("-", string.Empty, StringComparison.Ordinal)).CopyTo(result, 4);
        setId.TryWriteBytes(result.AsSpan(12), bigEndian: true, out _);
        return result;
    }

    /// <summary>The label's description of a set's encryption (no key material: the fingerprint and how to derive the KEK).</summary>
    public static TapeLabelEncryption Describe(string mode, string fingerprint, TapeKdfParameters parameters) =>
        new(mode, fingerprint, TapeKdfParameters.Name, parameters.MemoryKiB, parameters.Iterations, parameters.Parallelism, Convert.ToBase64String(parameters.Salt));

    public static TapeKdfParameters Parameters(TapeLabelEncryption encryption) =>
        encryption.Kdf == TapeKdfParameters.Name
            ? new TapeKdfParameters(encryption.MemoryKiB, encryption.Iterations, encryption.Parallelism, Convert.FromBase64String(encryption.Salt))
            : throw new NotSupportedException($"The tape's key is derived with '{encryption.Kdf}'; this version knows {TapeKdfParameters.Name}.");
}

/// <summary>
/// Software encryption of record bodies: AES-256-GCM with a random 96-bit nonce per record. The record header, the set
/// and the file are authenticated, so a body cannot be moved to another place unnoticed. Sealed: nonce, ciphertext, tag.
/// </summary>
public sealed class AesGcmRecordCipher(byte[] setKey, Guid setId) : ITapeRecordCipher, IDisposable
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly AesGcm _aes = new(setKey, TagSize);

    public int Overhead => NonceSize + TagSize;

    public byte[] Seal(ReadOnlySpan<byte> recordHeader, ulong fileId, ReadOnlySpan<byte> body)
    {
        var result = new byte[NonceSize + body.Length + TagSize];
        var nonce = result.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        _aes.Encrypt(nonce, body, result.AsSpan(NonceSize, body.Length), result.AsSpan(NonceSize + body.Length), AssociatedData(recordHeader, fileId));
        return result;
    }

    public byte[] Open(ReadOnlySpan<byte> recordHeader, ulong fileId, ReadOnlySpan<byte> sealedBody)
    {
        if (sealedBody.Length < Overhead)
        {
            throw new CryptographicException("An encrypted record is shorter than its nonce and tag.");
        }

        var length = sealedBody.Length - Overhead;
        var plain = new byte[length];
        _aes.Decrypt(sealedBody[..NonceSize], sealedBody.Slice(NonceSize, length), sealedBody[(NonceSize + length)..], plain, AssociatedData(recordHeader, fileId));
        return plain;
    }

    public void Dispose() => _aes.Dispose();

    private byte[] AssociatedData(ReadOnlySpan<byte> recordHeader, ulong fileId)
    {
        var aad = new byte[recordHeader.Length + 16 + 8];
        recordHeader.CopyTo(aad);
        setId.TryWriteBytes(aad.AsSpan(recordHeader.Length), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt64BigEndian(aad.AsSpan(recordHeader.Length + 16), fileId);
        return aad;
    }
}

/// <summary>The key file offered once when a key is created: the KEK itself, for restores without the passphrase.</summary>
public sealed record TapeKeyFile
{
    public const string FormatName = "BDFU-TAPE-KEY";

    public string Format { get; init; } = FormatName;

    public int Version { get; init; } = 1;

    public required string Fingerprint { get; init; }

    /// <summary>Base64 of the 32-byte KEK.</summary>
    public required string Kek { get; init; }

    public required string Kdf { get; init; }

    public int MemoryKiB { get; init; }

    public int Iterations { get; init; }

    public int Parallelism { get; init; }

    public required string Salt { get; init; }

    public static TapeKeyFile Create(byte[] kek, TapeKdfParameters parameters) => new()
    {
        Fingerprint = TapeKeys.Fingerprint(kek),
        Kek = Convert.ToBase64String(kek),
        Kdf = TapeKdfParameters.Name,
        MemoryKiB = parameters.MemoryKiB,
        Iterations = parameters.Iterations,
        Parallelism = parameters.Parallelism,
        Salt = Convert.ToBase64String(parameters.Salt),
    };

    /// <summary>The KEK, checked against the fingerprint the file names.</summary>
    public byte[] ReadKek()
    {
        if (Format != FormatName)
        {
            throw new InvalidDataException("This is not a backdafuckup tape key file.");
        }

        var kek = Convert.FromBase64String(Kek);
        return kek.Length == 32 && TapeKeys.Fingerprint(kek) == Fingerprint
            ? kek
            : throw new InvalidDataException("The key file is damaged: its key does not match its fingerprint.");
    }
}
