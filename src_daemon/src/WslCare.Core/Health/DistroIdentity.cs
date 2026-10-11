using System.Text.Json;
using System.Text.RegularExpressions;

using WslCare.Core.Collectors;
using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Hosting;
using WslCare.Core.Json;

namespace WslCare.Core.Health;

/// <summary><c>{state}/distro-name.json</c>: this distro's Lxss name as a root run started through <c>wsl.exe</c> last saw it.</summary>
public sealed record DistroNameFile(int SchemaVersion, string Name, DateTimeOffset RecordedAt);

/// <summary>
/// Which Lxss distro is this one (E15.S2 design §1, measured 2026-10-11): <c>systemctl show-environment</c> has no <c>WSL_*</c>
/// variable, the wsl-care units set none, <c>wslinfo</c> has no name option — but a process started through <c>wsl.exe</c> carries
/// <c>WSL_DISTRO_NAME</c>. So a ROOT run that carries it (the panel's <i>Run full check now</i> is <c>wsl.exe -u root … collect
/// --detach</c>) records it here, and the full run — usually a systemd unit — takes the environment first, then this file.
/// </summary>
/// <remarks>The name is believed only when it passes WSL's own rule (the extension's <c>DISTRO_NAME</c>, <c>src/wsl/distros.ts</c>):
/// a letter or digit, then up to 63 letters, digits, <c>.</c>, <c>_</c> or <c>-</c>. The file is root's state (read back owned by
/// root, through no link, capped) and is rewritten only when the name changed, atomically.</remarks>
public static partial class DistroIdentity
{
    public const string Variable = "WSL_DISTRO_NAME";

    public const string FileName = "distro-name.json";

    /// <summary>Why the name is not known — and how it becomes known.</summary>
    public const string NotKnownYet = "this distro's name is not known yet — a full check started through wsl.exe records it (the panel's Run full check now does)";

    private const string Action = "distro-name";

    public static string File(IHostPaths paths) => paths.Rules.Join(paths.StateDirectory, FileName);

    /// <summary>The name a root run recorded, or why there is none.</summary>
    public static Reading<string> Read(IHostPaths paths, IFileSystem files) =>
        files.ReadStateFile(File(paths), RootFileCaps.State) switch
        {
            FileReadResult.Content content => Parse(content.Bytes),
            FileReadResult.Unreadable u => Reading.Missing<string>($"{FileName} could not be read: {u.Reason}"),
            _ => Reading.Missing<string>(NotKnownYet),
        };

    /// <summary>The environment first (a process started through <c>wsl.exe</c>), then the recorded name.</summary>
    public static Reading<string> Resolve(string environment, Reading<string> recorded) =>
        IsName(environment) ? Reading.Of(environment) : recorded;

    /// <summary>Records <paramref name="environment"/> when it is a name and differs from the recorded one. Empty when written, when
    /// unchanged and when there is no variable; otherwise why not.</summary>
    public static string Record(IHostPaths paths, IFileSystem files, string environment, DateTimeOffset now) => environment switch
    {
        { Length: 0 } => string.Empty,
        _ when !IsName(environment) => $"\"{Printable(environment)}\" is not a distribution name (WSL's rule: a letter or digit, then letters, digits, '.', '_' or '-')",
        _ when IsRecorded(paths, files, environment) => string.Empty,
        _ => Write(paths, files, new DistroNameFile(SchemaVersion.Current, environment, now)),
    };

    private static bool IsRecorded(IHostPaths paths, IFileSystem files, string name) =>
        Read(paths, files) is Reading<string>.Available { Value: var recorded } && recorded == name;

    /// <summary>WSL's own rule for a distribution name.</summary>
    public static bool IsName(string name) => NamePattern().IsMatch(name);

    private static Reading<string> Parse(byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize(bytes, WslCareJsonContext.Default.DistroNameFile) is { Name: { } name } && IsName(name)
                ? Reading.Of(name)
                : Reading.Missing<string>($"{FileName} names no distribution WSL could have");
        }
        catch (JsonException e)
        {
            return Reading.Missing<string>($"{FileName} does not parse: {e.Message}");
        }
    }

    private static string Write(IHostPaths paths, IFileSystem files, DistroNameFile file)
    {
        try
        {
            files.CreateDirectory(paths.StateDirectory);
            var json = JsonSerializer.SerializeToUtf8Bytes(file, WslCareJsonContext.Default.DistroNameFile);
            return files.WriteFileAtomically(File(paths), json, new DeletionScope(paths.StateDirectory, Action)) is DeletionVerdict.Refused refused ? refused.Reason : string.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }

    /// <summary>The value as a sentence may quote it: control characters shown as <c>?</c>.</summary>
    private static string Printable(string value) => string.Concat(value.Select(c => char.IsControl(c) ? '?' : c));

    // \z, not $: .NET's $ also matches before a final newline, and a name with a newline is no name.
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
