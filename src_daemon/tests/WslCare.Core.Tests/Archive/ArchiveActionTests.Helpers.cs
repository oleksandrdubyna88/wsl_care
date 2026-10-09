using WslCare.Core.Actions;
using WslCare.Core.Files;
using WslCare.Core.Processes;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Archive;

public sealed partial class ArchiveActionTests
{
    /// <summary>The real sandbox file system, recording every path root READS through it (files and listings).</summary>
    private sealed class ReadRecordingFiles(IFileSystem inner) : DelegatingFileSystem(inner)
    {
        private readonly List<string> _paths = [];

        public IReadOnlyList<string> Paths
        {
            get
            {
                lock (_paths)
                {
                    return [.. _paths];
                }
            }
        }

        private T Read<T>(string path, Func<T> read)
        {
            lock (_paths)
            {
                _paths.Add(path.Replace('\\', '/'));
            }

            return read();
        }

        public override FileReadResult ReadFile(string path) => Read(path, () => base.ReadFile(path));

        public override FileReadResult ReadRegularFile(string path, int maxBytes) => Read(path, () => base.ReadRegularFile(path, maxBytes));

        public override FileReadResult ReadStateFile(string path, int maxBytes) => Read(path, () => base.ReadStateFile(path, maxBytes));

        public override FileReadResult ReadUserFile(string path, int maxBytes, uint owner, string beneath) => Read(path, () => base.ReadUserFile(path, maxBytes, owner, beneath));

        public override FileReadResult ReadNoFollowFile(string path, int maxBytes) => Read(path, () => base.ReadNoFollowFile(path, maxBytes));

        public override IReadOnlyList<string> ListDirectories(string path) => Read(path, () => base.ListDirectories(path));

        public override IReadOnlyList<string> ListFiles(string path) => Read(path, () => base.ListFiles(path));

        public override IReadOnlyList<FileEntry> ListEntries(string path) => Read(path, () => base.ListEntries(path));
    }

    /// <summary>Signals nothing; counts what it was asked.</summary>
    private sealed class CountingSignals : IProcessSignals
    {
        public int Asked { get; private set; }

        public Task<IReadOnlyList<SignalOutcome>> TerminateAllAsync(IReadOnlyList<ProcessIdentity> processes, TimeSpan grace, CancellationToken cancellationToken)
        {
            Asked += processes.Count;
            return Task.FromResult<IReadOnlyList<SignalOutcome>>([]);
        }
    }

    /// <summary>The recorder's streams, observed after each line, with the child "gone" right before the stream ends.</summary>
    private sealed class ObservingStreams(RecordingCommandRunner inner, Action afterLine, Action beforeEnd) : ICommandRunner
    {
        public Task<CommandOutcome> RunAsync(CommandRequest request, CancellationToken cancellationToken) => inner.RunAsync(request, cancellationToken);

        public async Task<CommandOutcome> StreamAsync(CommandRequest request, Action<string> onStdoutLine, CancellationToken cancellationToken)
        {
            var outcome = await inner.StreamAsync(request, line =>
            {
                onStdoutLine(line);
                afterLine();
            }, cancellationToken);
            beforeEnd();
            return outcome;
        }
    }
}
