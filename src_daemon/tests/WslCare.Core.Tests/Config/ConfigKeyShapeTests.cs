using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Processes.Policy;

namespace WslCare.Core.Tests.Config;

/// <summary>
/// Plan §15q R1.3 (review B1), restated as the register's own invariants: every text or list key is closed or a declared shape;
/// a path key is machine-only until its reader validates the filesystem; free-shaped text exists only where no daemon code
/// reads it; and every number a key puts into a command template is bounded by that key's range, exactly.
/// </summary>
public sealed class ConfigKeyShapeTests
{
    [Fact]
    public void Every_list_key_is_closed_and_never_offers_the_catch_all_family()
    {
        var lists = ConfigKeys.All.OfType<ConfigKey.TextListKey>().ToList();

        lists.Should().NotBeEmpty("processes.families is a list key");
        lists.Should().OnlyContain(k => k.Allowed.Count > 0);
        ConfigKeys.Processes.Families.Allowed.Should().NotContain(ProcessFamilies.Other).And.NotContain(ProcessFamilies.AiAgents)
            .And.BeSubsetOf(ProcessFamilies.Catalogue.Select(f => f.Name));
    }

    [Fact]
    public void A_path_key_is_machine_only_and_a_pattern_key_is_read_by_no_daemon_code()
    {
        var texts = ConfigKeys.All.OfType<ConfigKey.TextKey>().ToList();

        texts.Where(k => k.Rule is TextRule.AbsolutePathOrEmpty).Should().OnlyContain(k => k.Trust.MachineOnly)
            .And.Contain(ConfigKeys.Archive.BaseFolder);
        texts.Where(k => k.Rule is TextRule.HttpsUrlOrEmpty).Should().OnlyContain(k => k.Trust.MachineOnly, "root sends the request (PLAN_windows_time_guard.md D2)")
            .And.Contain(ConfigKeys.Clock.ReferenceUrl);
        texts.Where(k => k.Rule is TextRule.Matching).Should().OnlyContain(k => k.Trust.DaemonUnused)
            .And.Contain(ConfigKeys.Distro);
    }

    /// <summary>Every number slot of every template the product policy holds is either filled from ONE key — and then accepts
    /// exactly that key's range (times the unit the action converts to) — or is named here as not a configuration value. A new
    /// slot that is neither fails, naming it.</summary>
    [Fact]
    public void Every_number_slot_a_key_fills_accepts_exactly_that_key_s_range()
    {
        var fromKeys = new Dictionary<(string Template, string Slot), (ConfigKey.IntKey Key, long Factor)>
        {
            [("journalctl-vacuum-time", "keep")] = (ConfigKeys.Journal.KeepDays, 1),
            [("curl-head-date", "seconds")] = (ConfigKeys.Clock.ReferenceTimeoutSeconds, 1),
            [("docker-image-prune-unused", "until")] = (ConfigKeys.Images.UnusedOlderThanDays, 24),
        };
        var notConfig = new HashSet<(string, string)>
        {
            // A snap's revision comes from snap list, never from the configuration.
            ("snap-remove-revision", "revision"),
        };

        var slots = NumberSlots().ToList();
        var caps = slots.Where(s => s.Slot == "cap").ToList();
        caps.Should().NotBeEmpty("A7's size-capped prune templates carry the cap slot");
        foreach (var cap in caps)
        {
            fromKeys[(cap.Template, cap.Slot)] = (ConfigKeys.BuildCache.MaxGb, 1);
        }

        slots.Where(s => !fromKeys.ContainsKey((s.Template, s.Slot)) && !notConfig.Contains((s.Template, s.Slot)))
            .Select(s => $"{s.Template}:{s.Slot}").Should().BeEmpty("every number slot is mapped to the key that fills it, or declared not configuration");
        foreach (var slot in slots.Where(s => fromKeys.ContainsKey((s.Template, s.Slot))))
        {
            var (key, factor) = fromKeys[(slot.Template, slot.Slot)];
            (slot.Number.Min, slot.Number.Max).Should().Be((key.Min * factor, key.Max * factor), $"{slot.Template}:{slot.Slot} is filled from {key.Name}");
        }
    }

    private static IEnumerable<(string Template, string Slot, SlotKind.Number Number)> NumberSlots() =>
        CommandCatalogue.Product.Templates.SelectMany(t => t.Parts.SelectMany(part => part switch
        {
            ArgPart.Slot slot when Number(slot.Kind) is { } n => [(t.Name, slot.Name, n)],
            ArgPart.Repeat repeat when Number(repeat.Kind) is { } n => new[] { (t.Name, repeat.Name, n) },
            _ => [],
        }));

    private static SlotKind.Number? Number(SlotKind kind) => kind switch
    {
        SlotKind.Number number => number,
        SlotKind.Prefixed prefixed => Number(prefixed.Inner),
        _ => null,
    };
}
