using System.Text.Json;

using FluentAssertions;

namespace WslCare.Scenarios;

/// <summary>
/// The bundled-daemon pin lives in three places: the extension's <c>BUNDLED_DAEMON</c> and <c>min-daemon.json</c>
/// (held equal by the extension's <c>minDaemon.test</c>) and this repository's <see cref="WindowsTasksGolden.Release"/>, the
/// fixed release the render golden carries (the extension refuses a render whose version is not <c>BUNDLED_DAEMON</c>). This
/// test closes the triangle on the daemon's side (code round of the 0.6.0 bundle, 2026-10-22): a bump of
/// <c>bundledDaemon</c> that forgets <see cref="WindowsTasksGolden.Release"/> fails HERE, naming the file to change, instead
/// of as a version mismatch inside the extension's fixtures.
/// </summary>
public sealed class WindowsTasksGoldenPinTests
{
    [Fact]
    public void The_render_golden_pins_the_release_the_extension_bundles()
    {
        var path = Path.Combine(ReleaseFiles.Root, "src_vs_code", "min-daemon.json");
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var bundled = json.RootElement.GetProperty("bundledDaemon").GetString();

        WindowsTasksGolden.Release.Should().Be(bundled,
            "src_vs_code/min-daemon.json bundles {0}: move WindowsTasksGolden.Release with it and regenerate contracts/golden/windows-tasks-render.json (WSL_CARE_WRITE_GOLDENS=1)",
            bundled);
    }
}
