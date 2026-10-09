using System.Diagnostics;

namespace WslCare.TestSupport;

/// <summary>A CHILD process holding one file open with NO sharing until it is disposed — the fixture process of the archive's Windows
/// open-file tests (plan §15r E9.S5). Windows PowerShell opens the file and says so on stdout; disposing kills THAT child by its own
/// process object (never by image name) and waits for it.</summary>
public sealed class HoldingChild : IDisposable
{
    private const string Script = "$f = [System.IO.File]::Open($env:WSL_CARE_TEST_HOLD, 'Open', 'ReadWrite', 'None'); [Console]::Out.WriteLine('held'); [Console]::Out.Flush(); Start-Sleep -Seconds 120";

    private readonly Process _process;

    private HoldingChild(Process process) => _process = process;

    public int Pid => _process.Id;

    public static HoldingChild Hold(string path)
    {
        var info = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", Script })
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment["WSL_CARE_TEST_HOLD"] = path;
        var process = Process.Start(info) ?? throw new InvalidOperationException("powershell.exe did not start");
        var line = process.StandardOutput.ReadLine();
        return line == "held" ? new HoldingChild(process) : throw new InvalidOperationException($"the holding child said {line}");
    }

    public void Dispose()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(TimeSpan.FromSeconds(30));
        }
        catch (InvalidOperationException)
        {
            // It exited already.
        }

        _process.Dispose();
    }
}
