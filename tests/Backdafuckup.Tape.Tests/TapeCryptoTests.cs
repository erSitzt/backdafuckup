using System.Security.Cryptography;
using System.Text;
using Backdafuckup.Tape.Crypto;
using Backdafuckup.Tape.Format;

namespace Backdafuckup.Tape.Tests;

/// <summary>Tape keys and software encryption (small Argon2 parameters: the defaults take a second and 256 MiB).</summary>
public sealed class TapeCryptoTests
{
    private static readonly TapeKdfParameters Small = new(8 * 1024, 1, 1, Convert.FromHexString("00112233445566778899AABBCCDDEEFF"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_passphrase_gives_the_same_key_and_fingerprint_and_sets_get_keys_of_their_own()
    {
        var kek = TapeKeys.DeriveKek("correct horse battery staple", Small);
        Assert.Equal(kek, TapeKeys.DeriveKek("correct horse battery staple", Small));
        Assert.NotEqual(kek, TapeKeys.DeriveKek("correct horse battery stapler", Small));
        Assert.Matches("^[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}$", TapeKeys.Fingerprint(kek));
        Assert.NotEqual(TapeKeys.Fingerprint(kek), TapeKeys.Fingerprint(TapeKeys.DeriveKek("other", Small)));

        // HKDF-SHA256 with the set's id as salt: RFC 5869's construction, here checked against the BCL directly.
        var set = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        Assert.Equal(
            HKDF.DeriveKey(HashAlgorithmName.SHA256, kek, 32, Convert.FromHexString("0123456789ABCDEF0123456789ABCDEF"), "bdfu-tape-sw-v1"u8.ToArray()),
            TapeKeys.SetKey(kek, set, TapeKeys.SoftwarePurpose));
        Assert.NotEqual(TapeKeys.SetKey(kek, set, TapeKeys.SoftwarePurpose), TapeKeys.SetKey(kek, set, TapeKeys.HardwarePurpose));
        Assert.NotEqual(TapeKeys.SetKey(kek, set, TapeKeys.SoftwarePurpose), TapeKeys.SetKey(kek, Guid.NewGuid(), TapeKeys.SoftwarePurpose));

        var kad = TapeKeys.KeyAssociatedData(TapeKeys.Fingerprint(kek), set);
        Assert.Equal(28, kad.Length);
        Assert.Equal("BDFU"u8.ToArray(), kad[..4]);

        // The key file brings the KEK back and refuses a damaged one.
        var file = TapeKeyFile.Create(kek, Small);
        Assert.Equal(kek, file.ReadKek());
        Assert.Throws<InvalidDataException>(() => (file with { Fingerprint = "0000-0000-0000-0000" }).ReadKek());
        var label = TapeKeys.Describe("software", file.Fingerprint, Small);
        Assert.Equal(Small.Salt, TapeKeys.Parameters(label).Salt);
    }

    [Fact]
    public async Task Encrypted_records_hide_their_content_and_refuse_changes_and_wrong_keys()
    {
        var kek = TapeKeys.DeriveKek("pass", Small);
        var set = Guid.NewGuid();
        using var cipher = new AesGcmRecordCipher(TapeKeys.SetKey(kek, set, TapeKeys.SoftwarePurpose), set);
        var secret = Encoding.UTF8.GetBytes("{\"vm\":\"payroll-db-01\",\"chunk\":\"0011223344\"}");
        var tape = new MemoryTape();
        var writer = await TapeFileWriter.BeginAsync(tape, 64 * 1024, new TapeFileHeader { Type = TapeFileTypes.RestorePoint, FileId = 5 }, cipher, Ct);
        await writer.WriteRecordAsync(TapeFormat.Entry, secret, encrypt: true, Ct);
        await writer.FinishAsync(false, Ct);

        // Nothing of the content on tape; the file header stays readable for a scan.
        var raw = tape.Files[0].SelectMany(b => b).ToArray();
        Assert.Equal(-1, raw.AsSpan().IndexOf("payroll"u8));
        Assert.NotEqual(-1, raw.AsSpan().IndexOf("restore-point"u8));

        tape.Rewind();
        var records = await new TapeFileReader(tape, 64 * 1024, cipher).ReadAllAsync(Ct).ToListAsync(Ct);
        Assert.Equal(secret, records[1].Body);
        Assert.Equal(RecordFlags.Encrypted, records[1].Flags);

        tape.Rewind();
        var noKey = await Assert.ThrowsAsync<CryptographicException>(async () => await new TapeFileReader(tape, 64 * 1024).ReadAllAsync(Ct).ToListAsync(Ct));
        Assert.Contains("key is needed", noKey.Message, StringComparison.Ordinal);

        tape.Rewind();
        using var wrong = new AesGcmRecordCipher(TapeKeys.SetKey(TapeKeys.DeriveKek("wrong", Small), set, TapeKeys.SoftwarePurpose), set);
        await Assert.ThrowsAnyAsync<CryptographicException>(async () => await new TapeFileReader(tape, 64 * 1024, wrong).ReadAllAsync(Ct).ToListAsync(Ct));

        // Moved into another file of the same set: the file id is authenticated.
        var header = new byte[16];
        Encoding.ASCII.GetBytes("ENTR", header);
        var sealedBody = cipher.Seal(header, 5, secret);
        Assert.ThrowsAny<CryptographicException>(() => cipher.Open(header, 6, sealedBody));
        sealedBody[20] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => cipher.Open(header, 5, sealedBody));
    }
}
