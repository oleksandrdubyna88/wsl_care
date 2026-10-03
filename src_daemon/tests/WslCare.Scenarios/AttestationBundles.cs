using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

using WslCare.FakeTool;

namespace WslCare.Scenarios;

/// <summary>
/// The attestations an installer test publishes (E4): a bundle in Sigstore's own shape — a certificate whose subject
/// alternative name names the workflow that signed, with the Fulcio extensions gh's policy reads, and a DSSE envelope
/// whose in-toto statement names the archive's digest — compressed the way GitHub serves it from a bundle URL (snappy,
/// <c>application/x-snappy</c>, observed 2026-10-03). The fake <c>gh</c> enforces the installer's flags over exactly
/// these claims (<see cref="FakeAttestation"/>); nothing here is signed for real, because what a test proves is the
/// installer's policy, not Sigstore's cryptography.
/// </summary>
internal static class AttestationBundles
{
    public const string HostedRunner = "github-hosted";

    /// <summary>Who an attestation says built the archive: the signing workflow with its ref, the repository, the ref,
    /// and the runner kind.</summary>
    public sealed record Signer(string San, string SourceRepository, string SourceRef, string RunnerEnvironment = HostedRunner)
    {
        /// <summary>What release.yml of <paramref name="repo"/> signs when it runs for <paramref name="gitRef"/>.</summary>
        public static Signer ReleaseWorkflow(string repo, string gitRef, string runner = HostedRunner) =>
            new($"https://github.com/{repo}/.github/workflows/release.yml@{gitRef}", $"https://github.com/{repo}", gitRef, runner);
    }

    /// <summary>A bundle JSON for <paramref name="signer"/> attesting the SHA-256 <paramref name="digest"/>.</summary>
    public static string Bundle(Signer signer, string digest)
    {
        var statement = JsonSerializer.SerializeToUtf8Bytes(
            new Statement([new Subject("archive", new Digest(digest))]),
            AttestationJsonContext.Default.Statement);
        var bundle = new SigstoreBundle(
            "application/vnd.dev.sigstore.bundle.v0.3+json",
            new VerificationMaterial(new Certificate(Convert.ToBase64String(Certificate(signer)))),
            new DsseEnvelope(Convert.ToBase64String(statement), "application/vnd.in-toto+json", [new Signature("AA==")]));
        return JsonSerializer.Serialize(bundle, AttestationJsonContext.Default.SigstoreBundle);
    }

    /// <summary>The attestation API's answer for one digest: each bundle by URL only, <c>bundle</c> null — the shape GitHub
    /// answered for every attestation probed on 2026-10-03 — with the URL's ampersands escaped as <c>&</c>, as a JSON
    /// encoder may write them.</summary>
    public static string ApiAnswer(IReadOnlyList<string> bundleUrls) =>
        "{\n  \"attestations\": [\n"
        + string.Join(",\n", bundleUrls.Select(url => $"    {{\n      \"repository_id\": 1,\n      \"bundle_url\": \"{url.Replace("&", "\\u0026", StringComparison.Ordinal)}\",\n      \"initiator\": \"github\",\n      \"bundle\": null\n    }}"))
        + "\n  ]\n}\n";

    /// <summary>Snappy's raw block format with literals only — a valid stream any decoder must read (the copy elements
    /// are exercised by the captured bundle, <c>fixtures/attestation</c>).</summary>
    public static byte[] Snappy(byte[] data)
    {
        var output = new List<byte>(Varint(data.Length));
        foreach (var chunk in data.Chunk(65536))
        {
            output.AddRange(LiteralTag(chunk.Length));
            output.AddRange(chunk);
        }

        return [.. output];
    }

    private static byte[] Varint(int value)
    {
        var bytes = new List<byte>();
        for (var rest = (uint)value; ; rest >>= 7)
        {
            if (rest < 0x80)
            {
                bytes.Add((byte)rest);
                return [.. bytes];
            }

            bytes.Add((byte)((rest & 0x7f) | 0x80));
        }
    }

    /// <summary>A literal's tag: the length − 1 in the tag up to 59, else in one or two little-endian bytes after it.</summary>
    private static byte[] LiteralTag(int length) => (length - 1) switch
    {
        < 60 and var n => [(byte)(n << 2)],
        < 256 and var n => [60 << 2, (byte)n],
        var n => [61 << 2, (byte)(n & 0xff), (byte)(n >> 8)],
    };

    private static byte[] Certificate(Signer signer)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=sigstore-intermediate-stand-in", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddUri(new Uri(signer.San));
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(Utf8Extension(FakeAttestation.SourceRepositoryOid, signer.SourceRepository));
        request.CertificateExtensions.Add(Utf8Extension(FakeAttestation.SourceRefOid, signer.SourceRef));
        request.CertificateExtensions.Add(Utf8Extension(FakeAttestation.RunnerEnvironmentOid, signer.RunnerEnvironment));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(10));
        return certificate.RawData;
    }

    private static X509Extension Utf8Extension(string oid, string value)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.WriteCharacterString(UniversalTagNumber.UTF8String, value);
        return new X509Extension(oid, writer.Encode(), critical: false);
    }

    /// <summary>The bytes of <paramref name="text"/> as UTF-8.</summary>
    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
}

internal sealed record SigstoreBundle(string MediaType, VerificationMaterial VerificationMaterial, DsseEnvelope DsseEnvelope);

internal sealed record VerificationMaterial(Certificate Certificate);

internal sealed record Certificate(string RawBytes);

internal sealed record DsseEnvelope(string Payload, string PayloadType, IReadOnlyList<Signature> Signatures);

internal sealed record Signature(string Sig);

internal sealed record Statement(IReadOnlyList<Subject> Subject);

internal sealed record Subject(string Name, Digest Digest);

internal sealed record Digest(string Sha256);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(SigstoreBundle))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Statement))]
internal sealed partial class AttestationJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
