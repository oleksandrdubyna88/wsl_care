using System.Runtime.Versioning;

using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// A checkout of an extension release tag as release-extension-guard.sh reads it — package.json, the handshake's minimum,
/// min-daemon.json, POST_DEPLOY.md, optionally the root module — and a fake <c>gh</c> on PATH that records what it was
/// asked and answers the release queries. One unit, used by <see cref="ReleaseExtensionScriptFlows"/> and
/// <see cref="ReleaseExtensionInstallPinFlows"/> (coai round 9: a named unit, never a partial class, keeps both files
/// under the 800-line limit).
/// </summary>
[SupportedOSPlatform("linux")]
internal static class ReleaseExtensionCheckout
{
    internal const string Published = "false\tdaemon-v0.1.0";
    internal const string Verified = "Last verified: 2026-10-05 · the owner's installation · daemon 0.1.0";


    internal sealed record Checkout(string Dir, IReadOnlyDictionary<string, string?> Env, string GhLog);

    /// <summary>A checkout of the tag: package.json, the handshake's minimum, POST_DEPLOY.md — and a fake gh.</summary>
    /// <summary>The checked-in artefact the guard reads the minima from (E5 code round #2/#5; the actions minimum since E6.S2).</summary>
    /// <summary>The checked-in artefact: the two minima and, since #37, the release Install daemon types (defaulting to the
    /// actions minimum, so a flow that does not name it asks GitHub about no extra tag).</summary>
    internal static string MinDaemonJson(string min, string actions, string? install = null) => $"{{\n  \"minDaemonForRender\": \"{min}\",\n  \"minDaemonForActions\": \"{actions}\",\n  \"installDaemon\": \"{install ?? actions}\"\n}}\n";

    internal static Checkout Make(TempRoot root, string version = "0.1.0", string publisher = "wsl-care-dev", string min = "0.1.0", string stamp = Verified, string? ghAnswer = Published, string? handshake = null, string? minDaemonJson = "", string? actions = null, bool rootModule = false, string? install = null, string drafts = "")
    {
        var dir = root.Dir("checkout");
        root.File("checkout/src_vs_code/package.json", $"{{\n  \"name\": \"{ReleaseFiles.ExtensionName}\",\n  \"version\": \"{version}\",\n  \"publisher\": \"{publisher}\",\n  \"scripts\": {{\n    \"version\": \"not-the-top-level-one\"\n  }}\n}}\n");
        root.File("checkout/src_vs_code/src/client/handshake.ts", handshake ?? $"export const SUPPORTED_SCHEMA: readonly number[] = [1];\n\nexport const MIN_DAEMON_FOR_RENDER = '{min}';\n");
        if (minDaemonJson is not null)
        {
            root.File("checkout/src_vs_code/min-daemon.json", minDaemonJson.Length == 0 ? MinDaemonJson(min, actions ?? min, install) : minDaemonJson);
        }

        if (rootModule)
        {
            root.File("checkout/src_vs_code/src/root/rootCall.ts", "export const ROOT_OPS = ['preview', 'confirm', 'stop', 'fullCheck', 'rootCheck'] as const;\n");
        }

        root.File("checkout/POST_DEPLOY.md", $"# Post-deploy checks\n\nTarget: x\n{stamp}\n\n| # | a | b | c |\n");
        var log = root.Under("gh.log");
        // The extension's first release is answered apart (E6.S2 review S1: root is allowed only once it is published).
        var gh = root.File("bin/gh", "#!/bin/sh\necho \"$@\" >> \"$FAKE_GH_LOG\"\n[ -n \"$FAKE_GH_FAIL\" ] && { echo 'HTTP 404: Not Found' >&2; exit 1; }\ncase \"$*\" in *releases/tags/extension-v*) printf '%s\\n' \"$FAKE_GH_EXT_ANSWER\"; exit 0 ;; esac\n" +
            // FAKE_GH_ANSWER=by-tag answers a daemon release query for ITS tag: published, unless listed in FAKE_GH_DRAFTS (#37).
            "if [ \"$FAKE_GH_ANSWER\" = by-tag ]; then t=\"${*##*releases/tags/}\"; t=\"${t%% *}\"; case \" $FAKE_GH_DRAFTS \" in *\" $t \"*) d=true ;; *) d=false ;; esac; printf '%s\\t%s\\n' \"$d\" \"$t\"; exit 0; fi\n" +
            "printf '%s\\n' \"$FAKE_GH_ANSWER\"\n");
        File.SetUnixFileMode(gh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var env = new Dictionary<string, string?>
        {
            ["PATH"] = $"{Path.GetDirectoryName(gh)}:{Environment.GetEnvironmentVariable("PATH")}",
            ["GH_REPO"] = "oleksandrdubyna88/wsl_care",
            ["GH_TOKEN"] = "read-only",
            ["FAKE_GH_LOG"] = log,
            ["FAKE_GH_ANSWER"] = ghAnswer ?? string.Empty,
            ["FAKE_GH_EXT_ANSWER"] = "false\textension-v0.1.0",
            ["FAKE_GH_FAIL"] = ghAnswer is null ? "1" : null,
            ["FAKE_GH_DRAFTS"] = drafts,
            ["GITHUB_OUTPUT"] = null,
        };
        return new Checkout(dir, env, log);
    }
}
