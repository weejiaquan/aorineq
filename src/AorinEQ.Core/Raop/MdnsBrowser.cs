using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AorinEQ.Core.Raop;

/// <summary>Finds AirPlay receivers by asking for <c>_raop._tcp.local</c> over multicast DNS.
///
/// Queries EVERY up, non-loopback IPv4 interface rather than letting the OS pick one. This
/// machine carries Tailscale, Hyper-V and VirtualBox adapters alongside the real LAN, and a
/// single unbound socket reliably chooses the wrong one — observed directly while building
/// this, when a sender announced itself on a Tailscale address and the receiver had nowhere to
/// send audio.
///
/// Best-effort throughout, matching the rest of the audio layer: an interface that cannot be
/// joined is skipped, a malformed response contributes nothing, and the result is whatever was
/// actually found. Never throws.</summary>
public static class MdnsBrowser
{
    public const string RaopService = "_raop._tcp.local";

    private static readonly IPAddress MulticastGroup = IPAddress.Parse("224.0.0.251");
    private const int MulticastPort = 5353;

    public static async Task<IReadOnlyList<AirPlayDevice>> DiscoverAsync(
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var collected = new List<MdnsMessage>();
        var gate = new object();
        var sockets = new List<UdpClient>();

        foreach (var address in LocalIPv4Addresses())
        {
            try
            {
                var client = new UdpClient(AddressFamily.InterNetwork);
                client.Client.SetSocketOption(SocketOptionLevel.Socket,
                    SocketOptionName.ReuseAddress, true);
                client.Client.Bind(new IPEndPoint(address, 0));
                client.JoinMulticastGroup(MulticastGroup, address);
                client.MulticastLoopback = false;
                sockets.Add(client);
            }
            catch (SocketException)
            {
                // An interface that will not join is simply not searched.
            }
        }

        if (sockets.Count == 0)
            return [];

        using var expiry = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        expiry.CancelAfter(timeout);

        var query = MdnsRecords.BuildQuery(RaopService);
        var destination = new IPEndPoint(MulticastGroup, MulticastPort);
        var readers = new List<Task>();

        foreach (var socket in sockets)
        {
            try
            {
                await socket.SendAsync(query, query.Length, destination).ConfigureAwait(false);
            }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { continue; }

            readers.Add(Task.Run(async () =>
            {
                try
                {
                    while (!expiry.IsCancellationRequested)
                    {
                        var result = await socket.ReceiveAsync(expiry.Token).ConfigureAwait(false);
                        var parsed = MdnsRecords.ParseResponse(result.Buffer);
                        lock (gate) collected.Add(parsed);
                    }
                }
                catch (OperationCanceledException) { }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            }, expiry.Token));
        }

        try
        {
            await Task.WhenAll(readers).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }

        foreach (var socket in sockets)
        {
            try { socket.Dispose(); } catch (Exception) { /* closing a dead socket */ }
        }

        lock (gate) return Correlate(collected);
    }

    /// <summary>Stitches the four record types into devices.
    ///
    /// Responders scatter these across answer, authority and additional sections and across
    /// several packets, so everything received in the window is pooled before matching: a PTR
    /// names an instance, its SRV gives host and port, its TXT the capabilities, and an A
    /// record resolves the host to an address.</summary>
    private static List<AirPlayDevice> Correlate(List<MdnsMessage> messages)
    {
        var srv = new Dictionary<string, SrvRecord>(StringComparer.OrdinalIgnoreCase);
        var txt = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var addresses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var instances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var message in messages)
        {
            foreach (var p in message.Ptr)
                if (p.Name.StartsWith("_raop.", StringComparison.OrdinalIgnoreCase))
                    instances.Add(p.Target);
            foreach (var s in message.Srv) srv[s.Name] = s;
            foreach (var t in message.Txt) txt[t.Name] = t.Values;
            foreach (var a in message.A) addresses[a.Name] = a.Address;
        }

        // A responder may answer with SRV/TXT and no PTR when it replies to a direct query.
        foreach (var name in srv.Keys)
            if (name.EndsWith($".{RaopService}", StringComparison.OrdinalIgnoreCase))
                instances.Add(name);

        var devices = new List<AirPlayDevice>();
        foreach (var instance in instances)
        {
            if (!srv.TryGetValue(instance, out var service)) continue;
            if (!addresses.TryGetValue(service.Target, out var address)) continue;

            string shortName = instance.EndsWith($".{RaopService}", StringComparison.OrdinalIgnoreCase)
                ? instance[..^(RaopService.Length + 1)]
                : instance;

            devices.Add(new AirPlayDevice(
                shortName,
                service.Target,
                address,
                service.Port,
                txt.TryGetValue(instance, out var values)
                    ? values
                    : new Dictionary<string, string>()));
        }

        return devices.OrderBy(d => d.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static IEnumerable<IPAddress> LocalIPv4Addresses()
    {
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            yield break;
        }

        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (!nic.SupportsMulticast) continue;

            IPInterfaceProperties properties;
            try { properties = nic.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }

            foreach (var unicast in properties.UnicastAddresses)
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                    yield return unicast.Address;
        }
    }
}
