using System.Net;
using System.Net.Sockets;
using System.Text;
using BambuBar.Models;
using BambuBar.Services;

namespace BambuBar.Protocol;

/// <summary>A printer that announced itself on the local network.</summary>
public sealed record DiscoveredPrinter
{
    public string Serial { get; init; } = "";
    public string Host { get; init; } = "";
    public string Name { get; init; } = "";
    public string ModelCode { get; init; } = "";
    public string? Firmware { get; init; }
    /// <summary>"lan" (LAN Only mode) or "cloud".</summary>
    public string? ConnectMode { get; init; }
    public DateTime LastSeen { get; init; }

    public string ModelName => PrinterModel.NameFor(ModelCode, Serial);
}

/// <summary>
/// Listens for the SSDP-style NOTIFY packets Bambu printers broadcast on UDP 2021 (and answers to an M-SEARCH),
/// so the printer can be picked from a list and its IP address followed when DHCP changes it.
/// </summary>
public sealed class PrinterDiscovery
{
    /// <summary>Raised on the main thread.</summary>
    public event Action<DiscoveredPrinter>? Found;

    private readonly List<UdpClient> _sockets = [];
    private CancellationTokenSource? _cancel;
    private readonly int[] _extraPorts;

    /// <param name="extraPorts">The mock fleet announces itself on a port of its own (scripts/fleet), so the copy of
    /// Bambu Bar in daily use, also bound to 2021, doesn't take its unicast packets.</param>
    public PrinterDiscovery(params int[] extraPorts) => _extraPorts = extraPorts;

    public bool IsRunning => _cancel is not null;

    public void Start()
    {
        if (_cancel is not null) return;
        _cancel = new CancellationTokenSource();
        foreach (var port in new[] { 2021, 1990 }.Concat(_extraPorts))
        {
            if (OpenSocket(port) is not { } socket) continue;
            _sockets.Add(socket);
            var token = _cancel.Token;
            _ = Task.Run(() => ListenAsync(socket, token));
        }
        Search();
    }

    public void Stop()
    {
        _cancel?.Cancel();
        _cancel = null;
        foreach (var socket in _sockets) socket.Dispose();
        _sockets.Clear();
    }

    /// <summary>Asks every printer to answer now instead of waiting for its next broadcast (they announce every ~30 s).</summary>
    public void Search()
    {
        // Sent from a listening socket: printers answer unicast to the port the search came from.
        if (_sockets.FirstOrDefault() is not { } socket) return;
        const string message = "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1990\r\nMAN: \"ssdp:discover\"\r\nMX: 3\r\nST: urn:bambulab-com:device:3dprinter:1\r\n\r\n";
        var bytes = Encoding.ASCII.GetBytes(message);
        _ = Task.Run(async () =>
        {
            foreach (var (address, port) in new[] { ("239.255.255.250", 2021), ("239.255.255.250", 1990), ("255.255.255.255", 2021) })
            {
                try { await socket.SendAsync(bytes, new IPEndPoint(IPAddress.Parse(address), port)).ConfigureAwait(false); }
                catch (SocketException) { }
                catch (ObjectDisposedException) { return; }
            }
        });
    }

    private static UdpClient? OpenSocket(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            // Share the port with Bambu Studio and other listeners.
            socket.ExclusiveAddressUse = false;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.EnableBroadcast = true;
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            try
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                    new MulticastOption(IPAddress.Parse("239.255.255.250"), IPAddress.Any));
            }
            catch (SocketException) { /* still hears broadcasts and unicast answers */ }
            return new UdpClient { Client = socket };
        }
        catch (SocketException)
        {
            socket.Dispose();
            return null;
        }
    }

    private async Task ListenAsync(UdpClient socket, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
            {
                // Windows reports an ICMP port-unreachable from an earlier send as a reset on the next receive.
                continue;
            }
            catch (SocketException) { return; }
            var text = Encoding.UTF8.GetString(result.Buffer);
            if (Parse(text, result.RemoteEndPoint.Address.ToString()) is { } printer)
                MainThread.Post(() => Found?.Invoke(printer));
        }
    }

    public static DiscoveredPrinter? Parse(string text, string sender)
    {
        if (!text.StartsWith("NOTIFY", StringComparison.Ordinal) && !text.StartsWith("HTTP/1.1 200", StringComparison.Ordinal)) return null;
        var headers = new Dictionary<string, string>();
        foreach (var line in text.Split("\r\n").Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
        }
        var isBambu = (headers.TryGetValue("nt", out var nt) && nt.Contains("bambulab"))
                      || (headers.TryGetValue("st", out var st) && st.Contains("bambulab"));
        if (!isBambu || !headers.TryGetValue("usn", out var serial) || serial.Length == 0) return null;
        var host = headers.TryGetValue("location", out var location) && location.Length > 0 ? location : sender;
        return new DiscoveredPrinter
        {
            Serial = serial, Host = host, Name = headers.GetValueOrDefault("devname.bambu.com") ?? serial,
            ModelCode = headers.GetValueOrDefault("devmodel.bambu.com") ?? "", Firmware = headers.GetValueOrDefault("devversion.bambu.com"),
            ConnectMode = headers.GetValueOrDefault("devconnect.bambu.com"), LastSeen = DateTime.UtcNow,
        };
    }
}
