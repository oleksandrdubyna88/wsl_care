using FluentAssertions;

using WslCare.Core.Archive;
using WslCare.Core.Files;

namespace WslCare.Core.Tests.Archive;

/// <summary>
/// Plan §15r *Test plan* — the 14 kill points, each with the agent doing nothing, appending, writing a new file at the original name,
/// or deleting, AT that point. The run is stopped there (in process: the fault seam throws — the BUILT child killed at the same
/// points is the scenario suite's); a new run reconciles and finishes. After each: nothing the agent ever wrote is lost — every
/// last content the agent gave the session's main file is at the source or in an archived copy the index names — no file is left under a
/// quarantine name, and the reconcile run twice changes nothing the second time.
/// </summary>
public sealed partial class ArchiveProtocolTests
{
    public static TheoryData<string, string> KillPoints()
    {
        var points = new[]
        {
            MoveSteps.Intent, MoveSteps.CopyChunk, MoveSteps.FinalFlushed, nameof(ArchiveFileStep.ExclusiveCreated), MoveSteps.BetweenFiles,
            nameof(ArchiveFileStep.Appended), MoveSteps.IndexFlushed, MoveSteps.QuarantineStart, nameof(ArchiveFileStep.Renamed),
            nameof(ArchiveFileStep.RemovalHashed), nameof(ArchiveFileStep.Removed), MoveSteps.FoldersStart, MoveSteps.CloseStart, MoveSteps.Closed,
        };
        var data = new TheoryData<string, string>();
        foreach (var point in points)
        {
            foreach (var agent in new[] { "nothing", "append", "new-file", "delete" })
            {
                data.Add(point, agent);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(KillPoints))]
    public void A_crash_at_each_of_the_fourteen_points_leaves_the_session_whole_and_the_reconcile_finishes_it(string point, string agentDoes)
    {
        var unit = Session("k1", main: "the transcript");
        var contents = new List<string> { "the transcript" };
        var crashed = false;
        _fault = step =>
        {
            if (step == point && !crashed)
            {
                crashed = true;
                AgentActs(agentDoes, contents);
                throw new OperationCanceledException($"killed at {point}");
            }
        };

        RunToTheEnd(unit, crash: true);
        _fault = static _ => { };
        crashed.Should().BeTrue($"the point {point} is reached by the protocol");

        Restarted(unit);
        var again = Run("r9");
        var second = ArchiveReconcile.FromInflight(again);

        second.Should().BeEquivalentTo(ArchiveReconcileReport.Empty with { Notes = second.Notes }, "the reconcile run twice changes nothing the second time");
        Directory.EnumerateFiles(On(Layout), "*" + ArchiveNames.QuarantineMark + "*", SearchOption.AllDirectories).Should().BeEmpty("no file is left aside");
        if (contents.Count > 0)
        {
            var latest = contents[^1];
            Kept(latest).Should().BeTrue($"\"{latest}\", the last the agent wrote, must be at the source or in an indexed archived copy");
        }
    }

    /// <summary>Phase 1, then phase 2 a day later — stopped wherever the fault seam throws.</summary>
    private void RunToTheEnd(UnitFound unit, bool crash)
    {
        try
        {
            if (ArchiveCopy.Copy(Run("r1"), Agent, On(Layout), unit) is CopyOutcome.Archived archived)
            {
                RemoveLater(archived.Entry);
            }
        }
        catch (OperationCanceledException) when (crash)
        {
        }
    }

    /// <summary>A new run: the reconcile (in-flight file, then the quarantine names), then whatever is left of the protocol.</summary>
    private void Restarted(UnitFound unit)
    {
        _clock.Advance(TimeSpan.FromHours(25));
        var c = Run("r3");
        _ = ArchiveReconcile.FromInflight(c);
        var aside = Directory.EnumerateFiles(On(Layout), "*" + ArchiveNames.QuarantineMark + "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(On(Layout), p).Replace('\\', '/')).ToList();
        _ = ArchiveReconcile.RenameBack(c, Agent, On(Layout), aside, ArchiveReconcileReport.Empty);
        var waiting = c.Book.Entries.Where(e => e.State == InflightStates.Archived).ToList();
        foreach (var entry in waiting)
        {
            var indexed = Index(c).Single(e => e.EntryId == entry.EntryId);
            _ = ArchiveRemove.Remove(c, entry, indexed);
        }

        if (waiting.Count == 0 && File.Exists(Source(unit.Key)) && !Index(c).Any(e => e.Key == unit.Key && e.Status == ArchiveIndex.Events.SourceRemoved))
        {
            var fresh = unit with { Files = [.. unit.Files.Where(f => File.Exists(Source(f.Relative)))] };
            if (fresh.Files.Count > 0 && ArchiveCopy.Copy(c, Agent, On(Layout), fresh) is CopyOutcome.Archived archived)
            {
                RemoveLater(archived.Entry, "r4");
            }
        }
    }

    /// <summary>What the agent does at the kill point to the session's main file (recording every content it ever held).</summary>
    private void AgentActs(string agentDoes, List<string> contents)
    {
        var main = Source("projects/p/k1.jsonl");
        switch (agentDoes)
        {
            case "append" when File.Exists(main):
                File.AppendAllText(main, " — and the agent went on");
                contents.Add(File.ReadAllText(main));
                break;
            case "new-file" when !File.Exists(main):
                File.WriteAllText(main, "a new session at the old name");
                contents.Add("a new session at the old name");
                break;
            case "delete" when File.Exists(main):
                File.Delete(main);
                contents.Clear(); // the agent itself removed it — what it held is the agent's to lose
                break;
        }
    }

    /// <summary>Whether <paramref name="content"/> is at the source (any name) or in an archived copy an index line of this side names.</summary>
    private bool Kept(string content)
    {
        var atSource = Directory.EnumerateFiles(On(Layout), "*", SearchOption.AllDirectories).Any(f => File.ReadAllText(f) == content);
        var sha = Sha(content);
        var indexed = Index(Run("r9")).SelectMany(e => e.Files).Where(f => f.Sha256 == sha)
            .Any(f => File.Exists(Archived(f.Archived)) && Sha(File.ReadAllText(Archived(f.Archived))) == sha);
        return atSource || indexed;
    }
}
