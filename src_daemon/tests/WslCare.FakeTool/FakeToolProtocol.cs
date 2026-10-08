using System.Text;
using System.Text.Json;

namespace WslCare.FakeTool;

/// <summary>
/// The contract between the fake tools and the scenario harness, in ONE place: the harness
/// references this assembly and uses these types, so the two sides cannot disagree about a variable
/// name or a line format.
/// </summary>
public static class FakeToolProtocol
{
    /// <summary>The JSONL file every invocation appends one <see cref="FakeCall"/> line to. Required:
    /// without it the fake refuses to run, so a stray copy can never pose as the real tool.</summary>
    public const string CallsVariable = "WSL_CARE_FAKE_CALLS";

    /// <summary>The JSON file of scripted answers (<see cref="FakeScript"/>); optional.</summary>
    public const string ScriptVariable = "WSL_CARE_FAKE_SCRIPT";

    /// <summary>Comma-separated names of environment variables each call records beside its argv (E4: what the installer
    /// handed <c>gh</c> — its isolated configuration and cache folders, and no token); optional.</summary>
    public const string RecordEnvironmentVariable = "WSL_CARE_FAKE_RECORD_ENV";

    /// <summary>Exit code when <see cref="CallsVariable"/> is unset — not inside a scenario.</summary>
    public const int NotInAScenario = 97;

    /// <summary>Exit code for an invocation the scenario did not script: loud, never a silent success.</summary>
    public const int Unscripted = 98;

    /// <summary>The names the harness installs the fake under (plan §15 #14, §16 E1.S3). <c>powershell</c>
    /// joined in E2.S1: the Windows clock is a slow process (plan §15b #5), and <c>status</c> must be seen
    /// NOT to start it. <c>timedatectl</c> and <c>snap</c> joined in E2.S3: the health collectors and A9 read them. <c>curl</c> joined with the Windows Time guard
    /// (PLAN_windows_time_guard.md D2): the clock reference's HEAD, so no scenario reaches the network.</summary>
    public static readonly IReadOnlyList<string> Tools = ["docker", "systemctl", "journalctl", "powershell", "timedatectl", "snap", "curl"];

    /// <summary>The file name a tool is installed under: <c>.exe</c> on Windows; on Linux the bare name,
    /// except PowerShell, which a WSL distro reaches through interop as <c>powershell.exe</c>. The fake
    /// names itself from its file name without the extension, so every spelling records as <paramref name="tool"/>.</summary>
    public static string FileName(string tool, bool windows) =>
        windows ? tool + ".exe" : tool == "powershell" ? "powershell.exe" : tool;
}

/// <summary>One invocation of a fake: which tool it posed as, and its argv exactly as received.</summary>
public sealed record FakeCall(string Tool, IReadOnlyList<string> Argv)
{
    /// <summary>The directory the fake was started FROM — which copy answered. A decoy planted outside the scenario's
    /// <c>PATH</c> records its own folder here, so a scenario can tell the product reached the one it meant to.</summary>
    public string Location { get; init; } = string.Empty;

    /// <summary>The variables named by <see cref="FakeToolProtocol.RecordEnvironmentVariable"/> that were SET in the call's
    /// environment, with their values; a variable absent from the call is absent here.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The process id of the fake that answered — so a test can look for that process after the harness gave up on it
    /// (0 in a log written before the field existed).</summary>
    public int ProcessId { get; init; }

    public string Display => Argv.Count == 0 ? Tool : $"{Tool} {string.Join(' ', Argv)}";

    public bool Matches(string tool, IReadOnlyList<string> argv) =>
        string.Equals(Tool, tool, StringComparison.Ordinal) && Argv.SequenceEqual(argv, StringComparer.Ordinal);
}

/// <summary>The argv log: one compact JSON object per line, appended under an exclusive open.</summary>
public static class FakeCallLog
{
    private static readonly TimeSpan LockCeiling = TimeSpan.FromSeconds(10);

    public static void Append(string path, FakeCall call)
    {
        var line = Encoding.UTF8.GetBytes(Serialize(call) + "\n");
        var deadline = DateTime.UtcNow + LockCeiling;
        while (true)
        {
            try
            {
                // Exclusive open + seek to the end, not FileMode.Append: two fakes started at once must
                // write whole lines, one after the other.
                using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
                stream.Seek(0, SeekOrigin.End);
                stream.Write(line);
                return;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);
            }
        }
    }

    /// <summary>Every call recorded so far, in order; none when the file does not exist yet.</summary>
    public static IReadOnlyList<FakeCall> ReadAll(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        // A fake appends under an EXCLUSIVE open (Append above), so a read at that instant is refused with a sharing violation:
        // wait for the writer, as a writer waits for another writer (main's win-x64 leg, run 37793636777, 2026-10-08).
        var deadline = DateTime.UtcNow + LockCeiling;
        while (true)
        {
            try
            {
                return [.. File.ReadAllLines(path).Where(l => l.Length > 0).Select(Deserialize)];
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);
            }
        }
    }

    private static string Serialize(FakeCall call)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("tool", call.Tool);
            json.WriteStartArray("argv");
            foreach (var arg in call.Argv)
            {
                json.WriteStringValue(arg);
            }

            json.WriteEndArray();
            json.WriteString("location", call.Location);
            json.WriteNumber("pid", call.ProcessId);
            json.WriteStartObject("env");
            foreach (var (name, value) in call.Environment)
            {
                json.WriteString(name, value);
            }

            json.WriteEndObject();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static FakeCall Deserialize(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        return new FakeCall(
            root.GetProperty("tool").GetString() ?? string.Empty,
            [.. root.GetProperty("argv").EnumerateArray().Select(a => a.GetString() ?? string.Empty)])
        {
            Location = root.TryGetProperty("location", out var location) ? location.GetString() ?? string.Empty : string.Empty,
            Environment = root.TryGetProperty("env", out var env) ? ReadEnvironment(env) : new Dictionary<string, string>(StringComparer.Ordinal),
            ProcessId = root.TryGetProperty("pid", out var pid) ? pid.GetInt32() : 0,
        };
    }

    private static Dictionary<string, string> ReadEnvironment(JsonElement env) =>
        env.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty, StringComparer.Ordinal);
}

/// <summary>One scripted answer: when <see cref="Tool"/> is called with exactly <see cref="Argv"/>,
/// print the bytes of <see cref="StdoutFile"/> (a fixture; empty = nothing), then <see cref="Stderr"/>,
/// and exit with <see cref="ExitCode"/> — after <see cref="DelayMilliseconds"/>, which a scenario sets past the
/// product's ceiling to stand in for a tool that hangs (E2.S2).</summary>
/// <remarks>Since E2.S3 an answer can also match by PREFIX (<see cref="Prefix"/>: the scripted argv is the start of the
/// call's — for an argv that carries an instant, such as <c>docker events --since …</c>), apply only to the first
/// <see cref="UpTo"/> calls that match it (0 = every call; a later answer for the same call then takes over — a
/// daemon that is down, then up), and hold the process open AFTER its output (<see cref="HangAfterMilliseconds"/>:
/// a live stream nobody ended, which the product must cut off or a signal must stop).</remarks>
public sealed record FakeAnswer(string Tool, IReadOnlyList<string> Argv, int ExitCode, string StdoutFile, string Stderr, int DelayMilliseconds = 0)
{
    public bool Prefix { get; init; }

    public int UpTo { get; init; }

    public int HangAfterMilliseconds { get; init; }

    /// <summary>When set (E4.S1: <c>--output</c>, curl's), the fixture's bytes go to the FILE named by the argument after
    /// this flag in the call instead of stdout — a download the installer under test writes to a path in its own
    /// temporary folder, which the scenario cannot know in advance. A call without the flag, or with the flag last,
    /// writes nothing and exits <see cref="FakeToolProtocol.Unscripted"/>, saying why.</summary>
    public string OutputFlag { get; init; } = string.Empty;

    /// <summary>When set (E4: <c>gh attestation verify</c>), the fake does not print a fixture: it VERIFIES, enforcing the
    /// identity flags of the call over the bundle the call names — or, with no <c>--bundle</c>, over
    /// <see cref="StdoutFile"/>, which then stands for what GitHub's API would hand gh online
    /// (<see cref="FakeAttestation"/>).</summary>
    public bool VerifiesAttestation { get; init; }

    public bool Matches(FakeCall call) =>
        string.Equals(call.Tool, Tool, StringComparison.Ordinal)
        && (Prefix ? Argv.Count <= call.Argv.Count && Argv.SequenceEqual(call.Argv.Take(Argv.Count), StringComparer.Ordinal) : call.Argv.SequenceEqual(Argv, StringComparer.Ordinal));
}

/// <summary>The script file: a JSON object holding an <c>answers</c> array.</summary>
public static class FakeScript
{
    public static void Write(string path, IReadOnlyList<FakeAnswer> answers)
    {
        using var stream = File.Create(path);
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteStartArray("answers");
        foreach (var answer in answers)
        {
            json.WriteStartObject();
            json.WriteString("tool", answer.Tool);
            json.WriteStartArray("argv");
            foreach (var arg in answer.Argv)
            {
                json.WriteStringValue(arg);
            }

            json.WriteEndArray();
            json.WriteNumber("exitCode", answer.ExitCode);
            json.WriteString("stdoutFile", answer.StdoutFile);
            json.WriteString("stderr", answer.Stderr);
            json.WriteNumber("delayMs", answer.DelayMilliseconds);
            json.WriteBoolean("prefix", answer.Prefix);
            json.WriteNumber("upTo", answer.UpTo);
            json.WriteNumber("hangAfterMs", answer.HangAfterMilliseconds);
            json.WriteString("outputFlag", answer.OutputFlag);
            json.WriteBoolean("verifiesAttestation", answer.VerifiesAttestation);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    /// <summary>The first answer scripted for <paramref name="call"/> that is not used up, or <c>null</c>.
    /// <paramref name="earlierCalls"/> is the argv log so far (this call included): an answer with
    /// <see cref="FakeAnswer.UpTo"/> applies while at most that many logged calls match it.</summary>
    public static FakeAnswer? Find(string path, FakeCall call, IReadOnlyList<FakeCall> earlierCalls)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var element in document.RootElement.GetProperty("answers").EnumerateArray())
        {
            var answer = Read(element);
            if (answer.Matches(call) && (answer.UpTo == 0 || earlierCalls.Count(answer.Matches) <= answer.UpTo))
            {
                return answer;
            }
        }

        return null;
    }

    /// <summary>The first answer for <paramref name="call"/>, ignoring <see cref="FakeAnswer.UpTo"/>.</summary>
    public static FakeAnswer? Find(string path, FakeCall call) => Find(path, call, []);

    private static FakeAnswer Read(JsonElement element) =>
        new(
            element.GetProperty("tool").GetString() ?? string.Empty,
            [.. element.GetProperty("argv").EnumerateArray().Select(a => a.GetString() ?? string.Empty)],
            element.GetProperty("exitCode").GetInt32(),
            element.GetProperty("stdoutFile").GetString() ?? string.Empty,
            element.GetProperty("stderr").GetString() ?? string.Empty,
            element.TryGetProperty("delayMs", out var delay) ? delay.GetInt32() : 0)
        {
            Prefix = element.TryGetProperty("prefix", out var prefix) && prefix.GetBoolean(),
            UpTo = element.TryGetProperty("upTo", out var upTo) ? upTo.GetInt32() : 0,
            HangAfterMilliseconds = element.TryGetProperty("hangAfterMs", out var hang) ? hang.GetInt32() : 0,
            OutputFlag = element.TryGetProperty("outputFlag", out var output) ? output.GetString() ?? string.Empty : string.Empty,
            VerifiesAttestation = element.TryGetProperty("verifiesAttestation", out var verifies) && verifies.GetBoolean(),
        };
}
