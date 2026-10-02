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

        var call = new FakeCall(tool, args) { Location = Path.GetDirectoryName(Environment.ProcessPath) ?? string.Empty };
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
}
