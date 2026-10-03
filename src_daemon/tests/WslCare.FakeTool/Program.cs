namespace WslCare.FakeTool;

/// <summary>
/// A fake <c>docker</c> / <c>systemctl</c> / <c>journalctl</c>: records its argv, then answers
/// what the scenario scripted for exactly that argv. It never does anything a real tool would do.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var tool = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "fake-tool");
        var calls = Environment.GetEnvironmentVariable(FakeToolProtocol.CallsVariable);
        if (string.IsNullOrWhiteSpace(calls))
        {
            Console.Error.WriteLine($"fake {tool}: not inside a scenario ({FakeToolProtocol.CallsVariable} is unset); refusing to stand in for the real tool.");
            return FakeToolProtocol.NotInAScenario;
        }

        var call = new FakeCall(tool, args)
        {
            Location = Path.GetDirectoryName(Environment.ProcessPath) ?? string.Empty,
            Environment = RecordedEnvironment(),
        };
        FakeCallLog.Append(calls, call);

        var script = Environment.GetEnvironmentVariable(FakeToolProtocol.ScriptVariable);
        var answer = string.IsNullOrWhiteSpace(script) ? null : FakeScript.Find(script, call, FakeCallLog.ReadAll(calls));
        if (answer is null)
        {
            Console.Error.WriteLine($"fake {tool}: no scripted answer for: {call.Display}");
            return FakeToolProtocol.Unscripted;
        }

        if (answer.DelayMilliseconds > 0)
        {
            // A hang the product must cut off: it kills this process's tree at its ceiling.
            Thread.Sleep(answer.DelayMilliseconds);
        }

        return Respond(tool, call, answer);
    }

    /// <summary>The scripted answer, in the mode it asks for: verify an attestation, write to a file argument, or print.</summary>
    private static int Respond(string tool, FakeCall call, FakeAnswer answer) => answer switch
    {
        { VerifiesAttestation: true } => FakeAttestation.Verify(call.Argv, answer.StdoutFile),
        { OutputFlag.Length: > 0 } => WriteToOutputArgument(tool, call, answer),
        _ => Print(answer),
    };

    private static int Print(FakeAnswer answer)
    {
        if (answer.StdoutFile.Length > 0)
        {
            // The fixture's bytes, unchanged: no re-encoding, no added newline.
            using var stdout = Console.OpenStandardOutput();
            stdout.Write(File.ReadAllBytes(answer.StdoutFile));
        }

        if (answer.Stderr.Length > 0)
        {
            Console.Error.Write(answer.Stderr);
        }

        if (answer.HangAfterMilliseconds > 0)
        {
            // A live stream nobody ended: the output is out, the process stays until killed or the time passes.
            Console.Out.Flush();
            Thread.Sleep(answer.HangAfterMilliseconds);
        }

        return answer.ExitCode;
    }

    /// <summary>The variables the scenario asked every call to record (<see cref="FakeToolProtocol.RecordEnvironmentVariable"/>)
    /// that are set in this call's environment.</summary>
    private static Dictionary<string, string> RecordedEnvironment() =>
        (Environment.GetEnvironmentVariable(FakeToolProtocol.RecordEnvironmentVariable) ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => (Name: name, Value: Environment.GetEnvironmentVariable(name)))
            .Where(v => v.Value is not null)
            .ToDictionary(v => v.Name, v => v.Value!, StringComparer.Ordinal);

    /// <summary>The fixture's bytes, unchanged, to the file the call names after <see cref="FakeAnswer.OutputFlag"/>.</summary>
    private static int WriteToOutputArgument(string tool, FakeCall call, FakeAnswer answer)
    {
        var at = call.Argv.ToList().IndexOf(answer.OutputFlag);
        if (at < 0 || at + 1 >= call.Argv.Count)
        {
            Console.Error.WriteLine($"fake {tool}: the answer writes to the argument after {answer.OutputFlag}, and the call has none: {call.Display}");
            return FakeToolProtocol.Unscripted;
        }

        if (answer.StdoutFile.Length > 0)
        {
            File.WriteAllBytes(call.Argv[at + 1], File.ReadAllBytes(answer.StdoutFile));
        }

        if (answer.Stderr.Length > 0)
        {
            Console.Error.Write(answer.Stderr);
        }

        return answer.ExitCode;
    }
}
