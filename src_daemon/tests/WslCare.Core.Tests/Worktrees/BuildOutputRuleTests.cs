using FluentAssertions;

using WslCare.Core.Files;
using WslCare.Core.Worktrees;

namespace WslCare.Core.Tests.Worktrees;

/// <summary>
/// E19.S3 (PLAN_disk_hygiene_windows.md §3.3 "Build output", E19 stories §3 decision 11, and the M6 census of 321 real worktrees): a
/// <c>bin</c>/<c>obj</c> is build output only when EVERY file in it is a build artifact — by its extension, never by a glob — and none was
/// written more than <c>worktrees.artifactSkewMinutes</c> after the build's newest compiled output; a name is no proof of regenerability.
/// <c>node_modules</c> is exempt. Pure over a list of files.
/// </summary>
public sealed class BuildOutputRuleTests
{
    private static readonly DateTimeOffset Built = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    private const string Bin = @"C:\w\wt\bin";

    private static readonly WorktreeSettings Settings = WorktreeWorld.DefaultSettings();

    private static TreeFile F(string relative, int minutesAfterBuild = 0, string root = Bin) =>
        new($@"{root}\{relative}", 100, Built.AddMinutes(minutesAfterBuild));

    private static TreeFile[] Build() => [F(@"Debug\net10.0\App.dll"), F(@"Debug\net10.0\App.pdb"), F(@"Debug\net10.0\App.deps.json"), F(@"Debug\net10.0\App.runtimeconfig.json")];

    private static string Retention(string name, params TreeFile[] files) =>
        BuildOutputRule.Retention(name, name == "obj" ? @"C:\w\wt\obj" : Bin, files, Settings);

    [Fact]
    public void A_bin_of_only_build_artifacts_is_a_candidate()
    {
        Retention("bin", Build()).Should().BeEmpty();
    }

    [Fact]
    public void An_empty_bin_is_a_candidate_with_nothing_to_keep()
    {
        Retention("bin").Should().BeEmpty();
    }

    [Fact]
    public void A_dll_named_like_sqlite_is_an_artifact()
    {
        Retention("bin", [.. Build(), F(@"Debug\net10.0\Microsoft.Data.Sqlite.dll"), F(@"Debug\net10.0\SQLitePCLRaw.core.dll"), F(@"Debug\net10.0\e_sqlite3.pdb")])
            .Should().BeEmpty("the never-artifact names are EXTENSIONS, not globs: *.sqlite* matched Microsoft.Data.Sqlite.dll in 17 real bins (M6)");
    }

    [Fact]
    public void A_bin_holding_a_sqlite_file_is_retained_and_names_it()
    {
        var reason = Retention("bin", [.. Build(), F(@"Debug\net10.0\local.db")]);

        reason.Should().Contain("local.db").And.Contain("not a build artifact");
    }

    [Theory]
    [InlineData("data.db")]
    [InlineData("data.sqlite")]
    [InlineData("data.sqlite3")]
    [InlineData("data.db-wal")]
    [InlineData("data.db-shm")]
    [InlineData("data.mdf")]
    [InlineData("data.ldf")]
    [InlineData("cert.pfx")]
    [InlineData("signing.key")]
    [InlineData("cert.pem")]
    [InlineData(".env")]
    [InlineData(".env.local")]
    [InlineData("DATA.DB")]
    [InlineData(".ENV")]
    public void A_database_a_key_or_an_env_file_is_never_an_artifact_whatever_its_folder_or_time(string name)
    {
        var reason = Retention("bin", [.. Build(), F($@"Debug\net10.0\{name}")]);

        reason.Should().Contain(name, "the file is named");
    }

    [Theory]
    [InlineData("obj")]
    [InlineData("bin")]
    public void A_never_artifact_retains_in_both_bin_and_obj(string folder)
    {
        var root = $@"C:\w\wt\{folder}";

        BuildOutputRule.Retention(folder, root, [F("x.dll", root: root), F("Debug\\.env.production", root: root)], Settings).Should().Contain(".env.production");
    }

    [Theory]
    [InlineData("createdump")]
    [InlineData("App.exe")]
    [InlineData("App.dll.config.xml")]
    [InlineData("a.nupkg")]
    [InlineData("x.up2date")]
    [InlineData("x.sourcelink")]
    [InlineData("x.sn")]
    public void A_listed_extension_or_an_extensionless_runtime_file_is_an_artifact(string name)
    {
        Retention("bin", [.. Build(), F($@"Debug\net10.0\{name}")]).Should().BeEmpty($"{name} is what a build writes (M6)");
    }

    [Theory]
    [InlineData("run.log")]
    [InlineData("results.out")]
    [InlineData("notes.md")]
    [InlineData("seed.sql")]
    [InlineData("events.jsonl")]
    [InlineData("x.ndjson")]
    [InlineData("blob.dat")]
    [InlineData("x.golden")]
    [InlineData("x.diag")]
    [InlineData("x.current")]
    [InlineData("x.stat")]
    [InlineData("x.procs")]
    [InlineData("x.unheard-of")]
    public void An_extension_the_measurement_did_not_find_in_a_build_retains_the_candidate_naming_the_file(string name)
    {
        var reason = Retention("bin", [.. Build(), F($@"Debug\net10.0\{name}")]);

        reason.Should().Contain(name).And.Contain("not a build artifact");
    }

    [Fact]
    public void A_generated_source_counts_only_under_obj()
    {
        BuildOutputRule.Retention("obj", @"C:\w\wt\obj", [F("Debug\\net10.0\\App.dll", root: @"C:\w\wt\obj"), F("Debug\\net10.0\\App.AssemblyInfo.cs", root: @"C:\w\wt\obj")], Settings).Should().BeEmpty();
        Retention("bin", [.. Build(), F(@"Debug\net10.0\Hand.cs")]).Should().Contain("Hand.cs");
    }

    [Fact]
    public void A_json_written_after_the_build_retains_its_bin()
    {
        var reason = Retention("bin", [.. Build(), F(@"Debug\net10.0\local-state.json", minutesAfterBuild: 11)]);

        reason.Should().Contain("local-state.json").And.Contain("after the build");
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(9, false)]
    [InlineData(10, false)]
    [InlineData(11, true)]
    [InlineData(600, true)]
    public void The_skew_is_more_than_the_configured_minutes_after_the_newest_compiled_output(int minutes, bool retained)
    {
        var reason = Retention("bin", [.. Build(), F(@"Debug\net10.0\settings.json", minutes)]);

        (reason.Length > 0).Should().Be(retained, reason);
    }

    [Fact]
    public void The_skew_follows_the_setting()
    {
        var tight = Settings with { ArtifactSkewMinutes = 1 };

        BuildOutputRule.Retention("bin", Bin, [.. Build(), F(@"Debug\net10.0\settings.json", 2)], tight).Should().Contain("settings.json");
    }

    [Fact]
    public void A_folder_with_no_compiled_output_is_dated_by_the_candidates_newest()
    {
        var files = new[] { F(@"Debug\net10.0\App.dll"), F(@"Debug\net10.0\ref\only.json", 5), F(@"Debug\net10.0\ref\late.json", 30) };

        Retention("bin", files).Should().Contain("late.json", "a file in a folder with no dll is dated against the newest compiled output of the whole candidate");
    }

    [Fact]
    public void A_candidate_with_no_compiled_output_at_all_cannot_date_its_files_and_is_retained()
    {
        var reason = Retention("bin", F("project.assets.json"), F("project.nuget.cache"));

        reason.Should().Contain("compiled output");
    }

    [Fact]
    public void Names_and_extensions_are_compared_without_case()
    {
        Retention("bin", F(@"Debug\APP.DLL"), F(@"Debug\App.PDB")).Should().BeEmpty();
    }

    [Fact]
    public void Node_modules_is_exempt_from_the_content_check()
    {
        BuildOutputRule.Retention("node_modules", @"C:\w\wt\node_modules", [F("left\\x.db", root: @"C:\w\wt\node_modules"), F("left\\readme.md", root: @"C:\w\wt\node_modules")], Settings)
            .Should().BeEmpty("its contents are packages package.json and the lockfile restore");
    }

    [Fact]
    public void The_first_offender_in_name_order_is_the_one_named()
    {
        var reason = Retention("bin", [.. Build(), F(@"Debug\z.db"), F(@"Debug\a.log")]);

        reason.Should().Contain("a.log").And.NotContain("z.db");
    }

    [Fact]
    public void The_artifact_list_follows_the_setting()
    {
        var narrow = Settings with { ArtifactPatterns = [".dll"] };

        BuildOutputRule.Retention("bin", Bin, [F("App.dll"), F("App.pdb")], narrow).Should().Contain("App.pdb");
    }
}
