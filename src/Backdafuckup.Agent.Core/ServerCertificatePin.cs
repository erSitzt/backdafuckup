using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Backdafuckup.Agent.Core;

/// <summary>
/// Pins the backup server's certificate by the SHA-256 of its SubjectPublicKeyInfo, as the server pins the XCP-ng hosts.
/// Without pins, Windows certificate validation applies (internal CA or public certificate).
/// </summary>
public static class ServerCertificatePin
{
    public static HttpClientHandler CreateHandler(IReadOnlyCollection<string> pins)
    {
        var normalized = new HashSet<string>(pins.Select(p => p.Trim().ToLowerInvariant()).Where(p => p.Length > 0), StringComparer.Ordinal);
        var handler = new HttpClientHandler { UseProxy = true };
        if (normalized.Count > 0)
        {
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null && normalized.Contains(SpkiSha256(certificate.RawData));
        }
        else
        {
            handler.ServerCertificateCustomValidationCallback = (_, _, _, errors) => errors == SslPolicyErrors.None;
        }

        return handler;
    }

    /// <summary>SHA-256 (hex) of the SubjectPublicKeyInfo of a DER certificate.</summary>
    public static string SpkiSha256(byte[] certificateDer)
    {
        var spki = SubjectPublicKeyInfo(certificateDer);
        using (var sha = SHA256.Create())
        {
            return AgentFiles.Hex(sha.ComputeHash(certificateDer, spki.Offset, spki.Count));
        }
    }

    /// <summary>
    /// The SubjectPublicKeyInfo element of an X.509 certificate (RFC 5280): Certificate ::= SEQUENCE { tbsCertificate
    /// SEQUENCE { [0] version OPTIONAL, serialNumber, signature, issuer, validity, subject, subjectPublicKeyInfo, … } … }.
    /// </summary>
    internal static ArraySegment<byte> SubjectPublicKeyInfo(byte[] der)
    {
        var certificate = Read(der, 0, 0x30);
        var tbs = Read(der, certificate.ContentStart, 0x30);
        var position = tbs.ContentStart;
        if (der[position] == 0xA0)
        {
            position = Read(der, position, 0xA0).End; // version
        }

        for (var skip = 0; skip < 5; skip++)
        {
            position = Read(der, position, null).End; // serialNumber, signature, issuer, validity, subject
        }

        var spki = Read(der, position, 0x30);
        return new ArraySegment<byte>(der, spki.Start, spki.End - spki.Start);
    }

    private static Element Read(byte[] der, int start, int? expectedTag)
    {
        if (start + 2 > der.Length || (expectedTag is { } tag && der[start] != tag))
        {
            throw new CryptographicException("The server certificate is not a valid DER certificate.");
        }

        var lengthByte = der[start + 1];
        int length;
        var contentStart = start + 2;
        if (lengthByte < 0x80)
        {
            length = lengthByte;
        }
        else
        {
            var count = lengthByte & 0x7F;
            if (count is 0 or > 4 || contentStart + count > der.Length)
            {
                throw new CryptographicException("The server certificate has an invalid DER length.");
            }

            length = 0;
            for (var i = 0; i < count; i++)
            {
                length = (length << 8) | der[contentStart + i];
            }

            contentStart += count;
        }

        if (length < 0 || contentStart + length > der.Length)
        {
            throw new CryptographicException("The server certificate is truncated.");
        }

        return new Element(start, contentStart, contentStart + length);
    }

    private readonly struct Element
    {
        public Element(int start, int contentStart, int end)
        {
            Start = start;
            ContentStart = contentStart;
            End = end;
        }

        public int Start { get; }

        public int ContentStart { get; }

        public int End { get; }
    }
}
