namespace WslCare.Core.Archive;

/// <summary>
/// One rule's answer about a folder or a unit: it holds, or it refuses with its rule and its sentence — a closed result, never a
/// null (E9.S0 review round). The archive's ordered rule arrays (<see cref="BaseFolderRules"/>, <see cref="Selection"/>) take the
/// first refusal.
/// </summary>
public abstract record RuleVerdict
{
    private RuleVerdict()
    {
    }

    /// <summary>The rule holds.</summary>
    public static RuleVerdict Holds { get; } = new HoldsVerdict();

    /// <summary>The rule refuses: <paramref name="Rule"/> is its closed-set name, <paramref name="Why"/> the sentence.</summary>
    public sealed record Refuses(string Rule, string Why) : RuleVerdict;

    /// <summary>A refusal when <paramref name="refused"/> is true, else <see cref="Holds"/>.</summary>
    public static RuleVerdict When(bool refused, string rule, Func<string> why) => refused ? new Refuses(rule, why()) : Holds;

    /// <summary>The first refusal of <paramref name="rules"/> over <paramref name="subject"/>, asked in order and only while every rule
    /// before held; <see cref="Holds"/> when none refuses.</summary>
    public static RuleVerdict First<T>(IEnumerable<Func<T, RuleVerdict>> rules, T subject) =>
        rules.Select(rule => rule(subject)).Where(verdict => verdict is Refuses).DefaultIfEmpty(Holds).First();

    private sealed record HoldsVerdict : RuleVerdict;
}
