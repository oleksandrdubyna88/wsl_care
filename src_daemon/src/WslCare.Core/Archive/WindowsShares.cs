using System.Net;
using System.Net.NetworkInformation;

namespace WslCare.Core.Archive;

/// <summary>
/// E9.S0 review round S3, E9.S1 review round m2: the shares that reach this machine's own folders under another spelling — the
/// distribution's files (<c>\\wsl$</c>, <c>\\wsl.localhost</c>); a loopback name (<c>localhost</c> with or without its trailing dot,
/// any <c>127.*</c>, <c>::1</c> in every spelling, its <c>ipv6-literal.net</c> forms); this machine's NetBIOS name or DNS host name,
/// bare or qualified; an address of one of this machine's own interfaces; an administrative share (<c>C$</c>, <c>ADMIN$</c>,
/// <c>IPC$</c>). The overlap rule compares spellings and identities of THIS machine's folders; a share back to them would slip past
/// both, so it is refused by its name. A <c>/</c> is read as the <c>\</c> Windows reads it as.
/// <para>Residual: a DNS alias of this machine (a CNAME, an entry in the hosts file) is not resolved — no name is looked up.</para>
/// </summary>
public static class WindowsShares
{
    private const string Ipv6Literal = ".ipv6-literal.net";

    private static readonly string[] DistroServers = ["wsl$", "wsl.localhost"];

    private static readonly string[] Loopback = ["localhost", "."];

    private static readonly string[] AdministrativeShares = ["ADMIN$", "IPC$"];

    /// <summary>Why <paramref name="given"/> is a share this rule refuses; empty when it is none.</summary>
    public static string Alias(string given) =>
        given.Replace('/', '\\') is var spelled && spelled.StartsWith(@"\\", StringComparison.Ordinal) && spelled[2..].Split('\\') is [var server, var share, ..]
            ? Why(given, server, share)
            : string.Empty;

    private static string Why(string given, string server, string share) =>
        DistroServers.Contains(server, StringComparer.OrdinalIgnoreCase) ? $"{given} is the distribution's own files; name the folder inside the distribution, where its own process judges it"
        : IsThisMachine(server.TrimEnd('.')) ? $"{given} names this machine ({server}); name the folder by its drive, so it is judged as the folder it is"
        : IsAdministrative(share) ? $"{given} is an administrative share ({share}), a second spelling of a whole drive; name the folder by a share of its own or by its drive"
        : string.Empty;

    private static bool IsThisMachine(string server) =>
        Loopback.Contains(server, StringComparer.OrdinalIgnoreCase) || IsMachineName(server) || AddressOf(server) is [var address] && IsOwnAddress(address);

    /// <summary>This machine's NetBIOS name or DNS host name, bare or with a domain after it.</summary>
    private static bool IsMachineName(string server) =>
        new[] { Environment.MachineName, Dns.GetHostName() }.Any(name => name.Length > 0 && (server.Equals(name, StringComparison.OrdinalIgnoreCase) || server.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase)));

    /// <summary>The address <paramref name="server"/> spells: a literal (<c>127.0.0.2</c>, <c>[::1]</c>) or an <c>ipv6-literal.net</c> name
    /// (<c>0--1</c>, <c>0-0-0-0-0-0-0-1</c>: the dashes are colons, an <c>s</c> the zone mark); nothing when it is a name.</summary>
    private static IPAddress[] AddressOf(string server)
    {
        var literal = server.EndsWith(Ipv6Literal, StringComparison.OrdinalIgnoreCase)
            ? server[..^Ipv6Literal.Length].Replace('-', ':').Replace('s', '%')
            : server.Trim('[', ']');
        return IPAddress.TryParse(literal, out var address) ? [address] : [];
    }

    private static bool IsOwnAddress(IPAddress address) => IPAddress.IsLoopback(address) || OwnAddresses().Contains(address);

    /// <summary>The unicast addresses of this machine's interfaces — read from the interfaces, never looked up; none when they cannot
    /// be read.</summary>
    private static IReadOnlyList<IPAddress> OwnAddresses()
    {
        try
        {
            return [.. NetworkInterface.GetAllNetworkInterfaces().SelectMany(i => i.GetIPProperties().UnicastAddresses).Select(u => u.Address)];
        }
        catch (Exception e) when (e is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
        {
            return [];
        }
    }

    private static bool IsAdministrative(string share) => IsDriveShare(share) || AdministrativeShares.Contains(share, StringComparer.OrdinalIgnoreCase);

    /// <summary><c>C$</c>: a whole drive, shared by Windows itself.</summary>
    private static bool IsDriveShare(string share) => share.Length == 2 && char.IsAsciiLetter(share[0]) && share[1] == '$';
}
