namespace WslCare.Core.Collectors;

/// <summary>
/// One figure a collector tried to read: the value, or why it could not be read. "Not read" and
/// "zero" are different facts (C# doctrine §4, plan §15b #7), so a figure that cannot be read is
/// never rendered as 0 — it carries its reason all the way to the JSON answer.
/// </summary>
/// <typeparam name="T">What the figure is.</typeparam>
public abstract record Reading<T>
{
    private Reading()
    {
    }

    /// <summary>The figure was read.</summary>
    public sealed record Available(T Value) : Reading<T>;

    /// <summary>The figure could not be read, and this is why — in a sentence a person can act on.</summary>
    public sealed record Unavailable(string Reason) : Reading<T>;

    public bool IsAvailable => this is Available;

    /// <summary>The value transformed; an unavailable figure stays unavailable with its reason.</summary>
    public Reading<TOut> Map<TOut>(Func<T, TOut> transform) => this switch
    {
        Available a => new Reading<TOut>.Available(transform(a.Value)),
        Unavailable u => new Reading<TOut>.Unavailable(u.Reason),
        _ => throw new System.Diagnostics.UnreachableException("Reading is a closed set"),
    };

    /// <summary>A figure computed from this one by a step that may itself be unavailable.</summary>
    public Reading<TOut> Bind<TOut>(Func<T, Reading<TOut>> next) => this switch
    {
        Available a => next(a.Value),
        Unavailable u => new Reading<TOut>.Unavailable(u.Reason),
        _ => throw new System.Diagnostics.UnreachableException("Reading is a closed set"),
    };

    /// <summary>The value, or <paramref name="fallback"/> when unavailable — for arithmetic that has
    /// already decided what an absent figure means (never for rendering).</summary>
    public T ValueOr(T fallback) => this is Available a ? a.Value : fallback;

    /// <summary>The reason, or empty when the figure was read.</summary>
    public string ReasonOrEmpty => this is Unavailable u ? u.Reason : string.Empty;
}

/// <summary>Constructors and combinators for <see cref="Reading{T}"/>.</summary>
public static class Reading
{
    public static Reading<T> Of<T>(T value) => new Reading<T>.Available(value);

    public static Reading<T> Missing<T>(string reason) => new Reading<T>.Unavailable(reason);

    /// <summary>Two figures combined; the first unavailable one decides the reason.</summary>
    public static Reading<TOut> Combine<TA, TB, TOut>(Reading<TA> a, Reading<TB> b, Func<TA, TB, TOut> combine) =>
        a.Bind(x => b.Map(y => combine(x, y)));
}
