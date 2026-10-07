using WslCare.Core.Config;
using System.Globalization;
using System.Text;

namespace WslCare.Core.Collectors.Procfs;

/// <summary>
/// <c>/proc/[pid]/status</c>, the fields the process table reads (plan §4.2 as amended by §15b #4):
/// process memory is <c>RssAnon</c> + <c>RssShmem</c> — the pages that count in <c>AnonPages</c> and
/// <c>Shmem</c> — never <c>VmRSS</c>, whose file-backed share is page cache already counted elsewhere.
/// </summary>
/// <param name="IsKernelThread"><c>Kthread: 1</c>, or (older kernels) no <c>RssAnon</c> line at all:
/// a kernel thread owns no user memory and is not a holder.</param>
public sealed record ProcStatus(string Name, char State, int ParentPid, int Uid, long RssAnonBytes, long RssShmemBytes, bool IsKernelThread)
{
    private const long BytesPerKibibyte = 1024;

    public long HeldBytes => RssAnonBytes + RssShmemBytes;

    public static Reading<ProcStatus> Parse(string text, string path)
    {
        var fields = ProcText.Lines(text)
            .Select(l => l.Split(':', 2))
            .Where(kv => kv.Length == 2)
            .GroupBy(kv => kv[0], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First()[1].Trim(), StringComparer.Ordinal);
        return fields.ContainsKey("Name") && fields.ContainsKey("PPid")
            ? Reading.Of(From(fields))
            : Reading.Missing<ProcStatus>($"{path} has no Name or PPid line");
    }

    private static ProcStatus From(IReadOnlyDictionary<string, string> fields) =>
        new(
            fields["Name"],
            fields.GetValueOrDefault("State", "?").FirstOrDefault('?'),
            FirstNumber(fields.GetValueOrDefault("PPid", "0")),
            FirstNumber(fields.GetValueOrDefault("Uid", "-1")),
            Kibibytes(fields.GetValueOrDefault("RssAnon", "0 kB")),
            Kibibytes(fields.GetValueOrDefault("RssShmem", "0 kB")),
            fields.GetValueOrDefault("Kthread") == "1" || !fields.ContainsKey("RssAnon"));

    private static int FirstNumber(string text) =>
        int.TryParse(text.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(string.Empty), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : -1;

    private static long Kibibytes(string text) =>
        long.TryParse(text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(string.Empty), NumberStyles.None, CultureInfo.InvariantCulture, out var kib) ? kib * BytesPerKibibyte : 0;
}

/// <summary>
/// <c>/proc/[pid]/stat</c>, the fields after the command name: the name is parenthesised and may hold
/// spaces and parentheses itself, so the fields are counted from the LAST <c>)</c> (proc(5)).
/// </summary>
/// <param name="TtyNumber">Field 7, <c>tty_nr</c>: 0 means no controlling terminal.</param>
/// <param name="CpuTicks">Fields 14 + 15, <c>utime</c> + <c>stime</c>, in clock ticks.</param>
/// <param name="StartTicks">Field 22, <c>starttime</c>: clock ticks after boot.</param>
public sealed record ProcStat(int TtyNumber, long CpuTicks, long StartTicks)
{
    // Field numbers of proc(5), counted from 1; the text after the last ')' starts at field 3.
    private const int FirstFieldAfterName = 3;
    private const int TtyField = 7;
    private const int UserTimeField = 14;
    private const int SystemTimeField = 15;
    private const int StartTimeField = 22;

    public static Reading<ProcStat> Parse(string text, string path)
    {
        var close = text.LastIndexOf(')');
        var fields = close < 0 ? [] : text[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length > StartTimeField - FirstFieldAfterName
            ? Reading.Of(new ProcStat((int)Field(fields, TtyField), Field(fields, UserTimeField) + Field(fields, SystemTimeField), Field(fields, StartTimeField)))
            : Reading.Missing<ProcStat>($"{path} has fewer fields than proc(5) describes");
    }

    private static long Field(string[] fields, int number) =>
        long.TryParse(fields[number - FirstFieldAfterName], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : 0;
}

/// <summary><c>/proc/[pid]/cgroup</c>: the process's cgroup v2 path (the <c>0::</c> line), relative to
/// the cgroup namespace the reader lives in — the same namespace <c>/sys/fs/cgroup</c> is mounted from.</summary>
public static class ProcCgroup
{
    public static string Parse(string text) =>
        ProcText.Lines(text).FirstOrDefault(l => l.StartsWith("0::", StringComparison.Ordinal)) is { } line ? line[3..] : string.Empty;
}

/// <summary>
/// <c>/proc/[pid]/cmdline</c>: the argv, NUL-separated. Shown truncated (plan §4.2: 200 characters)
/// and with secret-looking values replaced, because a command line carries whatever a launcher put
/// there — measured 2026-10-02 on this machine: the VS Code server's <c>--connection-token=</c> and an
/// agent hub's <c>--csrf_token=</c> — and the status answer ends up in a panel and, from E2.S3, in a
/// history file other accounts can read.
/// </summary>
public static class CommandLineText
{
    /// <summary>The plan's limit on a shown command line (§4.2).</summary>
    public static int ShownLength => Tuning.Current.Int(ConfigKeys.Processes.ShownCommandChars);

    private const string Redacted = "<redacted>";

    private static readonly string[] SecretWords = ["token", "secret", "password", "passwd", "apikey", "api-key", "api_key", "credential"];

    /// <summary>The program and the word after it, as FILE NAMES (<c>.exe</c> stripped) — what an agent or an interpreter-run script
    /// is recognised by (plan §15q E7.S2d C-2). Taken from the RAW argv: the shown line is redacted and cut, and splitting it on
    /// spaces loses a program whose path holds one.</summary>
    public static IReadOnlyList<string> ProgramNames(IEnumerable<string> argv) =>
        [.. argv.Take(ProgramWords).Select(word => Path.GetFileName(word.Replace('\\', '/'))).Select(StripExe)];

    /// <summary>The program and its first argument: an interpreter (<c>node</c>) names its script there.</summary>
    private const int ProgramWords = 2;

    private static string StripExe(string name) => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    public static IReadOnlyList<string> Arguments(ReadOnlySpan<byte> cmdline) =>
        Encoding.UTF8.GetString(cmdline).Split('\0').SkipLast(cmdline.Length > 0 && cmdline[^1] == 0 ? 1 : 0).ToList();

    /// <summary>The argv as one line: secret-looking values redacted, then cut to <see cref="ShownLength"/>.</summary>
    public static string Shown(IReadOnlyList<string> argv)
    {
        var line = string.Join(' ', Redact(argv));
        return line.Length <= ShownLength ? line : line[..ShownLength];
    }

    /// <summary><c>--name=value</c> and <c>--name value</c> where the name holds a secret word lose the value.</summary>
    public static IReadOnlyList<string> Redact(IReadOnlyList<string> argv) =>
        [.. argv.Select((arg, i) => RedactOne(arg, i > 0 ? argv[i - 1] : string.Empty))];

    private static string RedactOne(string argument, string previous) =>
        IsSecretAssignment(argument) ? argument[..(argument.IndexOf('=') + 1)] + Redacted
        : IsSecretFlag(previous) ? Redacted
        : argument;

    private static bool IsSecretAssignment(string argument)
    {
        var equals = argument.IndexOf('=');
        return argument.StartsWith('-') && equals > 0 && NamesASecret(argument[..equals]);
    }

    private static bool IsSecretFlag(string argument) =>
        argument.StartsWith('-') && !argument.Contains('=') && NamesASecret(argument);

    private static bool NamesASecret(string name) =>
        SecretWords.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));
}
