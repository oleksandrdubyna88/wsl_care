using System.Text.RegularExpressions;

using FluentAssertions;

namespace WslCare.Core.Tests;

/// <summary>Plan §15q R1.3 (review B1): what decides what may run or be deleted reads no configuration — the never-list, the
/// command policy and its catalogue, the deletion policy and the protected roots. A setting can steer WHEN a declared command
/// runs and with which bounded number, never WHAT is allowed.</summary>
public sealed partial class ArchitectureTests
{
    [GeneratedRegex(@"\b(?:EffectiveConfig|ConfigKeys|ConfigLoadResult|ConfigLoader)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ConfigurationReference();

    private static readonly string[] PolicyFiles =
    [
        Path.Combine("WslCare.Core", "Processes", "Policy", "NeverList.cs"),
        Path.Combine("WslCare.Core", "Processes", "Policy", "CommandPolicy.cs"),
        Path.Combine("WslCare.Core", "Processes", "Policy", "CommandCatalogue.cs"),
        Path.Combine("WslCare.Core", "Files", "Deletion", "DeletionPolicy.cs"),
        Path.Combine("WslCare.Core", "Files", "Deletion", "ProtectedRoots.cs"),
    ];

    [Fact]
    public void No_policy_or_protected_roots_type_reads_the_configuration()
    {
        var root = Metadata("WslCare.SourceRoot");
        var offenders = PolicyFiles
            .Select(relative => Path.Combine(root, relative))
            .SelectMany(file => ConfigurationReference().Matches(File.ReadAllText(file)).Select(m => $"{file}:{LineOf(File.ReadAllText(file), m.Index)}: {m.Value}"))
            .ToList();

        PolicyFiles.Should().OnlyContain(relative => File.Exists(Path.Combine(root, relative)), "the scan reads the real policy files");
        offenders.Should().BeEmpty("a configuration value may never decide what is allowed to run or be deleted (plan §15q R1.3)");
    }

    /// <summary>The companion: the pattern still finds a configuration read where one legitimately is.</summary>
    [Fact]
    public void The_configuration_scan_still_finds_an_action_reading_its_setting()
    {
        var journal = Path.Combine(Metadata("WslCare.SourceRoot"), "WslCare.Core", "Actions", "JournalVacuum.cs");

        ConfigurationReference().IsMatch(File.ReadAllText(journal)).Should().BeTrue();
    }
}
