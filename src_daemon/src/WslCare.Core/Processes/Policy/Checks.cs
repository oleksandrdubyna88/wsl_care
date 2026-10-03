namespace WslCare.Core.Processes.Policy;

/// <summary>A conjunction of named tests, asked IN ORDER and stopping at the first that fails — so a test may rely on the
/// ones before it (a length checked before an index). The shape the policy's shape checks are written in, one fact per test,
/// so no single method carries a long chain of conditions (C# doctrine §6).</summary>
internal static class Checks
{
    public static bool All<T>(T value, params Func<T, bool>[] tests) => tests.All(test => test(value));
}
