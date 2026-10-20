using System.Globalization;
using System.Text;
using System.Text.Json;

using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Worktrees;

namespace WslCare.Cli.Commands;

/// <summary>
/// <c>worktrees roots set | roots clear | scan</c> (E19.S3 of PLAN_disk_hygiene_windows_E19.md, PLAN_disk_hygiene_windows.md §3.3): the
/// Windows binary's worktree report. <c>roots set</c> reads the chosen folders from STDIN, judges every one by the root rules
/// (<see cref="WorktreeRoots"/>) and writes <c>worktrees.roots</c> through the daemon's own configuration writer ONLY when every one is
/// accepted — all or nothing, and every path still gets its own verdict. <c>roots clear</c> empties the key. <c>scan</c> is read-only:
/// it walks the roots, starts no process, deletes nothing, and writes only <c>worktrees.json</c>.
/// </summary>
/// <remarks>Exit codes: 0 done; 1 the scan could not record its report (the answer is still printed); 2 usage — the distro's binary, an
/// unusable stdin, or any refused path (nothing written); 70 the deletion policy refused the user layer's own file.</remarks>
internal static class WorktreesCommand
{
    private const string NotWindows = $"\"{CommandLine.BinaryName} worktrees\" is the Windows binary's: the worktree report reads the Windows host's folders, and the distro's binary reads none; nothing was done";

    private const string NothingWritten = "Nothing was written.";

    public static int RootsSet(Request.WorktreesRootsSet request, CliHost host, ConfigLoadResult loaded, TextWriter stdout, TextWriter stderr)
    {
        if (host.Paths is not WindowsHostPaths)
        {
            Output.Note(stderr, NotWindows);
            return (int)ExitCode.Usage;
        }

        return StdinList.Read(host.StandardInput(), StdinList.MaxBytes, host.StdinCeiling) switch
        {
            FileReadResult.Content content => Judged(request, host, loaded, Encoding.UTF8.GetString(content.Bytes), stdout, stderr),
            FileReadResult.Unreadable unreadable => Unusable(stderr, $"stdin {unreadable.Reason}"),
            _ => Unusable(stderr, "nothing arrived on stdin"),
        };
    }

    private static int Unusable(TextWriter stderr, string why)
    {
        Output.Note(stderr, $"worktrees roots set: {why}. {NothingWritten}");
        return (int)ExitCode.Usage;
    }

    private static int Judged(Request.WorktreesRootsSet request, CliHost host, ConfigLoadResult loaded, string text, TextWriter stdout, TextWriter stderr)
    {
        var given = StdinList.Lines(text, _ => true).Lines;
        if (given.Count == 0)
        {
            return Unusable(stderr, $"stdin held no path (one absolute path per line); to empty the list run \"{CommandLine.BinaryName} worktrees roots clear\"");
        }

        var verdicts = WorktreeRoots.Judge(host.Paths, host.Files, WorktreeRoots.ThisMachinesDrive, loaded.Config.Int(ConfigKeys.Worktrees.MaxRoots), given);
        if (verdicts.Any(v => !v.Accepted))
        {
            return Answered(request.Json, written: false, verdicts, stdout, stderr, refusal: $"{NothingWritten} {verdicts.Count(v => !v.Accepted)} of {verdicts.Count} paths were refused; each has its verdict above");
        }

        return Written(new UserConfigWriter(host.Paths, host.Files, host.Clock).Set(ConfigKeys.Worktrees.Roots, new ConfigValue.TextList([.. verdicts.Select(v => v.Path)])), request.Json, verdicts, stdout, stderr);
    }

    /// <summary>The write's outcome: the verdicts printed either way, and the exit that says whether the key changed.</summary>
    private static int Written(UserConfigWriteResult result, bool json, IReadOnlyList<RootVerdict> verdicts, TextWriter stdout, TextWriter stderr) => result switch
    {
        UserConfigWriteResult.Written => Answered(json, written: true, verdicts, stdout, stderr, string.Empty),
        UserConfigWriteResult.TooLarge large => Answered(json, written: false, verdicts, stdout, stderr, $"the user layer would be {large.Bytes} bytes, over the {large.Max}-byte cap its reader keeps. {NothingWritten}"),
        UserConfigWriteResult.BreaksRule broken => Answered(json, written: false, verdicts, stdout, stderr, $"{broken.Message}. {NothingWritten}"),
        UserConfigWriteResult.Refused refused => Output.Internal(stderr, $"the deletion policy refused the user config file: {refused.Verdict.Reason}"),
        _ => throw new System.Diagnostics.UnreachableException("UserConfigWriteResult is a closed set"),
    };

    private static int Answered(bool json, bool written, IReadOnlyList<RootVerdict> verdicts, TextWriter stdout, TextWriter stderr, string refusal)
    {
        var report = new RootVerdictsReport(Core.SchemaVersion.Current, written, verdicts);
        Output.Answer(stdout, json ? JsonSerializer.Serialize(report, WslCareJsonContext.Default.RootVerdictsReport) : VerdictLines(verdicts));
        if (refusal.Length > 0)
        {
            Output.Note(stderr, $"worktrees roots set: {refusal}");
        }

        return written ? (int)ExitCode.Ok : (int)ExitCode.Usage;
    }

    private static string VerdictLines(IReadOnlyList<RootVerdict> verdicts) =>
        string.Join(Environment.NewLine, verdicts.Select(v => CommandLine.Printable(v.Accepted ? $"accepted {v.Path}" : $"refused {v.Given}: {v.Reason} ({v.Rule})")));

    public static int RootsClear(Request.WorktreesRootsClear request, CliHost host, TextWriter stdout, TextWriter stderr)
    {
        if (host.Paths is not WindowsHostPaths)
        {
            Output.Note(stderr, NotWindows);
            return (int)ExitCode.Usage;
        }

        return new UserConfigWriter(host.Paths, host.Files, host.Clock).Reset(ConfigKeys.Worktrees.Roots) switch
        {
            UserConfigWriteResult.Written => Cleared(request.Json, stdout),
            UserConfigWriteResult.Refused refused => Output.Internal(stderr, $"the deletion policy refused the user config file: {refused.Verdict.Reason}"),
            var other => Output.Refuse(stderr, $"{ConfigKeys.Worktrees.Roots.Name} could not be cleared ({other.GetType().Name}). {NothingWritten}"),
        };
    }

    private static int Cleared(bool json, TextWriter stdout) =>
        Output.Answer(stdout, json ? JsonSerializer.Serialize(new RootVerdictsReport(Core.SchemaVersion.Current, true, []), WslCareJsonContext.Default.RootVerdictsReport) : $"{ConfigKeys.Worktrees.Roots.Name} cleared");

    public static int Scan(Request.WorktreesScan request, CliHost host, ConfigLoadResult loaded, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        if (host.Paths is not WindowsHostPaths)
        {
            Output.Note(stderr, NotWindows);
            return (int)ExitCode.Usage;
        }

        var settings = WorktreeSettings.From(loaded.Config);
        var roots = loaded.Config.TextList(ConfigKeys.Worktrees.Roots);
        if (roots.Count == 0)
        {
            Output.Note(stderr, WorktreesStatus.NoRoot);
        }

        var store = new WorktreeReportStore(host.Paths, host.Files);
        _ = store.SweepStaleTemp(host.Clock.GetUtcNow(), TimeSpan.FromSeconds(settings.MaxSeconds));
        var report = WorktreeReports.From(new WorktreeScanner(host.Paths, host.Files, host.Clock, WorktreeRoots.ThisMachinesDrive).Run(roots, settings, cancellationToken, line => Output.Note(stderr, line)), settings);
        var failed = store.TryWrite(report);
        if (failed.Length > 0)
        {
            Output.Note(stderr, $"worktrees scan: the report was not recorded: {failed}");
        }

        Output.Answer(stdout, request.Json ? JsonSerializer.Serialize(report, WslCareJsonContext.Default.WorktreeReport) : ScanText(report));
        return failed.Length > 0 ? (int)ExitCode.RunFailed : (int)ExitCode.Ok;
    }

    /// <summary>What a person reads: a line per root, then what is NOT yet known.</summary>
    private static string ScanText(WorktreeReport report)
    {
        var text = new StringBuilder();
        foreach (var root in report.Roots)
        {
            text.AppendLine(CommandLine.Printable(root.Refused is { } refused ? $"{root.Path}: refused - {refused}" : RootLine(root)));
        }

        text.Append(CommandLine.Printable($"scan {(report.Complete ? "complete" : "INCOMPLETE (a budget ran out; the folders it left are unknown, never idle)")} in {report.ElapsedSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s; reclaimable: unknown - {report.Reclaimable.Reason}"));
        return text.ToString();
    }

    private static string RootLine(RootReport root) => string.Create(
        CultureInfo.InvariantCulture,
        $"{root.Path}: {root.Folders} folders - {root.Worktrees} worktrees ({root.Locked} locked), {root.Orphans} orphans, {root.ForeignPointers} made elsewhere, {root.MainClones} main clones, {root.NotGit} not git; {root.IdleOver.Count} idle over {root.IdleOver.Days} days; build output {root.BuildOutput.All.Count} folders {root.BuildOutput.All.Bytes / WorktreesStatus.BytesPerGibibyte:0.0} GiB ({root.BuildOutput.Retained.Sum(r => r.Count)} kept whole); {root.Unknown.Count} unknown");
}
