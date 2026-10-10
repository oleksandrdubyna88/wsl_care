using System.Globalization;

using WslCare.Core.Config;
using WslCare.Core.Files;

namespace WslCare.Core.Archive;

/// <summary>
/// Plan §15r, the E9.S5 amendment (owner decision 2026-10-09) — whether a Claude Code session on Windows is IDLE: every one of its files
/// untouched for <see cref="Idle"/> (<c>archive.windowsIdleDays</c>). While Claude Code runs there (its working folder cannot be read),
/// only an idle session may move. The times are read when asked — the selection asks, and phase 2 asks again a day or more later — so
/// a session resumed in between is seen.
/// </summary>
/// <remarks>
/// <para><b>The clock.</b> A file dated after now by more than <see cref="Skew"/> (<c>archive.clockSkewMinutes</c>: a NAS stamps a
/// share's files, a WSL VM clock drifts after sleep) keeps its session, saying so — which clock is wrong is not guessed. A file dated
/// after now by less counts as touched now. The Windows clock is the machine's reference (the time service keeps it), so a jump of
/// days is not guarded here: the open-file check and the copies' verification still stand.</para>
/// <para><b>Fails closed.</b> A time that cannot be read keeps the session. A name that does not exist is skipped — phase 2's resume
/// asks the quarantine names beside the originals — and a session none of whose names exists is idle: there is nothing an agent
/// could be using, and keeping it would only hold a finished removal open (the own review, finding 5).</para>
/// <para><b>The tolerance decides too.</b> A session is idle only when its newest file is older than the window AND the tolerance, so a
/// clock a few minutes ahead does not let a session at the edge of the window go (the own review, finding 4).</para>
/// </remarks>
public sealed record WindowsIdle(Func<string, FileSizeResult> Stat, TimeProvider Clock, TimeSpan Idle, TimeSpan Skew)
{
    public static WindowsIdle Of(IFileSystem files, EffectiveConfig config, TimeProvider clock) => Of(files.FileSize, config, clock);

    public static WindowsIdle Of(Func<string, FileSizeResult> stat, EffectiveConfig config, TimeProvider clock) =>
        new(stat, clock, TimeSpan.FromDays(config.Int(ConfigKeys.Archive.WindowsIdleDays)), TimeSpan.FromMinutes(config.Int(ConfigKeys.Archive.ClockSkewMinutes)));

    /// <summary>Why the session of <paramref name="files"/> (full paths) is NOT idle now; empty when every file of it is.</summary>
    public string Problem(IReadOnlyList<string> files)
    {
        var read = files.Select(Stat).ToList();
        return read.Any(r => r is FileSizeResult.Unreadable) ? "a file's time could not be read, so whether it is idle cannot be told"
            : Newest(read) is { } newest ? Judged(newest, Clock.GetUtcNow())
            : string.Empty;
    }

    /// <summary>The newest last write among the files that exist; <c>null</c> when none does — the one "not found" of this judgement.</summary>
    private static DateTimeOffset? Newest(IReadOnlyList<FileSizeResult> read) =>
        read.OfType<FileSizeResult.Measured>().Select(m => (DateTimeOffset?)m.ModifiedAt).Max();

    private string Judged(DateTimeOffset newest, DateTimeOffset now) =>
        newest > now + Skew ? string.Create(CultureInfo.InvariantCulture, $"a file of it is dated {Stamp(newest)}, after this machine's clock ({Stamp(now)}) by more than {ConfigKeys.Archive.ClockSkewMinutes.Name} ({Skew.TotalMinutes:0} min); whether it is idle cannot be told")
        : newest > now - Idle - Skew ? string.Create(CultureInfo.InvariantCulture, $"a file of it changed within {ConfigKeys.Archive.WindowsIdleDays.Name} ({Idle.TotalDays:0} days; the newest at {Stamp(newest)})")
        : string.Empty;

    private static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
