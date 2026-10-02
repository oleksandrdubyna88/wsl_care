using System.Reflection;

namespace WslCare.Core;

/// <summary>
/// The version a build answers to, read from the assembly the SDK stamped.
/// </summary>
/// <remarks>
/// <para>Every assembly under <c>src_daemon/</c> is stamped from <c>src_daemon/version.txt</c>
/// (see <c>src_daemon/Directory.Build.props</c>), and the SDK appends <c>+&lt;commit&gt;</c> when it
/// knows the source revision — so <see cref="Text"/> is the release number, optionally followed by
/// the commit it was built from.</para>
/// <para>An assembly with no informational version is a different fact from one stamped
/// <c>0.0.0</c>, and the two must not read alike: it reports <see cref="Unstamped"/> with
/// <see cref="IsStamped"/> false rather than an empty string or a zero.</para>
/// </remarks>
public sealed record ProductVersion
{
    /// <summary>What an assembly without an informational version reports.</summary>
    public const string Unstamped = "unknown";

    private ProductVersion(string text, bool isStamped)
    {
        Text = text;
        IsStamped = isStamped;
    }

    /// <summary>The version text, or <see cref="Unstamped"/>.</summary>
    public string Text { get; }

    /// <summary>Whether the assembly carried an informational version at all.</summary>
    public bool IsStamped { get; }

    /// <summary>The version <paramref name="assembly"/> was stamped with.</summary>
    public static ProductVersion Of(Assembly assembly)
    {
        var stamped = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(stamped)
            ? new ProductVersion(Unstamped, isStamped: false)
            : new ProductVersion(stamped, isStamped: true);
    }
}
