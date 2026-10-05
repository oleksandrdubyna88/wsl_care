using System.Text;
using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Files;
using WslCare.Core.Json;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Actions;

/// <summary>
/// The request reader (E6.S0 review S1): a request is a file ROOT wrote for a root unit to act on, so the reader trusts
/// nothing it did not check — a regular file (never a FIFO it would block on, never a symbolic link), at most
/// <see cref="RunRequests.MaxRequestBytes"/>, owned by the state's owner with no group or other write bit (Linux), schema 1,
/// a known kind, known action ids, every shown name a 64-hex volume name and no more of them than a shown list carries;
/// a file that vanished between the listing and the read is skipped (review D4); and the folder is read at most
/// <see cref="RunRequests.MaxRequestsRead"/> files deep.
/// </summary>
public sealed class RunRequestsTests : IDisposable
{
    private static readonly DateTimeOffset Now = FixedTimeProvider.DefaultNow;

    private readonly SandboxHost _sandbox = new("run-requests");

    public void Dispose() => _sandbox.Dispose();

    private static RunRequestFile Valid(int pid) =>
        new(1, RunId.New(Now, pid), "act", ["A4"], RunTrigger.Manual, Now) { Shown = [new string('a', 64)] };

    private string Write(RunId id, string json)
    {
        var path = RunRequests.File(_sandbox.Paths, id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }

    private string Write(RunRequestFile request) => Write(request.RunId, JsonSerializer.Serialize(request, WslCareJsonContext.Default.RunRequestFile));

    private RunRequestRead Only() => RunRequests.List(_sandbox.Paths, _sandbox.Files).Should().ContainSingle().Subject;

    private static string Bad(RunRequestRead read) => read.Should().BeOfType<RunRequestRead.Bad>().Subject.Why;

    [Fact]
    public void A_valid_request_parses_the_positive_the_refusals_below_are_measured_against()
    {
        Write(Valid(10));

        Only().Should().BeOfType<RunRequestRead.Parsed>().Which.File.Shown.Should().ContainSingle();
    }

    [Theory]
    [InlineData("""{"schemaVersion":2,"runId":"20261002T120000Z-11","kind":"act","actions":["A4"],"trigger":"manual","createdAt":"2026-10-02T12:00:00+00:00"}""", "schema")]
    [InlineData("""{"schemaVersion":1,"runId":"20261002T120000Z-11","kind":"rm -rf","actions":["A4"],"trigger":"manual","createdAt":"2026-10-02T12:00:00+00:00"}""", "kind")]
    [InlineData("""{"schemaVersion":1,"runId":"20261002T120000Z-11","kind":"act","actions":["A99"],"trigger":"manual","createdAt":"2026-10-02T12:00:00+00:00"}""", "action")]
    [InlineData("""{"schemaVersion":1,"runId":"20261002T120000Z-11","kind":"act","actions":["A4"],"trigger":"manual","createdAt":"2026-10-02T12:00:00+00:00","shown":["../../etc/passwd"]}""", "shown")]
    [InlineData("""{"schemaVersion":1,"runId":"20261002T120000Z-11","kind":"collect","actions":["A4"],"trigger":"manual","createdAt":"2026-10-02T12:00:00+00:00"}""", "collect")]
    public void A_request_whose_content_is_not_what_root_writes_is_refused_naming_what(string json, string reason)
    {
        Write(RunId.New(Now, 11), json);

        Bad(Only()).Should().Contain(reason);
    }

    [Fact]
    public void A_request_carrying_more_shown_names_than_a_shown_list_holds_is_refused()
    {
        Write(Valid(12) with { Shown = [.. Enumerable.Range(0, ShownList.MaxNames + 1).Select(i => i.ToString("x64", System.Globalization.CultureInfo.InvariantCulture))] });

        Bad(Only()).Should().Contain("shown");
    }

    [Fact]
    public void A_request_larger_than_the_cap_is_refused_unread_past_it()
    {
        Write(RunId.New(Now, 13), new string(' ', RunRequests.MaxRequestBytes + 1));

        Bad(Only()).Should().Contain("larger than");
    }

    [Fact]
    public void Only_the_first_requests_of_a_flooded_folder_are_read_and_the_rest_is_named()
    {
        for (var pid = 1; pid <= RunRequests.MaxRequestsRead + 3; pid++)
        {
            Write(Valid(pid));
        }

        var reads = RunRequests.List(_sandbox.Paths, _sandbox.Files);

        reads.OfType<RunRequestRead.Parsed>().Should().HaveCount(RunRequests.MaxRequestsRead);
        reads.OfType<RunRequestRead.Bad>().Should().ContainSingle().Which.Why.Should().Contain("3 more");
    }

    [Fact]
    public void A_request_that_vanished_between_the_listing_and_the_read_is_skipped_not_reported_bad()
    {
        var path = Write(Valid(14));
        var vanishing = new VanishingFileSystem(_sandbox.Files, path);

        RunRequests.List(_sandbox.Paths, vanishing).Should().BeEmpty();
        RunRequests.Find(_sandbox.Paths, vanishing, Valid(14).RunId).Should().BeNull();
    }

    [Fact]
    public async Task A_fifo_a_symbolic_link_and_a_group_writable_request_are_refused_never_waited_on()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("FIFOs, symbolic links and mode bits are the distro's: run in WSL or on the Linux legs");
            return;
        }

        var fifo = RunRequests.File(_sandbox.Paths, RunId.New(Now, 15));
        Directory.CreateDirectory(Path.GetDirectoryName(fifo)!);
        (await ChildProcess.RunAsync("mkfifo", [fifo], new Dictionary<string, string?>())).Exit.Should().Be(0);
        var elsewhere = _sandbox.Root.File("elsewhere.json", JsonSerializer.Serialize(Valid(16), WslCareJsonContext.Default.RunRequestFile));
        File.CreateSymbolicLink(RunRequests.File(_sandbox.Paths, RunId.New(Now, 16)), elsewhere);
        var writable = Write(Valid(17));
        File.SetUnixFileMode(writable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead);

        var read = Task.Run(() => RunRequests.List(_sandbox.Paths, _sandbox.Files), TestContext.Current.CancellationToken);
        var finished = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)) == read;
        if (!finished)
        {
            await using var writer = new FileStream(fifo, FileMode.Open, FileAccess.Write); // releases a blocked reader
        }

        finished.Should().BeTrue("a request is never opened in a way that could block");
        var reasons = (await read).Select(Bad).ToList();
        reasons.Should().HaveCount(3);
        reasons.Should().Contain(r => r.Contains("a FIFO")).And.Contain(r => r.Contains("symbolic link")).And.Contain(r => r.Contains("writable by group or others"));
    }

    [Fact]
    public void A_request_not_owned_by_the_states_owner_is_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "file ownership is the distro's: run in WSL or on the Linux legs");
        Assert.SkipWhen(RegularFiles.EffectiveUid() == 0, "this account is root: a file it writes is root's");
        Write(Valid(18));
        var rootOnly = new PhysicalFileSystem(_sandbox.Paths) { TrustedStateOwner = 0 };

        Bad(RunRequests.List(_sandbox.Paths, rootOnly).Should().ContainSingle().Subject).Should().Contain("not uid 0");
    }

    /// <summary>The real file system, except that one file is gone by the time it is read (it was listed a moment before).</summary>
    private sealed class VanishingFileSystem(IFileSystem inner, string gone) : DelegatingFileSystem(inner)
    {
        public override FileReadResult ReadStateFile(string path, int maxBytes) =>
            path == gone ? new FileReadResult.Missing() : base.ReadStateFile(path, maxBytes);

        public override FileReadResult ReadFile(string path) =>
            path == gone ? new FileReadResult.Missing() : base.ReadFile(path);
    }
}
