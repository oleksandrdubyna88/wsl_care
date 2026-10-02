namespace WslCare.Core.Processes.Policy;

/// <summary>Who a command runs as: the daemon itself (root under the timer), or the target user through <c>runuser</c>
/// (plan §15c #2).</summary>
public enum CommandScope
{
    Machine,
    User,
}

/// <summary>One element of a template's argument list — a closed set.</summary>
public abstract record ArgPart
{
    private ArgPart()
    {
    }

    /// <summary>Exactly this text.</summary>
    public sealed record Literal(string Text) : ArgPart;

    /// <summary>One value of <paramref name="Kind"/>.</summary>
    public sealed record Slot(string Name, SlotKind Kind) : ArgPart;

    /// <summary>Between <paramref name="Min"/> and <paramref name="Max"/> values of <paramref name="Kind"/> — only ever the
    /// LAST part (a batch of container ids, a list of volume names).</summary>
    public sealed record Repeat(string Name, SlotKind Kind, int Min, int Max) : ArgPart;
}

/// <summary>What binding a template's slots produced: the arguments, or why not — a value, never an exception.</summary>
public abstract record TemplateBinding
{
    private TemplateBinding()
    {
    }

    public sealed record Bound(IReadOnlyList<string> Arguments) : TemplateBinding;

    public sealed record Refused(string Reason) : TemplateBinding;
}

/// <summary>
/// One argv a component may run, DECLARED in code: the executable (a bare name, resolved on <c>PATH</c> — or, for a
/// user-scoped one, in the target user's bin folders), fixed words and typed slots (plan §15 #1, E3.S1). The
/// <see cref="CommandPolicy"/> allows an argv only when a declared template matches it; an action may only BIND the
/// templates it declares (<c>ActionCommands</c>), so neither a collector nor an action can run an argv nobody declared.
/// </summary>
/// <param name="Name">A stable short name (the log, the run detail).</param>
/// <param name="Scope">Run as the daemon, or as the target user.</param>
/// <param name="Executable">A bare name; never a path, never a shell.</param>
/// <param name="Parts">The arguments after the executable.</param>
/// <param name="Ceiling">How long it may run before its tree is killed.</param>
/// <param name="OutputCapChars">How much of each stream is kept.</param>
public sealed record CommandTemplate(string Name, CommandScope Scope, string Executable, IReadOnlyList<ArgPart> Parts, TimeSpan Ceiling, int OutputCapChars)
{
    /// <summary>A template of exactly <paramref name="command"/>'s argv — every argument a literal.</summary>
    public static CommandTemplate Fixed(ToolCommand command) =>
        new(command.Name, CommandScope.Machine, command.Executable, [.. command.Arguments.Select(a => new ArgPart.Literal(a))], command.Ceiling, command.OutputCapChars);

    /// <summary>The shape as a person reads it: <c>journalctl --vacuum-time=&lt;1..3650&gt;d</c>.</summary>
    public string Shape => string.Join(' ', [Executable, .. Parts.Select(Describe)]);

    /// <summary>Whether <paramref name="arguments"/> (the argv WITHOUT the executable) is an instance of this template.</summary>
    public bool Matches(IReadOnlyList<string> arguments)
    {
        var fixedParts = Parts.Count - (Parts.Count > 0 && Parts[^1] is ArgPart.Repeat ? 1 : 0);
        if (arguments.Count < fixedParts || !Parts.Take(fixedParts).Select((part, i) => Accepts(part, arguments[i])).All(ok => ok))
        {
            return false;
        }

        return Parts.Count > fixedParts ? RepeatAccepts((ArgPart.Repeat)Parts[^1], arguments.Skip(fixedParts).ToList()) : arguments.Count == fixedParts;
    }

    /// <summary>The arguments with <paramref name="values"/> in the slots, in order (the last ones fill a <see cref="ArgPart.Repeat"/>);
    /// refused, naming the slot, when a value is not its shape or the count is wrong.</summary>
    public TemplateBinding Bind(IReadOnlyList<string> values)
    {
        var arguments = new List<string>();
        var next = 0;
        foreach (var part in Parts)
        {
            if (Take(part, values, ref next, arguments) is { } refusal)
            {
                return new TemplateBinding.Refused($"{Name}: {refusal}");
            }
        }

        return next == values.Count
            ? new TemplateBinding.Bound(arguments)
            : new TemplateBinding.Refused($"{Name}: {values.Count - next} value(s) more than the template has slots for");
    }

    private static string? Take(ArgPart part, IReadOnlyList<string> values, ref int next, List<string> arguments)
    {
        switch (part)
        {
            case ArgPart.Literal literal:
                arguments.Add(literal.Text);
                return null;
            case ArgPart.Slot slot:
                return TakeOne(slot, values, ref next, arguments);
            case ArgPart.Repeat repeat:
                return TakeRest(repeat, values, ref next, arguments);
            default:
                throw new System.Diagnostics.UnreachableException("ArgPart is a closed set");
        }
    }

    private static string? TakeOne(ArgPart.Slot slot, IReadOnlyList<string> values, ref int next, List<string> arguments)
    {
        if (next >= values.Count || !slot.Kind.Accepts(values[next]))
        {
            return next >= values.Count ? $"no value for {slot.Name}" : $"{slot.Name} must be {slot.Kind.Describe}";
        }

        arguments.Add(values[next++]);
        return null;
    }

    private static string? TakeRest(ArgPart.Repeat repeat, IReadOnlyList<string> values, ref int next, List<string> arguments)
    {
        var rest = values.Skip(next).ToList();
        if (!RepeatAccepts(repeat, rest))
        {
            return $"{repeat.Name} takes {repeat.Min} to {repeat.Max} values of {repeat.Kind.Describe}; got {rest.Count}";
        }

        arguments.AddRange(rest);
        next = values.Count;
        return null;
    }

    private static bool RepeatAccepts(ArgPart.Repeat repeat, IReadOnlyList<string> values) =>
        values.Count >= repeat.Min && values.Count <= repeat.Max && values.All(repeat.Kind.Accepts);

    private static bool Accepts(ArgPart part, string value) => part switch
    {
        ArgPart.Literal literal => string.Equals(literal.Text, value, StringComparison.Ordinal),
        ArgPart.Slot slot => slot.Kind.Accepts(value),
        _ => false,
    };

    private static string Describe(ArgPart part) => part switch
    {
        ArgPart.Literal literal => literal.Text.Length > 40 ? $"<literal of {literal.Text.Length} chars>" : literal.Text,
        ArgPart.Slot slot => slot.Kind.Describe,
        ArgPart.Repeat repeat => $"{repeat.Kind.Describe}{{{repeat.Min}..{repeat.Max}}}",
        _ => throw new System.Diagnostics.UnreachableException("ArgPart is a closed set"),
    };
}
