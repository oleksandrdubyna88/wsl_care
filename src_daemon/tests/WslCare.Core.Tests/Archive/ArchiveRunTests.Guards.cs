using FluentAssertions;

using WslCare.Core.Archive;
using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;

namespace WslCare.Core.Tests.Archive;

/// <summary>Plan §15r, *Two fail-closed guards*, G2: phase 2 removes a source only after the archived copy re-hashes — a copy the
/// share may have answered from the Offline Files cache is NOT trusted: the source stays, the entry is kept for a later run, never
/// marked damaged.</summary>
public sealed partial class ArchiveRunTests
{
    [Fact]
    public void Phase_2_keeps_the_source_when_the_archived_copy_may_be_the_offline_caches()
    {
        var source = Session("cached");
        ArchiveRun.Run(Input(Config())).Agents.Single().Copied.Should().Be(1);
        _clock.Advance(TimeSpan.FromHours(25));

        var second = ArchiveRun.Run(Input(Config(), runId: "r2") with { Archive = new UntrustedReadBack(_sandbox.Files) }).Agents.Single();

        second.Removed.Should().Be(0);
        second.Damaged.Should().Be(0, "an untrusted answer is not a damaged copy — the entry waits");
        second.Waiting.Should().Be(1);
        File.Exists(source).Should().BeTrue();
        ArchiveRun.Run(Input(Config(), runId: "r3")).Agents.Single().Removed.Should().Be(1, "a trusted re-hash later removes it");
    }

    /// <summary>Every call of the real seam, but each read-back answered "not trusted" — what a share in the Offline Files cache says.</summary>
    private sealed class UntrustedReadBack(IArchiveFiles inner) : IArchiveFiles
    {
        public SourceOpen OpenSource(string layoutRoot, string path) => inner.OpenSource(layoutRoot, path);

        public FolderBeneath OpenFolderBeneath(string baseFolder, IReadOnlyList<string> levels, DeletionScope scope) => inner.OpenFolderBeneath(baseFolder, levels, scope);

        public FolderBeneath OpenExistingFolderBeneath(string baseFolder, IReadOnlyList<string> levels) => inner.OpenExistingFolderBeneath(baseFolder, levels);

        public DurableAppend AppendDurably(BeneathFolder folder, string name, ReadOnlySpan<byte> bytes, DeletionScope scope) => inner.AppendDurably(folder, name, bytes, scope);

        public FileReadResult ReadCapped(BeneathFolder folder, string name, int maxBytes) => inner.ReadCapped(folder, name, maxBytes);

        public VerifiedRemoval RemoveIfUnchanged(BeneathFolder folder, string name, string expectedSha256, DeletionScope scope) => inner.RemoveIfUnchanged(folder, name, expectedSha256, scope);

        public ExclusiveFile CreateExclusive(BeneathFolder folder, string name, DeletionScope scope) => inner.CreateExclusive(folder, name, scope);

        public FileHash ReadBack(BeneathFolder folder, string name) => new FileHash.Untrusted("the share may answer from the Offline Files cache (a test says so)");

        public VerifiedRemoval RemoveOwnCopy(BeneathFolder folder, string name, FileIdentity created, DeletionScope scope) => inner.RemoveOwnCopy(folder, name, created, scope);

        public FolderFlush FlushFolder(BeneathFolder folder) => inner.FlushFolder(folder);

        public NoReplaceRename QuarantineRename(string layoutRoot, string path, string quarantinedName, DeletionScope scope) => inner.QuarantineRename(layoutRoot, path, quarantinedName, scope);

        public NoReplaceRename RenameBack(string layoutRoot, string quarantinedPath, string originalName, DeletionScope scope) => inner.RenameBack(layoutRoot, quarantinedPath, originalName, scope);

        public NoReplaceRename PromoteRestored(string layoutRoot, string temporaryPath, string finalName, DeletionScope scope) => inner.PromoteRestored(layoutRoot, temporaryPath, finalName, scope);

        public VerifiedRemoval RemoveVerified(string layoutRoot, string path, string expectedSha256, string archivedCopy, DeletionScope scope) => inner.RemoveVerified(layoutRoot, path, expectedSha256, archivedCopy, scope);

        public VerifiedRemoval RemoveEmptyFolder(string layoutRoot, string folder, DeletionScope scope) => inner.RemoveEmptyFolder(layoutRoot, folder, scope);
    }
}
