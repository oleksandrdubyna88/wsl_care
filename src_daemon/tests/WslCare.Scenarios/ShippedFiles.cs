namespace WslCare.Scenarios;

/// <summary>Where the files a release ships live in this repository (E4.S1): the installer, the units, the machine
/// layer — paths the build stamped, read where they are so every test exercises what a release packs.</summary>
internal static class ShippedFiles
{
    public static string InstallScript => ScenarioHome.Stamped("WslCare.InstallScript");

    public static string SystemdDirectory => ScenarioHome.Stamped("WslCare.SystemdDirectory");

    public static string MachineConfig => ScenarioHome.Stamped("WslCare.MachineConfig");

    public static string RepositoryRoot => ScenarioHome.Stamped("WslCare.RepositoryRoot");

    public static IReadOnlyList<string> UnitNames => ["wsl-care.service", "wsl-care.timer", "wsl-care-events.service", "wsl-care-act@.service", "wsl-care-watch.service", "wsl-care-watch.timer"];
}
