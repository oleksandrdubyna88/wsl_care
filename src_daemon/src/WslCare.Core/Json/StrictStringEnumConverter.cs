using System.Text.Json.Serialization;

namespace WslCare.Core.Json;

/// <summary>
/// An enum read and written by its names ONLY (PR #16 retro round, gate G2): <see cref="JsonStringEnumConverter{TEnum}"/> by
/// default also reads an integer as the member with that ordinal — <c>"trigger": 0</c> read as <c>timer</c> — so a number no
/// writer ever wrote became a name. Here an integer is refused like any value the enum does not know. The attribute form cannot
/// pass constructor arguments, so this subclass carries them; the source generator instantiates it (no reflection, Native AOT).
/// </summary>
public sealed class StrictStringEnumConverter<TEnum>() : JsonStringEnumConverter<TEnum>(namingPolicy: null, allowIntegerValues: false)
    where TEnum : struct, Enum;
