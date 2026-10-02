using System.Runtime.InteropServices;

namespace WslCare.Cli;

/// <summary>
/// Ctrl+C and SIGTERM (and SIGQUIT / Ctrl+Break) as one <see cref="CancellationToken"/>, so a
/// planned stop reaches every running command — which kills its process tree — and the run leaves
/// a state the next one can sweep, rather than an orphan and a half-written file (reliability rule:
/// cancellation is real, shutdown is planned; CLIs included).
/// </summary>
/// <remarks>The default handler is suppressed (<c>Cancel = true</c>): the process ends by returning
/// <see cref="ExitCode.Interrupted"/> from <c>Main</c> after the cleanup ran, not by being torn down
/// mid-write.</remarks>
internal sealed class ShutdownSignals : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly IReadOnlyList<PosixSignalRegistration> _registrations;

    public ShutdownSignals()
    {
        _registrations = [Register(PosixSignal.SIGINT), Register(PosixSignal.SIGTERM), Register(PosixSignal.SIGQUIT)];
    }

    public CancellationToken Token => _cancellation.Token;

    public void Dispose()
    {
        foreach (var registration in _registrations)
        {
            registration.Dispose();
        }

        _cancellation.Dispose();
    }

    private PosixSignalRegistration Register(PosixSignal signal) =>
        PosixSignalRegistration.Create(signal, context =>
        {
            context.Cancel = true;
            _cancellation.Cancel();
        });
}
