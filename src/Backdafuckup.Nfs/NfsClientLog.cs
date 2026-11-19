using System.Collections.Concurrent;
using System.Net;

namespace Backdafuckup.Nfs;

/// <summary>One local port a client reached.</summary>
public sealed record NfsPortContact(int Port, string Transport, DateTimeOffset First, DateTimeOffset Last, long Count);

/// <summary>A client of the NFS server: the ports it reached and the exports it was refused.</summary>
public sealed record NfsClientEntry(IPAddress Address, IReadOnlyList<NfsPortContact> Contacts, IReadOnlyList<(string Path, DateTimeOffset At)> Refusals);

/// <summary>
/// Which hosts reached which ports of the NFS server, and which were refused an export. When a hypervisor fails to mount,
/// this tells a firewall in between (the host never arrived, or arrived only at the portmapper) from a refused allowlist.
/// Bounded: the hosts seen longest ago are forgotten first.
/// </summary>
public sealed class NfsClientLog(TimeProvider time, int maxHosts = 1024)
{
    private readonly ConcurrentDictionary<IPAddress, Host> _hosts = new();

    private sealed class Host
    {
        public readonly Lock Gate = new();
        public readonly Dictionary<(int Port, string Transport), NfsPortContact> Contacts = [];
        public readonly List<(string Path, DateTimeOffset At)> Refusals = [];
        public DateTimeOffset LastSeen;
    }

    /// <summary>Records a connection (TCP) or a datagram (UDP); true when this host reached this port for the first time.</summary>
    public bool Record(IPAddress address, int localPort, string transport)
    {
        ArgumentNullException.ThrowIfNull(address);
        var now = time.GetUtcNow();
        var host = Get(address);
        lock (host.Gate)
        {
            host.LastSeen = now;
            var key = (localPort, transport);
            var first = !host.Contacts.TryGetValue(key, out var contact);
            host.Contacts[key] = first ? new NfsPortContact(localPort, transport, now, now, 1) : contact! with { Last = now, Count = contact.Count + 1 };
            return first;
        }
    }

    public void RecordRefusal(IPAddress address, string path)
    {
        ArgumentNullException.ThrowIfNull(address);
        var now = time.GetUtcNow();
        var host = Get(address);
        lock (host.Gate)
        {
            host.LastSeen = now;
            host.Refusals.Add((path, now));
            if (host.Refusals.Count > 20)
            {
                host.Refusals.RemoveAt(0);
            }
        }
    }

    public IReadOnlyList<NfsClientEntry> Clients =>
        [.. _hosts.OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal).Select(kv => Snapshot(kv.Key, kv.Value))];

    public NfsClientEntry? Find(IPAddress address) => _hosts.TryGetValue(Normalize(address), out var host) ? Snapshot(Normalize(address), host) : null;

    /// <summary>
    /// What <paramref name="address"/> reached of the <paramref name="expected"/> ports, as a sentence for an error message:
    /// e.g. "10.26.1.5 reached 111/tcp (portmapper) and 20048/tcp (MOUNT), but never 2049/tcp (NFS)".
    /// </summary>
    public string Describe(IPAddress address, IReadOnlyList<(int Port, string Transport, string Role)> expected)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(expected);
        var entry = Find(address);
        var who = Normalize(address).ToString();
        if (entry is null || entry.Contacts.Count == 0)
        {
            var ports = string.Join(", ", expected.Select(e => $"{e.Port}/{e.Transport} ({e.Role})"));
            var refused = entry?.Refusals.Count > 0 ? $"; it was refused {Refusals(entry)}" : string.Empty;
            return $"{who} has not reached this server on any of {ports}{refused}: a firewall between them, or the host uses another of its addresses";
        }

        string Name((int Port, string Transport, string Role) e) => $"{e.Port}/{e.Transport} ({e.Role})";
        var reached = expected.Where(e => entry.Contacts.Any(c => c.Port == e.Port && c.Transport == e.Transport)).Select(Name).ToList();
        var missing = expected.Where(e => !entry.Contacts.Any(c => c.Port == e.Port && c.Transport == e.Transport)).Select(Name).ToList();
        var text = reached.Count > 0 ? $"{who} reached {string.Join(", ", reached)}" : $"{who} reached none of the expected ports";
        if (missing.Count > 0)
        {
            text += $", but never {string.Join(", ", missing)}";
        }

        if (entry.Refusals.Count > 0)
        {
            text += $"; it was refused {Refusals(entry)}";
        }

        return text;
    }

    private static string Refusals(NfsClientEntry entry) =>
        string.Join(", ", entry.Refusals.Select(r => r.Path).Distinct(StringComparer.Ordinal)) + " (not in the export's allowlist)";

    private Host Get(IPAddress address)
    {
        address = Normalize(address);
        if (_hosts.TryGetValue(address, out var host))
        {
            return host;
        }

        if (_hosts.Count >= maxHosts)
        {
            var oldest = _hosts.OrderBy(kv => kv.Value.LastSeen).FirstOrDefault();
            if (oldest.Key is not null)
            {
                _hosts.TryRemove(oldest.Key, out _);
            }
        }

        return _hosts.GetOrAdd(address, _ => new Host());
    }

    private static NfsClientEntry Snapshot(IPAddress address, Host host)
    {
        lock (host.Gate)
        {
            return new NfsClientEntry(address, [.. host.Contacts.Values.OrderBy(c => c.Port).ThenBy(c => c.Transport, StringComparer.Ordinal)], [.. host.Refusals]);
        }
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
