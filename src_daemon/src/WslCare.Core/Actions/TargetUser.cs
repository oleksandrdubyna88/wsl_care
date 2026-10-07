using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Processes.Policy;

namespace WslCare.Core.Actions;

/// <summary>The account a user-scoped action works for: its name, uid and home (from <c>/etc/passwd</c>).</summary>
public sealed record TargetUser(string Name, int Uid, string Home);

/// <summary>What the discovery found — a closed set; only <see cref="Found"/> lets a user-scoped action run.</summary>
public abstract record TargetUserResult
{
    private TargetUserResult()
    {
    }

    /// <param name="Source">How it was found: <c>wsl.conf</c> or <c>single login account</c>.</param>
    public sealed record Found(TargetUser User, string Source) : TargetUserResult;

    /// <summary>More than one candidate, or a candidate that does not check out — every user-scoped action refuses.</summary>
    public sealed record Ambiguous(string Reason) : TargetUserResult;

    /// <summary>No candidate at all (and on the Windows side, no such notion yet).</summary>
    public sealed record None(string Reason) : TargetUserResult;

    /// <summary>Why a user-scoped action may not run; empty when a user was found.</summary>
    public string Refusal => this switch
    {
        Found => string.Empty,
        Ambiguous a => $"no target user: {a.Reason}",
        None n => $"no target user: {n.Reason}",
        _ => throw new System.Diagnostics.UnreachableException("TargetUserResult is a closed set"),
    };
}

/// <summary>
/// Whose home and tools a user-scoped action means (plan §15c #2), discovered ONCE per run: the <c>[user] default=</c> of
/// <c>/etc/wsl.conf</c>; else the single account with uid ≥ 1000 and a login shell in <c>/etc/passwd</c>; anything else
/// is ambiguous and every user-scoped action refuses with the reason (machine-scoped ones still run).
/// </summary>
/// <remarks>Conservative on every edge: an unreadable <c>wsl.conf</c> or <c>passwd</c>, a default user that is not in
/// <c>passwd</c> or whose name is not a valid account name, a home that is not absolute — all refuse rather than guess.</remarks>
public static class TargetUserDiscovery
{
    public const int FirstLoginUid = 1000;
    public const int NobodyUid = 65534;

    private static readonly string[] NoLoginShells = ["/usr/sbin/nologin", "/sbin/nologin", "/bin/false", "/usr/bin/false", "/bin/sync", "/usr/bin/nologin"];
    private static readonly SlotKind.UserName ValidName = new();

    /// <summary>One <c>/etc/passwd</c> entry, the fields the discovery reads.</summary>
    public sealed record Account(string Name, int Uid, string Home, string Shell)
    {
        public bool IsLoginAccount => Uid >= FirstLoginUid && Uid != NobodyUid && Shell.Length > 0 && !NoLoginShells.Contains(Shell);
    }

    public static TargetUserResult Discover(IFileSystem files, LinuxHostPaths paths)
    {
        var passwd = files.ReadFile(paths.PasswdFile);
        if (passwd is FileReadResult.Missing)
        {
            // No user database at all (a bare sandbox): no account, so no target — and no user's layer to miss (E3.S2).
            return new TargetUserResult.None($"{paths.PasswdFile} does not exist");
        }

        if (passwd is not FileReadResult.Content content)
        {
            return new TargetUserResult.Ambiguous($"{paths.PasswdFile} could not be read");
        }

        var accounts = Accounts(System.Text.Encoding.UTF8.GetString(content.Bytes));
        return files.ReadFile(paths.WslConfFile) switch
        {
            FileReadResult.Content wslConf => FromDefault(DefaultUser(System.Text.Encoding.UTF8.GetString(wslConf.Bytes)), accounts, paths),
            FileReadResult.Missing => FromSingleAccount(accounts),
            FileReadResult.Unreadable u => new TargetUserResult.Ambiguous($"{paths.WslConfFile} exists and could not be read ({u.Reason})"),
            _ => throw new System.Diagnostics.UnreachableException("FileReadResult is a closed set"),
        };
    }

    /// <summary>
    /// The homes of root and every login account, as THIS process sees them (under the sandbox root when sandboxed): the
    /// homes whose <c>~/git</c> and AI-agent folders the deletion policy protects besides <c>$HOME</c>'s — whoever the
    /// target user turns out to be (plan §15c #2). An unreadable <c>passwd</c> protects none further.
    /// </summary>
    public static IReadOnlyList<string> ProtectedHomes(IFileSystem files, LinuxHostPaths paths) =>
        files.ReadFile(paths.PasswdFile) is FileReadResult.Content content
            ? [.. Accounts(System.Text.Encoding.UTF8.GetString(content.Bytes))
                .Where(HasProtectedHome)
                .Select(a => paths.DistroPath(a.Home))
                .Distinct(StringComparer.Ordinal)]
            : [];

    /// <summary>Root or a login account, with an absolute home that is not <c>/</c> itself.</summary>
    private static bool HasProtectedHome(Account account) =>
        (account.IsLoginAccount || account.Uid == 0) && account.Home.StartsWith('/') && account.Home.Length > 1;

    /// <summary>Every well-formed line of a passwd file.</summary>
    public static IReadOnlyList<Account> Accounts(string passwd) =>
        [.. ProcText.Lines(passwd)
            .Select(l => l.Split(':'))
            .Where(f => f.Length >= 7 && int.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .Select(f => new Account(f[0], int.Parse(f[2], CultureInfo.InvariantCulture), f[5], f[6].Trim()))];

    /// <summary>The <c>default</c> key of the <c>[user]</c> section; empty when there is none.</summary>
    public static string DefaultUser(string wslConf) =>
        Entries(wslConf).Where(e => e.Section == "user" && e.Key == "default").Select(e => e.Value).FirstOrDefault() ?? string.Empty;

    /// <summary>Every <c>key = value</c> of an ini file with the section it stands in (lowercased; empty before the first).</summary>
    private static IEnumerable<(string Section, string Key, string Value)> Entries(string ini)
    {
        var section = string.Empty;
        foreach (var line in ProcText.Lines(ini).Select(l => l.Trim()))
        {
            section = SectionOf(line) ?? section;
            if (KeyValue(line) is (var key, var value))
            {
                yield return (section, key, value);
            }
        }
    }

    /// <summary>The name of a <c>[section]</c> header line, lowercased; <c>null</c> for any other line.</summary>
    private static string? SectionOf(string line) =>
        line.StartsWith('[') && line.EndsWith(']') ? line[1..^1].Trim().ToLowerInvariant() : null;

    private static (string Key, string Value)? KeyValue(string line)
    {
        var equals = line.IndexOf('=');
        return line.StartsWith('#') || line.StartsWith(';') || equals <= 0
            ? null
            : (line[..equals].Trim().ToLowerInvariant(), line[(equals + 1)..].Trim().Trim('"', '\''));
    }

    private static TargetUserResult FromDefault(string name, IReadOnlyList<Account> accounts, LinuxHostPaths paths)
    {
        if (name.Length == 0)
        {
            return FromSingleAccount(accounts);
        }

        var account = accounts.FirstOrDefault(a => a.Name == name);
        return !ValidName.Accepts(name) || account is null
            ? new TargetUserResult.Ambiguous($"{paths.WslConfFile} names the default user \"{name}\", which {UnusableDefault(name, paths)}")
            : Checked(account, "wsl.conf");
    }

    private static string UnusableDefault(string name, LinuxHostPaths paths) =>
        ValidName.Accepts(name) ? $"{paths.PasswdFile} does not hold" : "is not a valid account name";

    private static TargetUserResult FromSingleAccount(IReadOnlyList<Account> accounts)
    {
        var logins = accounts.Where(a => a.IsLoginAccount).ToList();
        return logins.Count switch
        {
            0 => new TargetUserResult.None($"no account with uid >= {FirstLoginUid} and a login shell, and no [user] default= in wsl.conf"),
            1 => Checked(logins[0], "single login account"),
            _ => new TargetUserResult.Ambiguous($"{logins.Count} login accounts ({string.Join(", ", logins.Select(a => a.Name))}) and no [user] default= in wsl.conf to choose one"),
        };
    }

    private static TargetUserResult Checked(Account account, string source) =>
        ValidName.Accepts(account.Name) && account.Home.StartsWith('/') && account.Home.Length > 1
            ? new TargetUserResult.Found(new TargetUser(account.Name, account.Uid, account.Home), source)
            : new TargetUserResult.Ambiguous($"the account \"{account.Name}\" has no usable name or home (home \"{account.Home}\")");
}

/// <summary>Whose home the per-user paths of THIS process follow (plan §15c #2, E3.S2) — a closed set.</summary>
public abstract record HomeOwner
{
    private HomeOwner()
    {
    }

    /// <summary><c>$HOME</c> of this process: it is not root (its home IS the user's), or there is no login account at all.</summary>
    public sealed record ThisProcess(string Why) : HomeOwner;

    /// <summary>Root, for the target user: the folder walks, the editor and cache roots and the user configuration layer
    /// are that user's.</summary>
    public sealed record Target(TargetUser User, string Source) : HomeOwner;

    /// <summary>Root, and the target user is ambiguous: whose configuration layer counts cannot be told, so the run reads the
    /// embedded defaults and the machine layer only — machine-scoped actions still run, and the engine's target-user gate
    /// refuses every user-scoped one with the reason (plan §15c #2, gate finding #2).</summary>
    public sealed record Unknown(string Reason) : HomeOwner;

    /// <summary>Why the user configuration layer is left out of this run; empty when it is read.</summary>
    public string UserLayerSkipped => this is Unknown u ? $"the user layer is not read: no single target user ({u.Reason}); user-scoped actions refuse and the timer runs no action until /etc/wsl.conf names one under [user] default=" : string.Empty;
}

/// <summary>
/// Plan §15c #2, closed in E3.S2: when this process is ROOT, every per-user path — the daily folder walk (A8 / A9's rows),
/// the roots of A12 / A14 / A17, and the user configuration layer — is the TARGET user's home, never root's <c>$HOME</c>.
/// Unprivileged, <c>$HOME</c> already is the user's.
/// </summary>
public static class TargetHome
{
    public static (LinuxHostPaths Paths, HomeOwner Owner) Resolve(LinuxHostPaths paths, IFileSystem files, bool privileged)
    {
        if (!privileged)
        {
            return (paths, new HomeOwner.ThisProcess("not root: $HOME is the user's own"));
        }

        return TargetUserDiscovery.Discover(files, paths) switch
        {
            TargetUserResult.Found found => (paths.WithHome(paths.DistroPath(found.User.Home)), new HomeOwner.Target(found.User, found.Source)),
            TargetUserResult.Ambiguous ambiguous => (paths, new HomeOwner.Unknown(ambiguous.Reason)),
            TargetUserResult.None none => (paths, new HomeOwner.ThisProcess($"root, and no target user ({none.Reason})")),
            _ => throw new System.Diagnostics.UnreachableException("TargetUserResult is a closed set"),
        };
    }
}
