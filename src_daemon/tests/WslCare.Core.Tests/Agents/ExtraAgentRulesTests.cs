using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Agents;
using WslCare.Core.Files;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Agents;

/// <summary>
/// The FILESYSTEM rules of a manual agent's data folder (plan §15q R2.1), judged at <c>config set</c> and again at every root
/// read: strictly inside the home's real path, on the home's filesystem, and not equal to, inside or containing <c>~/git</c>,
/// a catalogue agent's folder, any folder a cleanup action cleans (review M8) or the product's own folders (review M9).
/// </summary>
public sealed class ExtraAgentRulesTests : IDisposable
{
    private readonly LinuxSandbox _sandbox = new("extra-rules");

    public void Dispose() => _sandbox.Dispose();

    private ExtraJudgement Judge(string folder, IFileSystem? files = null)
    {
        var agent = new ExtraAgent("/home/me/.local/bin/mycli", ExtraAgentShape.Wsl, "mycli", [folder], string.Empty);
        return ExtraAgentRules.Judge(_sandbox.Paths, files ?? _sandbox.Files, [agent], ExtraAgentRules.CleanupRoots(ActionRegistry.Product, _sandbox.Paths.Home, _sandbox.Paths.Rules)).Single();
    }

    private void Folder(string distroPath) => Directory.CreateDirectory(_sandbox.Paths.DistroPath(distroPath));

    [Fact]
    public void A_folder_of_its_own_inside_the_home_is_accepted()
    {
        Folder("/home/me/.mycli");

        var judged = Judge("/home/me/.mycli");

        judged.Accepted.Should().BeTrue(judged.Refusal);
        judged.Folders.Should().Equal(_sandbox.Paths.DistroPath("/home/me/.mycli"));
    }

    [Theory]
    [InlineData("/data/mycli", "is not inside the home")]
    [InlineData("/home/me", "is not inside the home")]
    [InlineData("/home/me/git/project", "~/git")]
    [InlineData("/home/me/.claude", "Claude Code's folder ~/.claude (already tracked)")]
    [InlineData("/home/me/.claude/projects", "Claude Code's folder ~/.claude (already tracked)")]
    [InlineData("/home/me/.npm", "A8's cleanup folder ~/.npm")]
    [InlineData("/home/me/.cache/ms-playwright/chromium-1", "A12's cleanup folder ~/.cache/ms-playwright")]
    [InlineData("/home/me/.cache", "Antigravity's folder ~/.cache/antigravity")]
    [InlineData("/home/me/.local/share/pnpm/store/v3", "A17's cleanup folder ~/.local/share/pnpm/store")]
    [InlineData("/home/me/.vscode-server", "A14's cleanup folder ~/.vscode-server")]
    [InlineData("/home/me/.config/wsl-care", "wsl-care's own folder")]
    [InlineData("/home/me/.config", "wsl-care's own folder")]
    public void A_folder_that_leaves_the_home_or_overlaps_a_protected_or_cleaned_folder_is_refused_naming_whose(string folder, string whose)
    {
        Folder(folder);

        Judge(folder).Refusal.Should().Contain(folder).And.Contain(whose);
    }

    [Fact]
    public void A_folder_that_does_not_exist_is_refused()
    {
        Judge("/home/me/.nothing").Refusal.Should().Contain("does not exist");
    }

    /// <summary>Review C1 / R2.1: a folder on another filesystem than the home (a mount, /mnt/c over 9p) is refused.</summary>
    [Fact]
    public void A_folder_on_another_filesystem_than_the_home_is_refused()
    {
        Folder("/home/me/mounted");
        var mounted = Path.GetFullPath(_sandbox.Paths.DistroPath("/home/me/mounted"));
        var files = new DeviceFiles(_sandbox.Files, path => Path.GetFullPath(path) == mounted ? (0u, 159u) : (8u, 48u));

        Judge("/home/me/mounted", files).Refusal.Should().Contain("another filesystem");
    }

    /// <summary>The REAL path decides: a link inside the home that points out of it is refused (Linux: a link needs no privilege).</summary>
    [Fact]
    public void A_link_inside_the_home_pointing_out_of_it_is_judged_by_where_it_points()
    {
        var outside = _sandbox.Paths.DistroPath("/data/elsewhere");
        Directory.CreateDirectory(outside);
        Folder("/home/me");
        if (!DirectoryLinks.TryCreate(_sandbox.Paths.DistroPath("/home/me/.linked"), outside))
        {
            Assert.Skip("this account can create neither a symbolic link nor a junction");
        }

        Judge("/home/me/.linked").Refusal.Should().Contain("is not inside the home");
    }

    [Fact]
    public void A_windows_entry_is_left_to_the_windows_binary()
    {
        var agent = new ExtraAgent("C:\\x.exe", ExtraAgentShape.Windows, "x", ["C:\\Users\\me\\.x"], string.Empty);

        ExtraAgentRules.Judge(_sandbox.Paths, _sandbox.Files, [agent], []).Single().Refusal.Should().Be(ExtraAgentRules.WindowsEntry);
    }

    private sealed class DeviceFiles(IFileSystem inner, Func<string, (uint, uint)?> device) : DelegatingFileSystem(inner)
    {
        public override (uint Major, uint Minor)? DeviceOf(string path) => device(path);
    }
}
