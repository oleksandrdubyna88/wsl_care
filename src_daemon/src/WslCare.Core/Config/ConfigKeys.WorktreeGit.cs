namespace WslCare.Core.Config;

/// <summary>The <c>worktrees.git*</c> keys (PLAN_disk_hygiene_windows_E19.md E19.S4): which <c>git.exe</c> the worktree scan may
/// ask, how long one question may take, and how a batch of names is bounded. In their own file beside the other
/// <c>worktrees.*</c> keys of the scan, never one group in two files of the same name.</summary>
public static partial class ConfigKeys
{
    public static class WorktreeGit
    {
        /// <summary>An administrator's choice of <c>git.exe</c>, tried after the Program Files install and admitted only by the
        /// same ACL rule (decision 9). Empty means "the supported install only". A path is a program to run, so only the MACHINE
        /// layer may name it.</summary>
        public static readonly ConfigKey.TextKey GitPath = new("worktrees.gitPath", new TextRule.AbsolutePathOrEmpty())
        {
            Trust = new(SafeDirection.None, MachineOnly: true),
        };

        /// <summary>One git question may run this long before its tree is killed. Default 30.</summary>
        public static readonly ConfigKey.IntKey TimeoutSeconds = new("worktrees.gitTimeoutSeconds", 5, 600);

        /// <summary>At most this many names ride one <c>check-ignore</c> / <c>ls-files</c> call. Default 64.</summary>
        public static readonly ConfigKey.IntKey NamesPerCall = new("worktrees.gitNamesPerCall", 1, 256);

        /// <summary>The whole encoded command line of one call stays under this many characters (Windows' own limit is 8 191 for
        /// <c>cmd</c> and 32 767 for <c>CreateProcess</c>). Default 7000.</summary>
        public static readonly ConfigKey.IntKey CommandLineMaxChars = new("worktrees.gitCommandLineMaxChars", 1000, 30000);
    }
}
