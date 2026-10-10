using System.Globalization;
using System.Text.Json;

using WslCare.Core.Actions;
using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;

namespace WslCare.Core.Archive;

/// <summary>One process root started for the archive: the <c>runuser</c> it launched, or the product binary under it.</summary>
/// <param name="Role"><see cref="ArchiveChildren.Launcher"/> or <see cref="ArchiveChildren.Worker"/>.</param>
/// <param name="StartTicks"><c>/proc/[pid]/stat</c> field 22: the identity within one boot.</param>
public sealed record ArchiveChildIdentity(string Role, string Template, int Pid, long StartTicks, DateTimeOffset LaunchedUtc);

/// <summary>Whether an archive child may start: <see cref="Free"/>, or why not — a recorded child still alive (a skip: it waits for
/// it) or a record root cannot keep (a refusal: containment is broken, the S4 own review round C-7).</summary>
public sealed record ChildGate(string Why, bool Refuses)
{
    public static ChildGate Free { get; } = new(string.Empty, false);

    public bool Blocks => Why.Length > 0;
}

/// <summary>Root's record of the archive's children (plan §15r risk consult 9/9.4 #1): ONE file, replaced at every launch and
/// emptied once its children are gone — never a growing list (the S4 plan round's finding 2).</summary>
public sealed record ArchiveChildFile(int SchemaVersion, string BootId, IReadOnlyList<ArchiveChildIdentity> Children)
{
    public static ArchiveChildFile Empty { get; } = new(Core.SchemaVersion.Current, string.Empty, []);
}

/// <summary>
/// The archive's children, as root starts them (plan §15r D1, D8, E9.S4): the product's OWN binary run as the target user
/// through self-invocation templates — closed verbs, typed slots — and the identities of what was started, kept in root's state
/// so a child that outlived its ceiling in uninterruptible sleep (a read the share stopped serving) is seen, and no second one
/// is launched beside it (risk consult 9/9.4 #1).
/// </summary>
/// <remarks>Root signals ONLY identities its own launcher recorded (9/9.4 #3): nothing a child prints and nothing on the base
/// ever names a process root acts on — and this file is only ever read to decide NOT to launch.</remarks>
public static class ArchiveChildren
{
    public const string Launcher = "launcher";

    public const string Worker = "worker";

    public const string FileName = "archive-children.json";

    /// <summary><c>archive preview --json</c>: what would move — an ordinary ceiling, counted in the timer run's worst case.</summary>
    public static readonly CommandTemplate Preview = Self("archive-preview", [Literal("archive"), Literal("preview"), Literal("--json")], new CommandLimits.Keyed(ConfigKeys.Archive.PreviewTimeoutSeconds, ConfigKeys.Archive.ChildOutputCapBytes));

    /// <summary><c>archive reach --json</c>: the side's lock and the base within <c>archive.reachabilitySeconds</c> — the short child
    /// before the long one (D1).</summary>
    public static readonly CommandTemplate Reach = Self("archive-reach", [Literal("archive"), Literal("reach"), Literal("--json")], new CommandLimits.Keyed(ConfigKeys.Archive.ReachabilitySeconds, ConfigKeys.Archive.ChildOutputCapBytes));

    /// <summary><c>archive run --budget-seconds &lt;n&gt; --run-id &lt;runId&gt; --json</c>: STREAMED and BUDGETED (D8).</summary>
    public static readonly CommandTemplate Run = Self(
        "archive-run",
        [Literal("archive"), Literal("run"), Literal("--budget-seconds"), new ArgPart.Slot("budget", new SlotKind.Number(ConfigKeys.Archive.RunBudgetMinutes.Min * 60L, ConfigKeys.Archive.RunBudgetMinutes.Max * 60L)), Literal("--run-id"), new ArgPart.Slot("run", new SlotKind.RunIdText()), Literal("--json")],
        new CommandLimits.Budgeted(ConfigKeys.Archive.RunBudgetMinutes, ConfigKeys.Archive.FinishGraceMinutes, ConfigKeys.Archive.ChildOutputCapBytes)) with
    { Streamed = true };

    /// <summary><c>archive list --restorable --json</c>: what A20 may restore, bounded by <c>archive.maxRestoreEntries</c> (the code
    /// round's finding 4: the answer stays inside the cap however large the archive grows).</summary>
    public static readonly CommandTemplate List = Self("archive-list", [Literal("archive"), Literal("list"), Literal("--restorable"), Literal("--json")], new CommandLimits.Keyed(ConfigKeys.Archive.PreviewTimeoutSeconds, ConfigKeys.Archive.ChildOutputCapBytes));

    /// <summary><c>archive list --restorable --entry &lt;id&gt;[,&lt;id&gt;…] --json</c>: what A20's preview asks when the panel SHOWED entries — those
    /// only, so an entry older than the newest <c>archive.maxRestoreEntries</c> is never lost to the window (the E10.S0 own review,
    /// finding 1). The ids are ONE argument, at most the key's range: 5000 × 17 bytes, far below the kernel's per-argument limit.</summary>
    public static readonly CommandTemplate ListShown = Self(
        "archive-list-shown",
        [Literal("archive"), Literal("list"), Literal("--restorable"), Literal("--entry"), new ArgPart.Slot("entries", new SlotKind.HexList(16, ConfigKeys.Archive.MaxRestoreEntries.Max)), Literal("--json")],
        new CommandLimits.Keyed(ConfigKeys.Archive.PreviewTimeoutSeconds, ConfigKeys.Archive.ChildOutputCapBytes));

    /// <summary><c>archive restore --entry &lt;id&gt;[,&lt;id&gt;…] --json</c>: button-only and STREAMED (the S4 plan round's amendment 2).</summary>
    public static readonly CommandTemplate Restore = Self(
        "archive-restore",
        [Literal("archive"), Literal("restore"), Literal("--entry"), new ArgPart.Slot("entries", new SlotKind.HexList(16, ConfigKeys.Archive.MaxRestoreEntries.Max)), Literal("--json")],
        new CommandLimits.ButtonOnly(ConfigKeys.Archive.RestoreLimitMinutes, ConfigKeys.Archive.ChildOutputCapBytes)) with
    { Streamed = true };

    private static ArgPart Literal(string text) => new ArgPart.Literal(text);

    private static CommandTemplate Self(string name, IReadOnlyList<ArgPart> parts, CommandLimits limits) =>
        new(name, CommandScope.User, SelfBinary.Name, parts, limits) { SelfInvocation = true };

    private static int MaxBytes => Tuning.Current.Int(ConfigKeys.Records.MaxStateFileBytes);

    public static string File(IHostPaths paths) => paths.Rules.Join(paths.StateDirectory, FileName);

    /// <summary>The recorded children; <see cref="ArchiveChildFile.Empty"/> when there is none or it does not read.</summary>
    public static ArchiveChildFile Read(IHostPaths paths, IFileSystem files)
    {
        if (files.ReadStateFile(File(paths), MaxBytes) is not FileReadResult.Content content)
        {
            return ArchiveChildFile.Empty;
        }

        try
        {
            return JsonSerializer.Deserialize(content.Bytes, WslCareJsonContext.Default.ArchiveChildFile) is { Children: not null } file ? file : ArchiveChildFile.Empty;
        }
        catch (JsonException)
        {
            return ArchiveChildFile.Empty;
        }
    }

    /// <summary>Empty when written; otherwise why not.</summary>
    public static string Write(IHostPaths paths, IFileSystem files, ArchiveChildFile record)
    {
        try
        {
            files.CreateDirectory(paths.StateDirectory);
            var json = JsonSerializer.SerializeToUtf8Bytes(record, WslCareJsonContext.Default.ArchiveChildFile);
            return files.WritePrivateFileAtomically(File(paths), json, new DeletionScope(paths.StateDirectory, "archive-children")) is DeletionVerdict.Refused refused ? refused.Reason : string.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }

    /// <summary>The recorded children still alive in this boot — each the SAME process (pid and start ticks) in
    /// <paramref name="processes"/>; none when the record is of another boot.</summary>
    public static IReadOnlyList<ProcessEntry> Alive(ArchiveChildFile record, string bootId, IEnumerable<ProcessEntry> processes)
    {
        if (record.Children.Count == 0 || record.BootId.Length == 0 || record.BootId != bootId)
        {
            return [];
        }

        var running = processes.ToList();
        return [.. record.Children.SelectMany(child => running.Where(p => Same(p, child)))];
    }

    private static bool Same(ProcessEntry process, ArchiveChildIdentity child) =>
        process.Pid == child.Pid && process.StartTicks is Reading<long>.Available { Value: var ticks } && ticks == child.StartTicks;

    /// <summary>Why no archive child may start now — a recorded one is still alive (9/9.4 #1) — or empty. A record whose children
    /// are all gone is retired here (written empty), so the file never holds the dead (the S4 plan round's finding 2).</summary>
    public static ChildGate Survivor(ActionContext context, CancellationToken cancellationToken)
    {
        if (context.Paths is not LinuxHostPaths linux)
        {
            return ChildGate.Free;
        }

        var record = Read(context.Paths, context.Files);
        if (record.Children.Count == 0)
        {
            return ChildGate.Free;
        }

        return context.Processes(cancellationToken) is Reading<ProcessSnapshot>.Available { Value: var snapshot }
            ? Judged(context, record, Alive(record, BootIdentity.Read(linux, context.Files), snapshot.All))
            : new ChildGate("the process table could not be read, so whether the archive's last child is still alive is not known; no second one starts", Refuses: false);
    }

    private static ChildGate Judged(ActionContext context, ArchiveChildFile record, IReadOnlyList<ProcessEntry> alive)
    {
        if (alive.Count == 0)
        {
            // The S4 own review round C-7: a record that cannot be retired cannot take the next launch either — said, never dropped.
            return Write(context.Paths, context.Files, ArchiveChildFile.Empty) is { Length: > 0 } unwritten
                ? new ChildGate($"the record of the archive's children ({FileName}) could not be retired ({unwritten}); no child starts while root cannot record one", Refuses: true)
                : ChildGate.Free;
        }

        return new ChildGate(StillAlive(record, alive[0]), Refuses: false);
    }

    private static string StillAlive(ArchiveChildFile record, ProcessEntry first)
    {
        var child = record.Children.First(c => c.Pid == first.Pid);
        return string.Create(CultureInfo.InvariantCulture, $"the archive's last child ({child.Template}, pid {first.Pid}, state {first.State}, started {child.LaunchedUtc:yyyy-MM-dd HH:mm} UTC) is still alive{(first.State == 'D' ? " — stuck in the kernel, on the base most likely" : string.Empty)}; no second one starts until it is gone");
    }

    /// <summary>The launcher root just started, recorded at once (before any line arrives): the record is replaced, never grown.</summary>
    public static string Launched(ActionContext context, string template, int pid, CancellationToken cancellationToken) =>
        context.Paths is LinuxHostPaths linux
            ? Write(context.Paths, context.Files, new ArchiveChildFile(SchemaVersion.Current, BootIdentity.Read(linux, context.Files), Identities(context, template, [pid], Launcher, cancellationToken)))
            : string.Empty;

    /// <summary>The worker under the launcher (the product binary <c>runuser</c> started), added once it shows in the table.</summary>
    public static string WorkerSeen(ActionContext context, string template, int launcher, CancellationToken cancellationToken)
    {
        var record = Read(context.Paths, context.Files);
        if (record.Children.Any(c => c.Role == Worker) || context.Processes(cancellationToken) is not Reading<ProcessSnapshot>.Available { Value: var snapshot })
        {
            return string.Empty;
        }

        var workers = snapshot.All.Where(p => p.ParentPid == launcher).Select(p => p.Pid).ToList();
        return workers.Count == 0 ? string.Empty : Write(context.Paths, context.Files, record with { Children = [.. record.Children, .. Identities(context, template, workers, Worker, cancellationToken)] });
    }

    /// <summary>After the child ended: the record keeps only those still alive (normally none — written empty).</summary>
    public static string Ended(ActionContext context, CancellationToken cancellationToken)
    {
        var record = Read(context.Paths, context.Files);
        if (context.Paths is not LinuxHostPaths linux || context.Processes(cancellationToken) is not Reading<ProcessSnapshot>.Available { Value: var snapshot })
        {
            return string.Empty;
        }

        var alive = Alive(record, BootIdentity.Read(linux, context.Files), snapshot.All).Select(p => p.Pid).ToHashSet();
        return Write(context.Paths, context.Files, alive.Count == 0 ? ArchiveChildFile.Empty : record with { Children = [.. record.Children.Where(c => alive.Contains(c.Pid))] });
    }

    private static IReadOnlyList<ArchiveChildIdentity> Identities(ActionContext context, string template, IReadOnlyList<int> pids, string role, CancellationToken cancellationToken)
    {
        var all = context.Processes(cancellationToken) is Reading<ProcessSnapshot>.Available { Value: var snapshot } ? snapshot.All : [];
        return [.. pids.Select(pid => new ArchiveChildIdentity(role, template, pid, StartOf(all, pid), context.Clock.GetUtcNow()))];
    }

    /// <summary>The start ticks the table shows for <paramref name="pid"/>; 0 (no process matches it) when it is not there.</summary>
    private static long StartOf(IReadOnlyList<ProcessEntry> all, int pid) =>
        all.FirstOrDefault(p => p.Pid == pid)?.StartTicks is Reading<long>.Available { Value: var ticks } ? ticks : 0;
}
