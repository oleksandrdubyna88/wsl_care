using FluentAssertions;

using Serilog.Events;
using Serilog.Parsing;

using WslCare.Cli.Logging;
using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>
/// Nothing the user typed, and nothing read from a file, reaches stderr as a raw control character:
/// a refusal stays ONE line, and no newline, carriage return or escape sequence splits or repaints it.
/// </summary>
/// <remarks>The theory cases are NAMED fixtures rather than the strings themselves, so a failing test
/// name never carries an escape that clears the terminal it is printed on.</remarks>
public sealed class ControlCharacterTests
{
    private static readonly Dictionary<string, string> Keys = new(StringComparer.Ordinal)
    {
        ["newline"] = "bad\nkey",
        ["carriage-return"] = "bad\rkey",
        ["clear-screen-escape"] = "\u001b[2J",
    };

    public static TheoryData<string, string> VerbsAndKeys
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var verb in new[] { "get", "set", "reset" })
            {
                foreach (var key in Keys.Keys)
                {
                    data.Add(verb, key);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(VerbsAndKeys))]
    public void An_unknown_key_carrying_control_characters_is_refused_in_one_clean_message_line(string verb, string fixture)
    {
        using var sandbox = new SandboxHost("ctl-key");
        var key = Keys[fixture];
        string[] argv = verb == "set" ? ["config", verb, key, "1"] : ["config", verb, key];

        var (exit, stdout, stderr) = CliRun.Over(sandbox, argv);

        exit.Should().Be((int)ExitCode.Usage);
        stdout.Should().BeEmpty();
        CliRun.Lines(stderr).Should().ContainSingle("a refusal is ONE line whatever was typed")
            .Which.Should().StartWith("wsl-care: unknown key");
        TerminalText.ForeignControlCharacters(stderr).Should().BeEmpty("no refusal may carry a control character the user typed");
        File.Exists(sandbox.Paths.UserConfigFile).Should().BeFalse("a refused key writes nothing");
    }

    [Fact]
    public void An_internal_error_whose_reason_holds_control_characters_is_still_one_clean_line()
    {
        // A refusal's reason names paths, and a Linux path may hold any byte but '/' and NUL.
        using var sandbox = new SandboxHost("ctl-internal");
        var files = new RefusingAtomicWrites(sandbox.Files, "a path with a\nnewline and a \u001b[2J clear-screen escape");

        var (exit, stdout, stderr) = CliRun.Over(sandbox, files, "config", "set", "dryRun", "false");

        exit.Should().Be((int)ExitCode.Internal);
        stdout.Should().BeEmpty();
        CliRun.Lines(stderr).Should().ContainSingle("an internal error is ONE line too")
            .Which.Should().StartWith("wsl-care: internal error: ").And.Contain("a path with a?newline");
        TerminalText.ForeignControlCharacters(stderr).Should().BeEmpty();
    }

    [Fact]
    public void The_console_sink_never_writes_a_control_character_carried_by_a_logged_value()
    {
        // A key read from the user's file is logged on stderr; "\u001b[2J" in JSON is a real escape by then.
        var output = new StringWriter();
        var sink = new AnsiConsoleSink(output);
        var template = new MessageTemplateParser().Parse("configuration error: {ConfigError}");

        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Error, null, template, [new LogEventProperty("ConfigError", new ScalarValue("unknown key \"\u001b[2J\nforged\""))]));
        sink.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Fatal, new InvalidOperationException("boom \u001b[2J"), new MessageTemplateParser().Parse("internal error"), []));

        var text = output.ToString();
        TerminalText.ForeignControlCharacters(text).Should().BeEmpty("only the sink's own colour escapes may reach the terminal");
        CliRun.Lines(TerminalText.WithoutColour(text)).Should().Contain(l => l.EndsWith("configuration error: unknown key \"?[2J?forged\"", StringComparison.Ordinal), "a logged value cannot split its line");
        text.Should().Contain("boom ?[2J", "the exception is still rendered, its control characters replaced");
    }

    /// <summary>The sandbox's real file system, except that every atomic write is refused with <paramref name="reason"/>.</summary>
    private sealed class RefusingAtomicWrites(IFileSystem inner, string reason) : IFileSystem
    {
        public FileReadResult ReadFile(string path) => inner.ReadFile(path);

        public bool FileExists(string path) => inner.FileExists(path);

        public bool DirectoryExists(string path) => inner.DirectoryExists(path);

        public IReadOnlyList<string> ListDirectories(string path) => inner.ListDirectories(path);

        public LinkReadResult ReadLink(string path) => inner.ReadLink(path);

        public VolumeReadResult MeasureVolume(string path) => inner.MeasureVolume(path);


        public FileSizeResult FileSize(string path) => inner.FileSize(path);

        public void CreateDirectory(string path) => inner.CreateDirectory(path);

        public DeletionVerdict WriteFileAtomically(string path, ReadOnlySpan<byte> content, DeletionScope scope) =>
            DeletionVerdict.Refuse(DeletionRule.OutsideDeclaredRoot, reason);

        public void AppendLine(string path, string line, TimeSpan lockTimeout) => inner.AppendLine(path, line, lockTimeout);

        public DeletionVerdict DeleteFile(string path, DeletionScope scope) => inner.DeleteFile(path, scope);

        public DeletionVerdict DeleteDirectory(string path, DeletionScope scope) => inner.DeleteDirectory(path, scope);

        public DeletionVerdict MoveFile(string from, string to, DeletionScope scope) => inner.MoveFile(from, to, scope);

        public DeletionVerdict MoveDirectory(string from, string to, DeletionScope scope) => inner.MoveDirectory(from, to, scope);
    }
}
