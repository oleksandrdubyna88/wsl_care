using System.Globalization;
using System.Reflection;
using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Cli.Commands;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Status;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>
/// Plan §15s D7 (E10.S0): <c>act … --entry -</c> reads A20's entry ids from STDIN, one per line, under the checks the flag has —
/// 16 lowercase hex, none twice, at most <c>archive.maxRestoreEntries</c>' ceiling, A20 among the actions — because a Windows command
/// line holds 32 767 characters and a month of sessions does not fit as <c>--entry &lt;id&gt;</c> words. The extension sends exactly
/// the request in <c>contracts/requests/act-a20-entry-stdin.json</c> (the plan round's finding 0): these tests parse exactly those
/// bytes, so the two halves cannot drift apart.
/// </summary>
public sealed class ActEntryStdinTests : IDisposable
{
    private readonly DetachedRunHarness _harness = new("entry-stdin");

    public void Dispose() => _harness.Dispose();

    private static string Ids(int count) => string.Concat(Enumerable.Range(1, count).Select(i => i.ToString("x16", CultureInfo.InvariantCulture) + "\n"));

    /// <summary>The shared request fixture, read where it lives (stamped by the build, never copied).</summary>
    private static (IReadOnlyList<string> Entries, string Stdin, string[] Preview, string[] Confirm) Fixture()
    {
        var directory = typeof(ActEntryStdinTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "WslCare.ContractsDirectory").Value!;
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "requests", "act-a20-entry-stdin.json")));
        string[] Words(string name) => [.. json.RootElement.GetProperty(name).EnumerateArray().Select(e => e.GetString()!)];
        return (Words("entries"), json.RootElement.GetProperty("stdin").GetString()!, Words("preview"), Words("confirm"));
    }

    // ---------- the parse ----------

    [Fact]
    public void The_entry_flag_takes_a_dash_for_stdin_and_names_no_id_on_the_command_line()
    {
        var act = CommandLine.Parse(["act", "A20", "--confirm", "--manual", "--detach", "--entry", "-", "--json"]).Should().BeOfType<Request.Act>().Subject;

        act.EntriesOnStdin.Should().BeTrue();
        act.Entries.Should().BeEmpty("the ids arrive on stdin, never as the word -");
    }

    [Theory]
    [InlineData("A13", "--entry", "-", "A20 among the actions")]
    [InlineData("A20", "--entry", "-", "--entry", "0123456789abcdef", "once")]
    [InlineData("A20", "--entry", "-", "--entry", "-", "once")]
    [InlineData("A4,A20", "--only", "-", "--entry", "-", "stdin")]
    public void A_stdin_entry_list_is_A20s_alone_given_once_and_never_beside_another_stdin_list(params string[] words)
    {
        var refusal = CommandLine.Parse(["act", words[0], "--confirm", "--manual", .. words[1..^1]]).Should().BeOfType<Request.Failed>().Subject;

        refusal.Message.Should().Contain(words[^1]);
    }

    // ---------- the shared request (both halves) ----------

    [Fact]
    public void The_shared_confirm_request_reaches_the_detached_run_with_exactly_its_entries()
    {
        var (entries, stdin, _, confirm) = Fixture();

        var (exit, _, stderr) = CliRun.Over(_harness.Host(stdin: stdin), confirm);

        exit.Should().Be((int)ExitCode.Ok, stderr);
        _harness.Requests().Should().ContainSingle().Which.Should().BeOfType<RunRequestRead.Parsed>().Which.File.ShownEntries.Should().Equal(entries);
    }

    [Fact]
    public void The_shared_preview_request_parses_and_reads_exactly_its_entries()
    {
        var (entries, stdin, preview, _) = Fixture();

        var act = CommandLine.Parse(preview).Should().BeOfType<Request.Act>().Subject;
        var read = ActCommand.EntriesOf(act, _harness.Host(stdin: stdin));

        read.Failure.Should().BeEmpty();
        read.List.Names.Order(StringComparer.Ordinal).Should().Equal(entries);
    }

    // ---------- the checks the flag has ----------

    [Theory]
    [InlineData("0123456789abcdef\nNOT-AN-ID secret\n", "line 2")]
    [InlineData("0123456789abcdef\n0123456789abcdef\n", "twice")]
    [InlineData("\n\n", "no entry")]
    public void A_bad_stdin_entry_list_is_refused_naming_the_rule_never_echoing_a_line(string stdin, string reason)
    {
        var (exit, stdout, stderr) = CliRun.Over(_harness.Host(stdin: stdin), "act", "A20", "--confirm", "--manual", "--detach", "--entry", "-");

        exit.Should().Be((int)ExitCode.Usage);
        stdout.Should().BeEmpty();
        stderr.Should().Contain(reason).And.NotContain("secret");
        _harness.Requests().Should().BeEmpty();
        _harness.Runner.Requests.Should().BeEmpty();
    }

    [Fact]
    public void A_stdin_entry_list_is_bounded_by_the_ceiling_any_restore_takes()
    {
        var most = Core.Config.ConfigKeys.Archive.MaxRestoreEntries.Max;
        _harness.MachineLayer($$"""{ "archive": { "maxRestoreEntries": {{most}} } }""");

        var (atMost, _, atMostErr) = CliRun.Over(_harness.Host(stdin: Ids(most)), "act", "A20", "--confirm", "--manual", "--detach", "--entry", "-");
        var (past, _, pastErr) = CliRun.Over(_harness.Host(stdin: Ids(most + 1)), "act", "A20", "--confirm", "--manual", "--detach", "--entry", "-");

        atMost.Should().Be((int)ExitCode.Ok, atMostErr);
        past.Should().Be((int)ExitCode.Usage);
        pastErr.Should().Contain(most.ToString(CultureInfo.InvariantCulture)).And.Contain("archive.maxRestoreEntries");
    }

    /// <summary>The own review, finding 2: the verb holds the ceiling IN FORCE (the parser can only hold the key's range), as it holds
    /// <c>act.maxShownNames</c> — more entries than one restore takes are refused before anything is written.</summary>
    [Fact]
    public void More_entries_than_the_ceiling_in_force_are_refused_before_anything_is_written()
    {
        _harness.MachineLayer("""{ "archive": { "maxRestoreEntries": 2 } }""");

        var (exit, _, stderr) = CliRun.Over(_harness.Host(stdin: Ids(3)), "act", "A20", "--confirm", "--manual", "--detach", "--entry", "-");

        exit.Should().Be((int)ExitCode.Usage);
        stderr.Should().Contain("archive.maxRestoreEntries").And.Contain("at most 2");
        _harness.Requests().Should().BeEmpty();
    }

    /// <summary>The own review, finding 3: the entries are a shown list of the request file — the cap in force on shown lists
    /// (<c>act.maxShownNames</c>) holds them at the command line, as it holds A4's and A18's, so a detach is never <c>accepted</c>
    /// for a request its unit would refuse to read.</summary>
    [Fact]
    public void Entries_past_the_shown_list_cap_in_force_are_refused_before_a_request_is_written()
    {
        _harness.MachineLayer("""{ "act": { "maxShownNames": 2 } }""");

        var (exit, stdout, stderr) = CliRun.Over(_harness.Host(stdin: Ids(3)), "act", "A20", "--confirm", "--manual", "--detach", "--entry", "-", "--json");

        exit.Should().Be((int)ExitCode.Usage, stdout);
        stderr.Should().Contain("act.maxShownNames");
        _harness.Requests().Should().BeEmpty();
    }

    /// <summary>The own review, finding 8a: a refusal of the whole request comes BEFORE stdin is touched — a process that is not root
    /// never reads the list.</summary>
    [Fact]
    public void A_refused_request_never_reads_stdin()
    {
        var host = _harness.Host(DetachedRunHarness.NotRoot) with { StandardInput = () => throw new InvalidOperationException("stdin was read") };

        var (exit, _, stderr) = CliRun.Over(host, "act", "A20", "--confirm", "--manual", "--detach", "--entry", "-");

        exit.Should().Be((int)ExitCode.NeedsRoot, stderr);
    }

    /// <summary>The own review, finding 8b: the in-process path reads stdin through the same lists as the detached one.</summary>
    [Fact]
    public void An_in_process_preview_reads_its_entries_from_stdin_under_the_same_checks()
    {
        var (exit, stdout, stderr) = CliRun.Over(_harness.Host(stdin: "NOT-AN-ID\n"), "act", "A20", "--preview", "--entry", "-", "--json");

        exit.Should().Be((int)ExitCode.Usage);
        stdout.Should().BeEmpty();
        stderr.Should().Contain("line 1").And.NotContain("NOT-AN-ID");
    }

    /// <summary>The own review, findings 8c and 8d: ids before the dash are refused as after it; stdin past the byte cap, or with no
    /// end, is refused for this list as for <c>--only -</c>.</summary>
    [Fact]
    public void Ids_before_the_dash_a_list_past_the_byte_cap_and_a_list_with_no_end_are_refused()
    {
        CommandLine.Parse(["act", "A20", "--confirm", "--entry", "0123456789abcdef", "--entry", "-"]).Should().BeOfType<Request.Failed>().Which.Message.Should().Contain("once");

        var (pastExit, _, pastErr) = CliRun.Over(_harness.Host(stdin: new string('a', 1024 * 1024 + 1)), "act", "A20", "--confirm", "--manual", "--detach", "--entry", "-");
        using var never = new NeverEndingStream();
        var stalled = _harness.Host() with { StandardInput = () => never, StdinCeiling = TimeSpan.FromMilliseconds(300) };
        var (neverExit, _, neverErr) = CliRun.Over(stalled, "act", "A20", "--confirm", "--manual", "--detach", "--entry", "-");

        pastExit.Should().Be((int)ExitCode.Usage);
        pastErr.Should().Contain("larger than 1048576 bytes");
        neverExit.Should().Be((int)ExitCode.Usage);
        neverErr.Should().Contain("had no end within 0.3 s");
        _harness.Requests().Should().BeEmpty();
    }

    /// <summary>The own review, finding 1: <c>archive list --restorable --entry &lt;ids&gt;</c> — what A20's preview asks its child for:
    /// the shown entries only, so none is lost to the newest window.</summary>
    [Fact]
    public void Archive_list_takes_the_entries_a_restorable_list_is_asked_for()
    {
        CommandLine.Parse(["archive", "list", "--restorable", "--entry", "0123456789abcdef,00000000000000aa", "--json"]).Should().BeOfType<Request.ArchiveList>()
            .Which.EntryIds.Should().Equal("0123456789abcdef", "00000000000000aa");
        CommandLine.Parse(["archive", "list", "--entry", "0123456789abcdef", "--json"]).Should().BeOfType<Request.Failed>().Which.Message.Should().Contain("--restorable");
        CommandLine.Parse(["archive", "list", "--restorable", "--entry", "NOT-HEX", "--json"]).Should().BeOfType<Request.Failed>();
        CommandLine.Parse(["archive", "list", "--restorable", "--entry", "0123456789abcdef,0123456789abcdef", "--json"]).Should().BeOfType<Request.Failed>().Which.Message.Should().Contain("twice");
    }

    [Fact]
    public void A_trailing_carriage_return_is_tolerated_as_the_only_file_tolerates_it()
    {
        var act = CommandLine.Parse(["act", "A20", "--preview", "--entry", "-"]).Should().BeOfType<Request.Act>().Subject;

        ActCommand.EntriesOf(act, _harness.Host(stdin: "0123456789abcdef\r\n00000000000000aa\r\n")).List.Names.Should().BeEquivalentTo(["0123456789abcdef", "00000000000000aa"]);
    }

    [Fact]
    public void The_capability_names_the_stdin_entry_list()
    {
        Capabilities.All.Should().Contain("act.entryStdin");
        Capabilities.All[^1].Should().Be(Capabilities.ActEntryStdin, "a capability is appended in the story that delivers it");
    }

    // ---------- D10: archive check-base, either order ----------

    [Fact]
    public void Check_base_takes_json_before_or_after_its_path()
    {
        CommandLine.Parse(["archive", "check-base", "--json", "/mnt/v/ai-archive"]).Should().BeOfType<Request.ArchiveCheckBase>()
            .Which.Should().Be(new Request.ArchiveCheckBase("/mnt/v/ai-archive", true));
        CommandLine.Parse(["archive", "check-base", "/mnt/v/ai-archive", "--json"]).Should().BeOfType<Request.ArchiveCheckBase>()
            .Which.Should().Be(new Request.ArchiveCheckBase("/mnt/v/ai-archive", true));
        CommandLine.Parse(["archive", "check-base", "--json", "--json"]).Should().BeOfType<Request.Failed>();
    }
}
