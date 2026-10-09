using FluentAssertions;

using WslCare.Core.Mcp;

namespace WslCare.Core.Tests.Mcp;

/// <summary>E14 S7a: the REAL Windows process table — a Toolhelp snapshot, and read-only handles to one process at a time. Windows
/// only; it reads THIS test process and its parent, and changes nothing.</summary>
public sealed class Win32ProcessTableTests
{
    [Fact]
    public void The_real_process_table_lists_this_test_process_with_its_parent()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("the Win32 process table is the Windows binary's");
            return;
        }

        var table = new Win32ProcessTable();

        var list = table.List().Should().BeOfType<Core.Collectors.Reading<IReadOnlyList<WindowsProcessEntry>>.Available>().Subject.Value;
        var self = list.Should().ContainSingle(p => p.Pid == Environment.ProcessId).Subject;
        var details = table.Details(self.Pid);

        self.ExeName.Should().StartWith("WslCare.Core.Tests", "the snapshot names the executable");
        list.Should().Contain(p => p.Pid == self.ParentPid, "the parent that started this test runner is alive");
        details.Created.IsAvailable.Should().BeTrue();
        details.CpuTime.IsAvailable.Should().BeTrue();
        details.WorkingSet.Should().Match<Core.Collectors.Reading<long>>(w => w.IsAvailable && w.ValueOr(0) > 0);
        details.PrivateBytes.IsAvailable.Should().BeTrue();
        table.Details(int.MaxValue - 1).Created.IsAvailable.Should().BeFalse("a pid nobody holds cannot be opened: unavailable, never a figure");
    }
}
