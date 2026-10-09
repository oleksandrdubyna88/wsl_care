using System.Text.Json;
using System.Text.Json.Serialization;

using WslCare.Core.Config;
using WslCare.Core.Docker;
using WslCare.Core.Preview;
using WslCare.Core.Records;
using WslCare.Core.Status;

namespace WslCare.Core.Json;

/// <summary>
/// Every type the daemon writes as JSON, declared for the source generator (plan §8): Native AOT
/// has no reflection-based serializer, and the projects set
/// <c>JsonSerializerIsReflectionEnabledByDefault=false</c> so reaching for one is a compile error
/// rather than a crash on a user's machine.
/// </summary>
/// <remarks><para>Two instances of one shape: <c>Default</c> indents, for what a person reads on a
/// terminal (<c>config get --json</c>); <see cref="Compact"/> writes one line, for
/// <c>history.jsonl</c>, where a record IS a line. A context built with explicit options takes its
/// naming policy from them, so the camel case is stated twice — once in the attribute, once here —
/// and a test holds the two outputs to the same property names.</para>
/// <para>Nulls are not written (<c>WhenWritingNull</c>, stated twice for the same reason): the only nullable
/// members are the value slots of the <c>status</c> figures and a run's optional slow parts, where an
/// absent key IS the contract — an unavailable figure carries <c>available: false</c> and a reason, and
/// no value that could be read as 0.</para></remarks>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RunRecord))]
[JsonSerializable(typeof(ConfigReport))]
[JsonSerializable(typeof(StatusReport))]
[JsonSerializable(typeof(StatusLimits))]
[JsonSerializable(typeof(PreviewReport))]
[JsonSerializable(typeof(VolumeSeenRecord))]
[JsonSerializable(typeof(Collect.RunDetail))]
[JsonSerializable(typeof(Collect.CollectReport))]
[JsonSerializable(typeof(RunDetailHead))]
[JsonSerializable(typeof(RunDetailVerdicts))]
[JsonSerializable(typeof(Events.CoverageLineJson))]
[JsonSerializable(typeof(Events.StartsSummary))]
[JsonSerializable(typeof(Doctor.DoctorReport))]
[JsonSerializable(typeof(Status.BusyReport))]
[JsonSerializable(typeof(Actions.Engine.ActRunDetail))]
[JsonSerializable(typeof(Actions.Engine.ActReport))]
[JsonSerializable(typeof(Actions.Engine.RunningFile))]
[JsonSerializable(typeof(Actions.Engine.FirstTimerRun))]
[JsonSerializable(typeof(Actions.Clock.ClockFixRecord))]
[JsonSerializable(typeof(History.RunsReport))]
[JsonSerializable(typeof(History.LogsReport))]
[JsonSerializable(typeof(History.DetailKindView))]
[JsonSerializable(typeof(History.TimerPassView))]
[JsonSerializable(typeof(History.RunShowReport))]
[JsonSerializable(typeof(Actions.Engine.RunRequestFile))]
[JsonSerializable(typeof(Actions.Engine.HandOffReport))]
[JsonSerializable(typeof(Agents.AgentCatalogueFile))]
[JsonSerializable(typeof(Agents.AgentsReport))]
[JsonSerializable(typeof(Agents.AgentProbeReport))]
[JsonSerializable(typeof(Actions.Suspects.AgentCpuFile))]
[JsonSerializable(typeof(Mcp.McpCpuFile))]
public sealed partial class WslCareJsonContext : JsonSerializerContext
{
    public static readonly WslCareJsonContext Compact = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    });
}
