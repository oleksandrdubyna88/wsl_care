using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace WslCare.FakeTool;

/// <summary>
/// The fake <c>gh attestation verify</c> (E4, the install trust boundary): it ENFORCES the identity flags it is given,
/// with gh's own semantics, over a bundle in Sigstore's own shape — so an installer test is about what the installer's
/// check lets through, not about the argv it happened to send (testing rule, "asserting that you SENT a protective
/// option is not testing that it protects").
/// </summary>
/// <remarks>
/// <para>The bundle is the file after <c>--bundle</c>, or — when the call names none, gh's online mode — the answer's
/// fixture, which stands for what GitHub's attestation API would hand gh. From it the fake reads what gh's policy checks
/// read: the signing certificate's subject alternative name (the workflow that signed, with its ref) and the Fulcio
/// extensions for the source repository (<c>1.3.6.1.4.1.57264.1.12</c>), its ref (<c>…1.14</c>) and the runner
/// environment (<c>…1.11</c>), and the in-toto statement's subject digests. It does NO cryptography: a bundle a test
/// builds carries a self-signed certificate, and what is proved is the installer's choice of policy, not Sigstore.</para>
/// <para>The semantics, measured against gh 2.97.0 on 2026-10-03 (cli/cli's own attestations, research/module_tests.md):
/// <c>--cert-identity</c> is an EXACT match of the SAN; <c>--signer-workflow</c> is a literal PREFIX of the SAN after
/// <c>https://github.com/</c> — <c>…/deploy</c> accepted a certificate of <c>…/deployment.yml@refs/heads/trunk</c>, which is
/// why an unpinned signer workflow admits a build of ANY ref; with neither, the SAN must be under
/// <c>https://github.com/&lt;repo&gt;/</c>; <c>--repo</c> also requires the source repository extension;
/// <c>--source-ref</c> the ref extension; <c>--deny-self-hosted-runners</c> a runner environment of
/// <c>github-hosted</c>; and the artifact's SHA-256 must be one of the statement's subjects.</para>
/// </remarks>
public static class FakeAttestation
{
    public const string RunnerEnvironmentOid = "1.3.6.1.4.1.57264.1.11";
    public const string SourceRepositoryOid = "1.3.6.1.4.1.57264.1.12";
    public const string SourceRefOid = "1.3.6.1.4.1.57264.1.14";
    private const string SubjectAlternativeNameOid = "2.5.29.17";
    private const string GitHub = "https://github.com/";

    private static readonly HashSet<string> ValueFlags =
        ["--bundle", "-b", "--repo", "-R", "--owner", "-o", "--cert-identity", "--signer-workflow", "--source-ref", "--cert-oidc-issuer", "--hostname"];

    /// <summary>What one bundle says about who built what.</summary>
    public sealed record Claims(string San, string SourceRepository, string SourceRef, string RunnerEnvironment, IReadOnlyList<string> Digests);

    /// <summary>A parsed <c>gh attestation verify</c> call: the artifact, the flags with values, the switches.</summary>
    public sealed record Request(string Artifact, IReadOnlyDictionary<string, string> Values, IReadOnlySet<string> Switches)
    {
        public string Value(string flag) => Values.TryGetValue(flag, out var value) ? value : string.Empty;
    }

    /// <summary>Answers the call: exit 0 when every flag given holds for the bundle, otherwise 1 with gh-like reasons.</summary>
    public static int Verify(IReadOnlyList<string> argv, string onlineBundle)
    {
        var request = Parse(argv);
        var bundle = request.Value("--bundle") is { Length: > 0 } local ? local : onlineBundle;
        var refusal = Refusal(request, bundle);
        if (refusal.Length > 0)
        {
            Console.Error.WriteLine($"fake gh: Error: {refusal}");
            return 1;
        }

        return 0;
    }

    /// <summary>The first reason the bundle does not satisfy the request; empty when it does.</summary>
    public static string Refusal(Request request, string bundlePath)
    {
        Claims claims;
        try
        {
            claims = Read(bundlePath);
        }
        catch (Exception e)
        {
            // Whatever a malformed bundle throws, the answer is gh's: refused, with the reason.
            return $"the bundle {bundlePath} is not a Sigstore bundle: {e.Message}";
        }

        var reason = Checks(request, claims).FirstOrDefault(r => r.Length > 0) ?? string.Empty;
        return reason.Length == 0 ? reason : $"the attestation signed by {claims.San}: {reason}";
    }

    public static Request Parse(IReadOnlyList<string> argv)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var switches = new HashSet<string>(StringComparer.Ordinal);
        var positional = new List<string>();
        var i = 2;
        while (i < argv.Count)
        {
            i += Take(argv, i, values, switches, positional);
        }

        return new Request(positional.LastOrDefault() ?? string.Empty, values, switches);
    }

    /// <summary>Reads the argument at <paramref name="i"/> (and its value); returns how many arguments it consumed.</summary>
    private static int Take(IReadOnlyList<string> argv, int i, Dictionary<string, string> values, HashSet<string> switches, List<string> positional)
    {
        var arg = argv[i];
        if (ValueFlags.Contains(arg) && i + 1 < argv.Count)
        {
            values[arg] = argv[i + 1];
            return 2;
        }

        (arg.StartsWith('-') ? (ICollection<string>)switches : positional).Add(arg);
        return 1;
    }

    private static IEnumerable<string> Checks(Request request, Claims claims)
    {
        yield return RepositoryCheck(request, claims);
        yield return IdentityCheck(request, claims);
        yield return Expect(request.Value("--source-ref"), claims.SourceRef, "SourceRepositoryRef");
        yield return request.Switches.Contains("--deny-self-hosted-runners") && claims.RunnerEnvironment != "github-hosted"
            ? $"the attestation was made on a {Quoted(claims.RunnerEnvironment)} runner, and --deny-self-hosted-runners was given"
            : string.Empty;
        yield return DigestCheck(request.Artifact, claims);
    }

    private static string RepositoryCheck(Request request, Claims claims) =>
        request.Value("--repo") is { Length: > 0 } repo ? Expect(GitHub + repo, claims.SourceRepository, "SourceRepositoryURI") : string.Empty;

    private static string IdentityCheck(Request request, Claims claims)
    {
        var exact = request.Value("--cert-identity");
        var workflow = request.Value("--signer-workflow");
        var prefix = workflow.Length > 0 ? GitHub + workflow : GitHub + request.Value("--repo") + "/";
        return exact.Length > 0
            ? Expect(exact, claims.San, "SAN value")
            : claims.San.StartsWith(prefix, StringComparison.Ordinal) ? string.Empty : $"expected SAN to start with \"{prefix}\", got \"{claims.San}\"";
    }

    private static string DigestCheck(string artifact, Claims claims)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(artifact)));
        return claims.Digests.Contains(digest) ? string.Empty : $"the attestation's subjects ({string.Join(", ", claims.Digests)}) do not include the artifact's sha256:{digest}";
    }

    private static string Expect(string wanted, string actual, string what) =>
        wanted.Length == 0 || wanted == actual ? string.Empty : $"expected {what} to be \"{wanted}\", got \"{actual}\"";

    private static string Quoted(string value) => value.Length == 0 ? "(unnamed)" : $"\"{value}\"";

    /// <summary>The claims of a bundle file in Sigstore's shape (<c>verificationMaterial.certificate.rawBytes</c>,
    /// <c>dsseEnvelope.payload</c>).</summary>
    public static Claims Read(string bundlePath)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(bundlePath));
        var root = document.RootElement;
        var raw = root.GetProperty("verificationMaterial").GetProperty("certificate").GetProperty("rawBytes").GetString() ?? string.Empty;
        using var certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(raw));
        var payload = root.GetProperty("dsseEnvelope").GetProperty("payload").GetString() ?? string.Empty;
        return new Claims(
            San(certificate),
            Utf8Extension(certificate, SourceRepositoryOid),
            Utf8Extension(certificate, SourceRefOid),
            Utf8Extension(certificate, RunnerEnvironmentOid),
            Digests(Convert.FromBase64String(payload)));
    }

    private static string San(X509Certificate2 certificate)
    {
        var extension = certificate.Extensions[SubjectAlternativeNameOid] ?? throw new FormatException("the certificate has no subject alternative name");
        var names = new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadSequence();
        var uri = new Asn1Tag(TagClass.ContextSpecific, 6);
        while (names.HasData)
        {
            if (names.PeekTag() == uri)
            {
                return names.ReadCharacterString(UniversalTagNumber.IA5String, uri);
            }

            names.ReadEncodedValue();
        }

        throw new FormatException("the subject alternative name holds no URI");
    }

    private static string Utf8Extension(X509Certificate2 certificate, string oid) =>
        certificate.Extensions[oid] is { } extension
            ? new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadCharacterString(UniversalTagNumber.UTF8String)
            : string.Empty;

    private static IReadOnlyList<string> Digests(byte[] statement)
    {
        using var document = JsonDocument.Parse(statement);
        return [.. document.RootElement.GetProperty("subject").EnumerateArray()
            .Select(s => s.GetProperty("digest").TryGetProperty("sha256", out var sha) ? sha.GetString() ?? string.Empty : string.Empty)];
    }
}
