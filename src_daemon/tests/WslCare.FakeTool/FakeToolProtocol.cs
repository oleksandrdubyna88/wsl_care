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

    /// <summary>Exit code when <see cref="CallsVariable"/> is unset — not inside a scenario.</summary>
    public const int NotInAScenario = 97;

    /// <summary>Exit code for an invocation the scenario did not script: loud, never a silent success.</summary>
    public const int Unscripted = 98;

    /// <summary>The names the harness installs the fake under (plan §15 #14, §16 E1.S3).</summary>
    public static readonly IReadOnlyList<string> Tools = ["docker", "systemctl", "journalctl"];
}

/// <summary>One invocation of a fake: which tool it posed as, and its argv exactly as received.</summary>
public sealed record FakeCall(string Tool, IReadOnlyList<string> Argv)
{
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

        return [.. File.ReadAllLines(path).Where(l => l.Length > 0).Select(Deserialize)];
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
            [.. root.GetProperty("argv").EnumerateArray().Select(a => a.GetString() ?? string.Empty)]);
    }
}

/// <summary>One scripted answer: when <see cref="Tool"/> is called with exactly <see cref="Argv"/>,
/// print the bytes of <see cref="StdoutFile"/> (a fixture; empty = nothing), then <see cref="Stderr"/>,
/// and exit with <see cref="ExitCode"/>.</summary>
public sealed record FakeAnswer(string Tool, IReadOnlyList<string> Argv, int ExitCode, string StdoutFile, string Stderr);

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
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    /// <summary>The first answer scripted for <paramref name="call"/>, or <c>null</c>.</summary>
    public static FakeAnswer? Find(string path, FakeCall call)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var element in document.RootElement.GetProperty("answers").EnumerateArray())
        {
            var answer = new FakeAnswer(
                element.GetProperty("tool").GetString() ?? string.Empty,
                [.. element.GetProperty("argv").EnumerateArray().Select(a => a.GetString() ?? string.Empty)],
                element.GetProperty("exitCode").GetInt32(),
                element.GetProperty("stdoutFile").GetString() ?? string.Empty,
                element.GetProperty("stderr").GetString() ?? string.Empty);
            if (call.Matches(answer.Tool, answer.Argv))
            {
                return answer;
            }
        }

        return null;
    }
}
