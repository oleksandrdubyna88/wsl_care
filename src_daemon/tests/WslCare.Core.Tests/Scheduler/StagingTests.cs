using System.Security.Cryptography;
using System.Text;

using FluentAssertions;

using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Hosting;
using WslCare.Core.Scheduler;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Scheduler;

/// <summary>
/// WR-D2 (PLAN_windows_runner.md §3.2, §3.10, §4, §6): <c>tasks stage</c> copies this process's own exe to
/// <c>&lt;stagingRoot&gt;\&lt;digest&gt;\wsl-care.exe</c> with an exclusive create, reads it back, writes <c>stage.json</c> only when the
/// read-back equals the digest, then sweeps the OTHER digest folders; <c>tasks stage --remove</c> removes every digest folder and the
/// root when empty. A folder whose exe is "executing" is, here, one whose exe the TEST holds open without sharing delete — so the
/// delete fails exactly as it does for a running image — never a real running copy of the product. Everything is under a TempRoot.
/// </summary>
public sealed class StagingTests
{
    private const int Cap = 65536;

    private const string OwnVersion = "0.4.0+7aeb02dd39a9841915112456532d3803e4cb6026";

    private const string Release = "0.4.0";

    private const string WindowsOnly = "a staged copy is a Windows file made and removed through the Windows seam (the distro's binary stages nothing)";

    private static readonly byte[] ExeBytes = Encoding.UTF8.GetBytes("not a real executable: the bytes tasks stage copies");

    private static readonly string ExeDigest = Sha(ExeBytes);

    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void A_stage_copies_byte_for_byte_and_records_version_and_digest()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new StagingWorld("stage-copies");

        var report = Staging.Stage(world.Input());

        report.Outcome.Should().Be(StageOutcomes.Staged, report.Reason);
        report.Digest.Should().Be(ExeDigest);
        report.Path.Should().Be(Path.Combine(world.Place.Root, ExeDigest, StagedCopies.ExeName));
        report.Version.Should().Be(Release);
        report.Source.Should().Be(world.Source);
        File.ReadAllBytes(report.Path).Should().Equal(ExeBytes, "the copy is the source byte for byte");
        var record = StageRecords.Read(world.Files, world.StageJson(ExeDigest), Cap).Should().BeOfType<StageRead.Found>().Which.Record;
        record.Should().Be(new StageRecord(Release, ExeDigest, Now, world.Source));
        StagedCopies.Check(world.Place.Root, report.Path, OwnVersion, world.Files, world.Files, Cap).Registrable.Should().BeTrue("the render accepts what the stage made");
    }

    /// <summary>The coai plan round (session 01e5e611): an unverified same-digest folder whose exe is IN USE is refused and named —
    /// never written over in place, its bytes and stage.json as they were, nothing swept.</summary>
    [Fact]
    public void A_stage_into_an_existing_digest_folder_never_overwrites()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new StagingWorld("stage-existing");
        var planted = Encoding.UTF8.GetBytes("other bytes left in the folder named for the source's digest");
        var exe = world.Plant(ExeDigest, planted, Record(ExeDigest, Release));
        var recordBefore = File.ReadAllText(world.StageJson(ExeDigest));
        var older = world.Plant(new string('a', 64), Encoding.UTF8.GetBytes("an older copy"), Record(new string('a', 64), "0.3.9"));

        StageReport report;
        using (Executing(exe))
        {
            report = Staging.Stage(world.Input());
        }

        report.Outcome.Should().Be(StageOutcomes.Refused);
        report.Reason.Should().Contain(Path.GetDirectoryName(exe)!).And.Contain("in use");
        File.ReadAllBytes(exe).Should().Equal(planted, "an existing folder is never written over in place");
        File.ReadAllText(world.StageJson(ExeDigest)).Should().Be(recordBefore, "its stage.json is not rewritten either");
        Directory.Exists(Path.GetDirectoryName(older)).Should().BeTrue("a refused stage sweeps nothing: the older copy may be the one the tasks run");
        report.Swept.Should().BeEmpty();
    }

    [Fact]
    public void A_stage_of_the_same_digest_that_verifies_is_reused_as_success()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new StagingWorld("stage-reused");
        var exe = world.Plant(ExeDigest, ExeBytes, Record(ExeDigest, Release));
        var recordBefore = File.ReadAllText(world.StageJson(ExeDigest));
        var writtenBefore = File.GetLastWriteTimeUtc(exe);

        var report = Staging.Stage(world.Input());

        report.Outcome.Should().Be(StageOutcomes.Reused, report.Reason);
        report.Path.Should().Be(exe);
        report.Digest.Should().Be(ExeDigest);
        File.ReadAllText(world.StageJson(ExeDigest)).Should().Be(recordBefore);
        File.GetLastWriteTimeUtc(exe).Should().Be(writtenBefore);
    }

    /// <summary>The coai plan round (session 01e5e611): a stage interrupted between the exe and stage.json (or a folder whose bytes or
    /// stage.json disagree) is removed through the deletion policy and staged FRESH — a retry recovers by itself.</summary>
    [Theory]
    [InlineData("half", "a half copy, no stage.json")]
    [InlineData("bytes", "other bytes under an agreeing stage.json")]
    [InlineData("record", "the right bytes under a stage.json naming another digest")]
    public void A_retry_after_an_interrupted_stage_replaces_the_unverified_folder_and_stages_fresh(string fault, string why)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new StagingWorld($"stage-retry-{fault}");
        var exe = fault switch
        {
            "half" => world.Plant(ExeDigest, ExeBytes[..10], string.Empty),
            "bytes" => world.Plant(ExeDigest, Encoding.UTF8.GetBytes("other bytes"), Record(ExeDigest, Release)),
            _ => world.Plant(ExeDigest, ExeBytes, Record(new string('d', 64), Release)),
        };
        File.WriteAllText(Path.Combine(world.Place.Root, ExeDigest, "leftover.tmp"), "a stray file of the interrupted stage");

        var report = Staging.Stage(world.Input());

        report.Outcome.Should().Be(StageOutcomes.Staged, $"{why}: {report.Reason}");
        File.ReadAllBytes(exe).Should().Equal(ExeBytes, why);
        StageRecords.Read(world.Files, world.StageJson(ExeDigest), Cap).Should().BeOfType<StageRead.Found>().Which.Record.Should().Be(new StageRecord(Release, ExeDigest, Now, world.Source));
        File.Exists(Path.Combine(world.Place.Root, ExeDigest, "leftover.tmp")).Should().BeFalse("the unverified folder was removed whole, then made fresh");
    }

    /// <summary>The keep rule at stage time (the review of WR-D2, M2, the coordinator's option 1): a stage sweeps UNVERIFIED folders
    /// only and keeps EVERY verified copy — the one a registered task names is among them, whichever it is; the <c>tasks run</c> start
    /// sweep (which runs from the registered copy) retires the older ones.</summary>
    [Fact]
    public void A_stage_keeps_the_previous_verified_copy_a_registered_task_may_still_name()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new StagingWorld("stage-keeps-previous");
        var previous = world.PlantVerified("the copy the installed tasks name", "0.3.9", new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero));
        var oldest = world.PlantVerified("a copy two versions back", "0.3.8", new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero));
        var unverified = new string('c', 64);
        world.Plant(unverified, Encoding.UTF8.GetBytes("a half cop"), string.Empty);

        var report = Staging.Stage(world.Input());

        report.Outcome.Should().Be(StageOutcomes.Staged, report.Reason);
        report.Retained.Select(f => f.Digest).Should().BeEquivalentTo([previous, oldest], "a stage keeps every verified copy");
        report.Retained.Should().OnlyContain(f => f.Why.Contains("verified copy", StringComparison.Ordinal));
        report.Swept.Select(f => f.Digest).Should().Equal([unverified]);
        Directory.Exists(Path.Combine(world.Place.Root, previous)).Should().BeTrue();
        Directory.Exists(Path.Combine(world.Place.Root, oldest)).Should().BeTrue();
        Directory.Exists(Path.Combine(world.Place.Root, ExeDigest)).Should().BeTrue();
    }

    /// <summary>M2: the tasks were registered to V1; V2 was staged and its registration DECLINED (WR-E3 stages before the modal); now V3
    /// is staged. "Keep the newest OTHER verified" would keep V2 and sweep V1 — the folder the tasks still name.</summary>
    [Fact]
    public void A_stage_after_a_declined_registration_keeps_the_folder_the_tasks_still_name()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new StagingWorld("stage-declined");
        var registered = world.PlantVerified("the copy the tasks are registered to", "0.3.8", new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero));
        var declined = world.PlantVerified("a copy staged, its registration declined", "0.3.9", new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero));

        var report = Staging.Stage(world.Input());

        report.Outcome.Should().Be(StageOutcomes.Staged, report.Reason);
        report.Swept.Should().BeEmpty();
        Directory.Exists(Path.Combine(world.Place.Root, registered)).Should().BeTrue("the folder the registered tasks name is never swept by a stage");
        Directory.Exists(Path.Combine(world.Place.Root, declined)).Should().BeTrue();
    }

    /// <summary>M2: wherever the newest copy is chosen, a <c>stagedAtUtc</c> in the future counts as NOW (and loses a tie to a genuine
    /// stamp) — a clock skew never makes an old copy "the newest" over the copy staged now.</summary>
    [Fact]
    public void A_future_stamp_counts_as_now_when_the_newest_copy_is_chosen()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new StagingWorld("sweep-future-stamp");
        var running = world.PlantVerified("the registered copy that runs", "0.3.8", new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero));
        var skewed = world.PlantVerified("an old copy stamped by a clock a year ahead", "0.3.7", Now.AddYears(1));
        var genuine = world.PlantVerified("the copy staged just now", Release, Now);

        var sweep = Staging.Sweep(world.Access, running);

        sweep.Retained.Select(f => f.Digest).Should().Equal([genuine], "a future stamp is clamped to now and a genuine stamp wins the tie");
        sweep.Swept.Select(f => f.Digest).Should().Equal([skewed]);
    }

    /// <summary>M1: a JUNCTION named with the digest is refused and never followed — no act goes through it to the folder it points at
    /// (which a sweep would otherwise find without its exe, and remove: the registered copy).</summary>
    [Fact]
    public void A_link_at_the_digest_name_is_refused_and_never_followed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new StagingWorld("stage-link");
        var target = world.PlantVerified("the registered copy a link points at", "0.3.9", new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero));
        var targetExe = Path.Combine(world.Place.Root, target, StagedCopies.ExeName);
        var bytesBefore = File.ReadAllBytes(targetExe);
        Assert.SkipUnless(DirectoryLinks.TryCreate(Path.Combine(world.Place.Root, ExeDigest), Path.Combine(world.Place.Root, target)), "this machine cannot make a directory link");

        var report = Staging.Stage(world.Input());

        report.Outcome.Should().Be(StageOutcomes.Refused);
        report.Reason.Should().Contain("link").And.Contain("never followed");
        report.Swept.Should().BeEmpty();
        File.ReadAllBytes(targetExe).Should().Equal(bytesBefore, "nothing went through the link");
        StagedCopies.Verify(world.Place.Root, target, world.Files, world.Files, Cap).Registrable.Should().BeTrue("the folder the link points at still verifies");
    }

    [Fact]
    public void A_file_at_the_digest_name_is_refused_as_in_the_way()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new StagingWorld("stage-file-in-way");
        Directory.CreateDirectory(world.Place.Root);
        var inTheWay = Path.Combine(world.Place.Root, ExeDigest);
        File.WriteAllText(inTheWay, "a file where the digest folder belongs");

        var report = Staging.Stage(world.Input());

        report.Outcome.Should().Be(StageOutcomes.Refused);
        report.Reason.Should().Contain("a file in the way").And.NotContain("in use");
        File.ReadAllText(inTheWay).Should().Be("a file where the digest folder belongs");
    }

    /// <summary>m4: two stages of one digest are serialised by THE run lock, never waited on — a held lock answers busy and does
    /// nothing; so do the removal and the run-start sweep.</summary>
    [Fact]
    public void A_held_run_lock_makes_the_stage_the_removal_and_the_sweep_busy()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new StagingWorld("stage-busy");
        var older = new string('a', 64);
        world.Plant(older, Encoding.UTF8.GetBytes("an unverified folder"), string.Empty);

        StageReport stage;
        StageRemoveReport remove;
        StagingSweep sweep;
        using (world.Files.TryLockExclusive(world.Place.RunLockFile) is ExclusiveLock.Held held ? held.Handle : throw new InvalidOperationException("the test could not take the lock"))
        {
            stage = Staging.Stage(world.Input());
            remove = Staging.Remove(world.Access);
            sweep = Staging.Sweep(world.Access, ExeDigest);
        }

        stage.Outcome.Should().Be(StageOutcomes.Busy);
        stage.Reason.Should().Contain("run lock");
        remove.Outcome.Should().Be(StageOutcomes.Busy);
        sweep.Busy.Should().Contain("run lock");
        Directory.Exists(Path.Combine(world.Place.Root, ExeDigest)).Should().BeFalse("a busy stage makes nothing");
        Directory.Exists(Path.Combine(world.Place.Root, older)).Should().BeTrue("a busy removal and a busy sweep remove nothing");
    }

    /// <summary>m6: a folder whose stage.json agrees but whose bytes do not hash to its name is swept as UNVERIFIED, not as "an older copy".</summary>
    [Fact]
    public void A_folder_whose_bytes_disagree_with_its_stage_json_is_swept_as_unverified()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new StagingWorld("sweep-bytes-disagree");
        var other = new string('a', 64);
        world.Plant(other, Encoding.UTF8.GetBytes("bytes that do not hash to the name"), Record(other, "0.3.9"));

        var sweep = Staging.Sweep(world.Access, ExeDigest);

        sweep.Swept.Should().ContainSingle().Which.Why.Should().StartWith("unverified").And.Contain("hashes to");
    }

    /// <summary>m1: a removal that fails for another reason than an exe in use says it could not be removed, never "in use".</summary>
    [Fact]
    public void A_folder_that_cannot_be_removed_for_another_reason_says_so_not_in_use()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new StagingWorld("sweep-read-only");
        var other = new string('a', 64);
        world.Plant(other, Encoding.UTF8.GetBytes("an unverified folder"), string.Empty);
        var readOnly = Path.Combine(world.Place.Root, other, "read-only.txt");
        File.WriteAllText(readOnly, "a read-only file the folder's removal cannot delete");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        try
        {
            var sweep = Staging.Sweep(world.Access, ExeDigest);

            sweep.Kept.Should().ContainSingle().Which.Why.Should().Contain("could not be removed").And.NotContain("in use");
        }
        finally
        {
            File.SetAttributes(readOnly, FileAttributes.Normal);
        }
    }

    /// <summary>m2: a LINK entry in the staging root — even one named like a digest — is never followed or removed by <c>--remove</c>;
    /// it is named as untouched and what it points at stays.</summary>
    [Fact]
    public void Remove_never_follows_a_link_entry()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new StagingWorld("remove-link");
        var elsewhere = Path.Combine(world.Place.StateDirectory, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, StagedCopies.ExeName), "a file a link points at");
        Directory.CreateDirectory(world.Place.Root);
        var link = Path.Combine(world.Place.Root, new string('f', 64));
        Assert.SkipUnless(DirectoryLinks.TryCreate(link, elsewhere), "this machine cannot make a directory link");

        var report = Staging.Remove(world.Access);

        report.Untouched.Should().Equal([link]);
        report.Removed.Should().BeEmpty();
        report.RootRemoved.Should().BeFalse();
        File.ReadAllText(Path.Combine(elsewhere, StagedCopies.ExeName)).Should().Be("a file a link points at");
    }

    /// <summary>A run from the OLD copy (its re-registration failed) keeps its own folder and the newest verified one: nothing is lost,
    /// and the next install finds the new copy.</summary>
    [Fact]
    public void A_run_start_sweep_from_an_older_copy_keeps_the_newest_verified_copy()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new StagingWorld("sweep-from-old");
        var old = world.PlantVerified("the old copy, still registered", "0.3.9", new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero));
        var newest = world.PlantVerified("the new copy, staged since", Release, new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero));

        var sweep = Staging.Sweep(world.Access, old);

        sweep.Swept.Should().BeEmpty();
        sweep.Retained.Select(f => f.Digest).Should().Equal([newest], "the newest verified copy is kept beside the running one");
        Directory.Exists(Path.Combine(world.Place.Root, old)).Should().BeTrue("the running copy's own folder is never swept");
        Directory.Exists(Path.Combine(world.Place.Root, newest)).Should().BeTrue();
    }

    [Fact]
    public void A_run_start_sweep_from_the_newest_copy_sweeps_the_older_one()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new StagingWorld("sweep-from-new");
        var old = world.PlantVerified("the old copy", "0.3.9", new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero));
        var newest = world.PlantVerified("the new copy the tasks now run", Release, new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero));

        var sweep = Staging.Sweep(world.Access, newest);

        sweep.Swept.Select(f => f.Digest).Should().Equal([old]);
        sweep.Retained.Should().BeEmpty("the running copy is the newest verified one: nothing else is kept");
        Directory.Exists(Path.Combine(world.Place.Root, old)).Should().BeFalse();
        Directory.Exists(Path.Combine(world.Place.Root, newest)).Should().BeTrue();
    }

    /// <summary>The coai plan round (session 01e5e611): the keep rule protects VERIFIED folders only — a folder named with the running
    /// digest that does not verify is swept as any unverified one.</summary>
    [Fact]
    public void An_unverified_folder_with_the_running_digest_is_not_protected_by_the_keep_rule()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new StagingWorld("sweep-unverified-own");
        var running = new string('c', 64);
        world.Plant(running, Encoding.UTF8.GetBytes("a half copy named like the running one"), string.Empty);
        var newest = world.PlantVerified("a verified copy", Release, new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero));

        var sweep = Staging.Sweep(world.Access, running);

        sweep.Swept.Select(f => f.Digest).Should().Equal([running]);
        sweep.Swept[0].Why.Should().Contain("unverified");
        sweep.Retained.Select(f => f.Digest).Should().Equal([newest]);
    }

    /// <summary>The run-start sweep from the newest copy: an older VERIFIED copy is swept and named with its release; a folder whose exe
    /// is held is kept WHOLE (its stage.json too) and named "in use"; the next sweep removes it.</summary>
    [Fact]
    public void An_older_folder_is_swept_and_an_executing_one_is_kept_and_named()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new StagingWorld("stage-sweep");
        var own = world.PlantVerified("the copy this run starts from", Release, new DateTimeOffset(2026, 10, 12, 8, 0, 0, TimeSpan.Zero));
        var olderDigest = world.PlantVerified("an older copy", "0.3.9", new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero));
        var runningDigest = world.PlantVerified("an older copy a task still runs", "0.3.8", new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero));
        var running = Path.Combine(world.Place.Root, runningDigest, StagedCopies.ExeName);

        StagingSweep sweep;
        using (Executing(running))
        {
            sweep = Staging.Sweep(world.Access, own);
        }

        sweep.Swept.Should().ContainSingle().Which.Should().Match<StagingFolder>(f => f.Digest == olderDigest && f.Path == Path.Combine(world.Place.Root, olderDigest));
        sweep.Swept[0].Why.Should().Contain("0.3.9");
        Directory.Exists(Path.Combine(world.Place.Root, olderDigest)).Should().BeFalse();
        sweep.Kept.Should().ContainSingle().Which.Should().Match<StagingFolder>(f => f.Digest == runningDigest && f.Path == Path.GetDirectoryName(running));
        sweep.Kept[0].Why.Should().Contain("in use");
        File.ReadAllText(world.StageJson(runningDigest)).Should().Contain("0.3.8", "a kept folder is kept whole — its stage.json too");

        var retried = Staging.Sweep(world.Access, own);

        retried.Swept.Select(f => f.Digest).Should().Equal([runningDigest], "once its run has ended, the next sweep removes it");
        Directory.Exists(Path.Combine(world.Place.Root, own)).Should().BeTrue("the sweep never removes the folder it runs from");
    }

    [Fact]
    public void A_half_copied_folder_is_swept_as_unverified()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new StagingWorld("stage-half");
        var halfDigest = new string('c', 64);
        world.Plant(halfDigest, Encoding.UTF8.GetBytes("the first bytes of a copy that never fin"), string.Empty);

        var report = Staging.Stage(world.Input());

        report.Outcome.Should().Be(StageOutcomes.Staged, report.Reason);
        report.Swept.Should().ContainSingle().Which.Why.Should().Contain("unverified").And.Contain(StageRecord.FileName);
        Directory.Exists(Path.Combine(world.Place.Root, halfDigest)).Should().BeFalse();
    }

    [Fact]
    public void A_mismatched_read_back_leaves_no_stage_json()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new StagingWorld("stage-mismatch");
        var olderDigest = new string('a', 64);
        var older = world.Plant(olderDigest, Encoding.UTF8.GetBytes("an older copy"), Record(olderDigest, "0.3.9"));

        var report = Staging.Stage(world.Input() with { Archive = new LyingReadBack(world.Files) });

        report.Outcome.Should().Be(StageOutcomes.Refused);
        report.Reason.Should().Contain("read back").And.Contain(LyingReadBack.Digest);
        File.Exists(world.StageJson(ExeDigest)).Should().BeFalse("no stage.json for a copy that did not read back equal");
        Directory.Exists(Path.Combine(world.Place.Root, ExeDigest)).Should().BeFalse("the copy this stage made is removed again, and its folder");
        Directory.Exists(Path.GetDirectoryName(older)).Should().BeTrue("a refused stage sweeps nothing");
    }

    [Fact]
    public void A_stage_without_its_own_path_is_refused_and_writes_nothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new StagingWorld("stage-no-path");

        var report = Staging.Stage(world.Input() with { Source = string.Empty });

        report.Outcome.Should().Be(StageOutcomes.Refused);
        Directory.Exists(world.Place.Root).Should().BeFalse();
    }

    [Fact]
    public void Remove_deletes_every_staging_folder_but_keeps_and_names_an_executing_one()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new StagingWorld("remove-kept");
        var olderDigest = new string('a', 64);
        var runningDigest = new string('b', 64);
        world.Plant(olderDigest, Encoding.UTF8.GetBytes("an older copy"), Record(olderDigest, "0.3.9"));
        world.Plant(ExeDigest, ExeBytes, Record(ExeDigest, Release));
        var running = world.Plant(runningDigest, Encoding.UTF8.GetBytes("a copy a task still runs"), Record(runningDigest, "0.3.8"));

        StageRemoveReport report;
        using (Executing(running))
        {
            report = Staging.Remove(world.Access);
        }

        report.Removed.Select(f => f.Digest).Should().BeEquivalentTo([olderDigest, ExeDigest]);
        report.Kept.Should().ContainSingle().Which.Should().Match<StagingFolder>(f => f.Digest == runningDigest && f.Path == Path.GetDirectoryName(running) && f.Why.Contains("in use"));
        report.RootRemoved.Should().BeFalse("the kept folder is still in it");
        Directory.Exists(world.Place.Root).Should().BeTrue();

        var again = Staging.Remove(world.Access);

        again.Removed.Select(f => f.Digest).Should().Equal(runningDigest);
        again.Kept.Should().BeEmpty();
        again.RootRemoved.Should().BeTrue();
        Directory.Exists(world.Place.Root).Should().BeFalse("the root goes once it is empty");
        Directory.Exists(world.Place.StateDirectory).Should().BeTrue("the state directory is never removed");
    }

    [Fact]
    public void Remove_never_touches_a_folder_that_is_not_a_digest()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new StagingWorld("remove-other");
        var olderDigest = new string('a', 64);
        world.Plant(olderDigest, Encoding.UTF8.GetBytes("an older copy"), Record(olderDigest, "0.3.9"));
        var notes = Path.Combine(world.Place.Root, "notes");
        Directory.CreateDirectory(notes);
        File.WriteAllText(Path.Combine(notes, "kept.txt"), "a person's file");
        var upper = Path.Combine(world.Place.Root, new string('D', 64));
        Directory.CreateDirectory(upper);
        File.WriteAllText(Path.Combine(upper, StagedCopies.ExeName), "not ours: a digest is written lower-case");
        var shorter = Path.Combine(world.Place.Root, new string('a', 63));
        Directory.CreateDirectory(shorter);
        var loose = Path.Combine(world.Place.Root, "readme.txt");
        File.WriteAllText(loose, "a loose file");

        var report = Staging.Remove(world.Access);

        report.Removed.Select(f => f.Digest).Should().Equal(olderDigest);
        report.Untouched.Should().BeEquivalentTo([notes, upper, shorter, loose]);
        report.RootRemoved.Should().BeFalse();
        File.ReadAllText(Path.Combine(notes, "kept.txt")).Should().Be("a person's file");
        File.Exists(Path.Combine(upper, StagedCopies.ExeName)).Should().BeTrue();
        Directory.Exists(shorter).Should().BeTrue();
        File.Exists(loose).Should().BeTrue();
    }

    [Fact]
    public void Remove_of_a_root_that_is_not_there_answers_nothing_removed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new StagingWorld("remove-none");

        var report = Staging.Remove(world.Access);

        report.Removed.Should().BeEmpty();
        report.Kept.Should().BeEmpty();
        report.RootRemoved.Should().BeFalse();
    }

    [Fact]
    public void The_sweep_keeps_the_folder_it_is_asked_to_keep()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new StagingWorld("sweep-keep");
        var olderDigest = new string('a', 64);
        world.Plant(olderDigest, Encoding.UTF8.GetBytes("an older copy"), Record(olderDigest, "0.3.9"));
        world.Plant(ExeDigest, ExeBytes, Record(ExeDigest, Release));

        var sweep = Staging.Sweep(world.Access, ExeDigest);

        sweep.Swept.Select(f => f.Digest).Should().Equal(olderDigest);
        Directory.Exists(Path.Combine(world.Place.Root, ExeDigest)).Should().BeTrue("tasks run keeps its own folder");
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string Record(string sha, string version) => Record(sha, version, new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero));

    private static string Record(string sha, string version, DateTimeOffset stagedAt) =>
        $$"""{ "version": "{{version}}", "sha256": "{{sha}}", "stagedAtUtc": "{{stagedAt:yyyy-MM-ddTHH:mm:ss}}+00:00", "source": "C:\\Users\\user\\.vscode\\extensions\\x\\bin\\wsl-care.exe" }""";

    /// <summary>The test's own stand-in for a running image: the exe held open with no delete sharing, so a delete fails with a
    /// sharing violation until the handle is released.</summary>
    private static FileStream Executing(string exe) => new(exe, FileMode.Open, FileAccess.Read, FileShare.Read);

    /// <summary>Every call of the real seam, but the read-back answers another digest — a disk that does not keep what it is given.</summary>
    private sealed class LyingReadBack(IArchiveFiles inner) : IArchiveFiles
    {
        public static readonly string Digest = new('e', 64);

        public SourceOpen OpenSource(string layoutRoot, string path) => inner.OpenSource(layoutRoot, path);

        public FolderBeneath OpenFolderBeneath(string baseFolder, IReadOnlyList<string> levels, DeletionScope scope) => inner.OpenFolderBeneath(baseFolder, levels, scope);

        public FolderBeneath OpenExistingFolderBeneath(string baseFolder, IReadOnlyList<string> levels) => inner.OpenExistingFolderBeneath(baseFolder, levels);

        public DurableAppend AppendDurably(BeneathFolder folder, string name, ReadOnlySpan<byte> bytes, DeletionScope scope) => inner.AppendDurably(folder, name, bytes, scope);

        public FileReadResult ReadCapped(BeneathFolder folder, string name, int maxBytes) => inner.ReadCapped(folder, name, maxBytes);

        public VerifiedRemoval RemoveIfUnchanged(BeneathFolder folder, string name, string expectedSha256, DeletionScope scope) => inner.RemoveIfUnchanged(folder, name, expectedSha256, scope);

        public ExclusiveFile CreateExclusive(BeneathFolder folder, string name, DeletionScope scope) => inner.CreateExclusive(folder, name, scope);

        public FileHash ReadBack(BeneathFolder folder, string name) => new FileHash.Hashed(Digest, ExeBytes.Length);

        public VerifiedRemoval RemoveOwnCopy(BeneathFolder folder, string name, FileIdentity created, DeletionScope scope) => inner.RemoveOwnCopy(folder, name, created, scope);

        public FolderFlush FlushFolder(BeneathFolder folder) => inner.FlushFolder(folder);

        public NoReplaceRename QuarantineRename(string layoutRoot, string path, string quarantinedName, DeletionScope scope) => inner.QuarantineRename(layoutRoot, path, quarantinedName, scope);

        public NoReplaceRename RenameBack(string layoutRoot, string quarantinedPath, string originalName, DeletionScope scope) => inner.RenameBack(layoutRoot, quarantinedPath, originalName, scope);

        public NoReplaceRename PromoteRestored(string layoutRoot, string temporaryPath, string finalName, DeletionScope scope) => inner.PromoteRestored(layoutRoot, temporaryPath, finalName, scope);

        public VerifiedRemoval RemoveVerified(string layoutRoot, string path, string expectedSha256, string archivedCopy, DeletionScope scope) => inner.RemoveVerified(layoutRoot, path, expectedSha256, archivedCopy, scope);

        public VerifiedRemoval RemoveEmptyFolder(string layoutRoot, string folder, DeletionScope scope) => inner.RemoveEmptyFolder(layoutRoot, folder, scope);
    }

    /// <summary>A Windows layout under a temporary root, the product's file system over it, and a source exe outside the staging root
    /// (where the extension folder's would be).</summary>
    private sealed class StagingWorld : IDisposable
    {
        private readonly TempRoot _temp;

        public StagingWorld(string purpose)
        {
            _temp = new TempRoot(purpose);
            var paths = new WindowsHostPaths(WindowsEnvironment.Sandboxed(_temp.Path));
            Files = new PhysicalFileSystem(paths) { OwnersAreThisProcess = true };
            Place = StagingPlace.For(paths);
            Source = _temp.File("Users/user/.vscode/extensions/x/bin/wsl-care.exe", string.Empty);
            File.WriteAllBytes(Source, ExeBytes);
        }

        public PhysicalFileSystem Files { get; }

        public StagingPlace Place { get; }

        public string Source { get; }

        public StageInput Input() => new(Place, Source, OwnVersion, Now, Cap, Files, Files);

        public StagingAccess Access => new(Place, Files, Files, Cap, Now);

        public string StageJson(string digest) => Path.Combine(Place.Root, digest, StageRecord.FileName);

        /// <summary>A digest folder with an exe of <paramref name="bytes"/> and, unless empty, a stage.json; the exe's path.</summary>
        public string Plant(string digest, byte[] bytes, string record)
        {
            var folder = Path.Combine(Place.Root, digest);
            Directory.CreateDirectory(folder);
            var exe = Path.Combine(folder, StagedCopies.ExeName);
            File.WriteAllBytes(exe, bytes);
            if (record.Length > 0)
            {
                File.WriteAllText(Path.Combine(folder, StageRecord.FileName), record);
            }

            return exe;
        }

        /// <summary>A VERIFIED digest folder: an exe whose bytes hash to its folder's name and an agreeing stage.json; the digest.</summary>
        public string PlantVerified(string content, string version, DateTimeOffset stagedAt)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            var digest = Sha(bytes);
            Plant(digest, bytes, Record(digest, version, stagedAt));
            return digest;
        }

        public void Dispose() => _temp.Dispose();
    }
}
