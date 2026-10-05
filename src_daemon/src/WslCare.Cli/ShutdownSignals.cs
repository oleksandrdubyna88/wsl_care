using System.Runtime.InteropServices;

namespace WslCare.Cli;

/// <summary>
/// Ctrl+C, SIGTERM, SIGHUP (and SIGQUIT / Ctrl+Break) as one <see cref="CancellationToken"/>, so a
/// planned stop reaches every running command — which kills its process tree — and the run leaves
/// a state the next one can sweep, rather than an orphan and a half-written file (reliability rule:
/// cancellation is real, shutdown is planned; CLIs included).
/// </summary>
/// <remarks>
/// <para>The default handler is suppressed (<c>Cancel = true</c>): the process ends by returning
/// <see cref="ExitCode.Interrupted"/> from <c>Main</c> after the cleanup ran, not by being torn down
/// mid-write.</para>
/// <para>SIGHUP since E6.S0 (plan §15j B2): a terminal that closes, or the <c>wsl.exe</c> relay that
/// started the CLI going away (a VS Code reload), delivers it to the foreground group — and its default
/// action killed a confirm mid-<c>docker volume rm</c> with no detail and no history line. Now it is a
/// cancellation like the others: the run records itself <c>interrupted</c>, naming the signal
/// (<see cref="Cause"/>). Defence in depth only: the panel's confirm runs DETACHED (E6.S1). On Windows
/// .NET maps SIGHUP to the console's close event.</para>
/// </remarks>
internal sealed class ShutdownSignals : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly IReadOnlyList<PosixSignalRegistration> _registrations;
    private const string NoSignal = "a signal";

    private string _cause = NoSignal;

    public ShutdownSignals()
    {
        _registrations = [Register(PosixSignal.SIGINT), Register(PosixSignal.SIGTERM), Register(PosixSignal.SIGQUIT), Register(PosixSignal.SIGHUP)];
    }

    public CancellationToken Token => _cancellation.Token;

    /// <summary>The FIRST signal that cancelled this process, in words (<c>a signal</c> before any) — set once, atomically.</summary>
    public string Cause => Volatile.Read(ref _cause);

    public void Dispose()
    {
        foreach (var registration in _registrations)
        {
            registration.Dispose();
        }

        _cancellation.Dispose();
    }

    /// <summary>How a record names <paramref name="signal"/>.</summary>
    internal static string Describe(PosixSignal signal) => signal switch
    {
        PosixSignal.SIGHUP => "SIGHUP (the terminal or the wsl.exe that started it went away)",
        PosixSignal.SIGINT => "SIGINT (Ctrl+C)",
        PosixSignal.SIGTERM => "SIGTERM (a planned stop: systemctl stop, a shutdown)",
        _ => $"{signal}",
    };

    private PosixSignalRegistration Register(PosixSignal signal) =>
        PosixSignalRegistration.Create(signal, context =>
        {
            context.Cancel = true;
            Interlocked.CompareExchange(ref _cause, Describe(signal), NoSignal);
            _cancellation.Cancel();
        });
}
