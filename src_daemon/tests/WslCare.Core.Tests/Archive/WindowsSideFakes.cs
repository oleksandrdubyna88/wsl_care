using WslCare.Core.Archive;
using WslCare.Core.Collectors;
using WslCare.Core.Mcp;

namespace WslCare.Core.Tests.Archive;

/// <summary>A Restart Manager that gives one answer to every question.</summary>
internal sealed class GivenAnswers(RmAnswer answer) : IRestartManager
{
    public RmAnswer Holders(IReadOnlyList<string> files) => answer;
}

/// <summary>A process table of the given processes, every command line the given one (none when empty).</summary>
internal sealed class GivenTable(IReadOnlyList<WindowsProcessEntry> processes, string commandLine) : IWindowsProcessTable
{
    public Reading<IReadOnlyList<WindowsProcessEntry>> List() => Reading.Of(processes);

    public WindowsProcessDetails Details(int pid) => WindowsProcessDetails.Unopenable("not asked");

    public Reading<string> CommandLine(int pid) => commandLine.Length > 0 ? Reading.Of(commandLine) : Reading.Missing<string>("none given");
}
