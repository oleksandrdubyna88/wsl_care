using System.Text.Json;
using System.Text.Json.Serialization;

using WslCare.Core.Config;
using WslCare.Core.Records;

namespace WslCare.Core.Json;

/// <summary>
/// Every type the daemon writes as JSON, declared for the source generator (plan §8): Native AOT
/// has no reflection-based serializer, and the projects set
/// <c>JsonSerializerIsReflectionEnabledByDefault=false</c> so reaching for one is a compile error
/// rather than a crash on a user's machine.
/// </summary>
/// <remarks>Two instances of one shape: <c>Default</c> indents, for what a person reads on a
/// terminal (<c>config get --json</c>); <see cref="Compact"/> writes one line, for
/// <c>history.jsonl</c>, where a record IS a line. A context built with explicit options takes its
/// naming policy from them, so the camel case is stated twice — once in the attribute, once here —
/// and a test holds the two outputs to the same property names.</remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(RunRecord))]
[JsonSerializable(typeof(ConfigReport))]
public sealed partial class WslCareJsonContext : JsonSerializerContext
{
    public static readonly WslCareJsonContext Compact = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    });
}
