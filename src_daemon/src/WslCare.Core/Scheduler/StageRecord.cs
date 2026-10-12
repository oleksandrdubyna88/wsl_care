using System.Text.Json;
using System.Text.RegularExpressions;

using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Json;

namespace WslCare.Core.Scheduler;

/// <summary>
/// <c>stage.json</c> beside a staged copy (PLAN_windows_runner.md §3.7): <c>&lt;stagingRoot&gt;\&lt;digest&gt;\stage.json</c> next to
/// <c>wsl-care.exe</c>. <c>tasks stage</c> (WR-D2) writes it through <see cref="WslCareJsonContext"/>; <c>tasks render</c> (WR-D1)
/// reads it strictly (<see cref="StageRecords"/>).
/// </summary>
/// <param name="Version">The release number of the staged exe (its <c>--version</c> without the <c>+commit</c>,
/// <see cref="StagedCopies.ReleaseOf"/>).</param>
/// <param name="Sha256">The lowercase hex SHA-256 of the copy as it was read back — the name of its folder.</param>
/// <param name="StagedAtUtc">When the copy was made, UTC.</param>
/// <param name="Source">The absolute path the copy was made from (the extension folder's exe).</param>
public sealed record StageRecord(string Version, string Sha256, DateTimeOffset StagedAtUtc, string Source)
{
    public const string FileName = "stage.json";
}

/// <summary>What reading a <c>stage.json</c> found — a closed set.</summary>
public abstract record StageRead
{
    private StageRead()
    {
    }

    public sealed record Found(StageRecord Record) : StageRead;

    public sealed record Missing : StageRead;

    /// <summary>There, but not a record this reader takes, and why (the reason starts with the file's name).</summary>
    public sealed record Refused(string Why) : StageRead;
}

/// <summary>
/// The strict reader of <see cref="StageRecord"/>: exactly the four members, each present and well formed — a member missing, an
/// unknown one, a malformed digest or an empty value is refused naming it, never taken with a default (the C# doctrine §4a: a
/// deserializer does not run defaults, so an omitted member would arrive null).
/// </summary>
public static partial class StageRecords
{
    /// <summary>The members, as <see cref="WslCareJsonContext"/> spells them (camel case).</summary>
    private static readonly string[] Members = ["version", "sha256", "stagedAtUtc", "source"];

    /// <summary>The <c>stage.json</c> at <paramref name="path"/> — this account's own file, a regular file read whole up to
    /// <paramref name="maxBytes"/> (<c>records.maxStateFileBytes</c>).</summary>
    public static StageRead Read(IFileSystem files, string path, int maxBytes) => files.ReadRegularFile(path, maxBytes) switch
    {
        FileReadResult.Content content => Parse(content.Bytes),
        FileReadResult.Missing => new StageRead.Missing(),
        FileReadResult.Unreadable unreadable => new StageRead.Refused($"{StageRecord.FileName} could not be read: {unreadable.Reason}"),
        _ => new StageRead.Refused($"{StageRecord.FileName} could not be read"),
    };

    /// <summary>The record in <paramref name="bytes"/>, or why it is refused.</summary>
    public static StageRead Parse(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            return ShapeProblem(document.RootElement) is { Length: > 0 } shape
                ? new StageRead.Refused($"{StageRecord.FileName} {shape}")
                : Checked(JsonSerializer.Deserialize(bytes, WslCareJsonContext.Default.StageRecord));
        }
        catch (JsonException e)
        {
            return new StageRead.Refused($"{StageRecord.FileName} cannot be read as a stage record: {e.Message}");
        }
    }

    /// <summary>Not one object, an unknown member, or a missing one; empty when the members are exactly the four.</summary>
    private static string ShapeProblem(JsonElement root) => root switch
    {
        { ValueKind: not JsonValueKind.Object } => "is not one JSON object",
        _ when root.EnumerateObject().Select(p => p.Name).FirstOrDefault(n => !Members.Contains(n, StringComparer.Ordinal)) is { } unknown => $"holds an unknown member \"{unknown}\"",
        _ when Members.FirstOrDefault(m => !root.TryGetProperty(m, out _)) is { } missing => $"lacks \"{missing}\"",
        _ => string.Empty,
    };

    private static StageRead Checked(StageRecord? record) =>
        record is not null && ValueProblem(record) is { Length: 0 }
            ? new StageRead.Found(record)
            : new StageRead.Refused($"{StageRecord.FileName} {(record is null ? "is null" : ValueProblem(record))}");

    /// <summary>The first member whose value is not well formed; empty when every one is.</summary>
    private static string ValueProblem(StageRecord record) => record switch
    {
        { Version: var v } when string.IsNullOrWhiteSpace(v) => "names no \"version\"",
        { Sha256: var sha } when sha is null || !Sha256Shape().IsMatch(sha) => "has a \"sha256\" that is not 64 lower-case hex digits",
        { StagedAtUtc: var at } when at == default => "names no \"stagedAtUtc\"",
        { Source: var source } when string.IsNullOrWhiteSpace(source) || !PathRules.Windows.IsAbsolute(source) => "names no absolute \"source\"",
        _ => string.Empty,
    };

    /// <summary>A SHA-256 as this family writes it: 64 lower-case hex digits.</summary>
    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    internal static partial Regex Sha256Shape();
}
