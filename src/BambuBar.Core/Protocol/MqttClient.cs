using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;

namespace BambuBar.Protocol;

/// <summary>
/// A small MQTT 3.1.1 client over TLS 1.2, just what a Bambu printer's broker needs:
/// CONNECT with username/password, SUBSCRIBE, QoS 0 PUBLISH both ways, keepalive pings.
///
/// Network work happens on the thread pool; events are handed to <see cref="Deliver"/>, which the owner points at
/// its own thread (the app's main thread).
/// </summary>
public sealed class MqttClient
{
    public sealed record Configuration
    {
        public required string Host { get; init; }
        public int Port { get; init; } = 8883;
        public string Username { get; init; } = "bblp";
        public required string Password { get; init; }
        /// <summary>The printer serial, which its certificate is named after.</summary>
        public required string Serial { get; init; }
        public int KeepAlive { get; init; } = 30;
        public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(12);
        /// <summary>Accept any certificate (the local printer emulator only).</summary>
        public bool Insecure { get; init; }
    }

    public abstract record Failure
    {
        /// <summary>CONNACK return code: 4 bad username/password, 5 not authorised.</summary>
        public sealed record Refused(byte Code) : Failure;
        public sealed record Certificate(string Reason) : Failure;
        public sealed record Unreachable(string Reason) : Failure;
        public sealed record TimedOut : Failure;
        public sealed record Closed : Failure;
        public sealed record Malformed : Failure;

        public bool IsAuthentication => this is Refused { Code: 4 or 5 };
    }

    public abstract record Event
    {
        public sealed record Connected : Event;
        public sealed record Message(string Topic, byte[] Payload) : Event;
        public sealed record Disconnected(Failure Failure) : Event;
    }

    /// <summary>Receives events in order, on whatever thread <see cref="Deliver"/> chooses.</summary>
    public Action<Event>? OnEvent { get; set; }

    /// <summary>How events reach the owner. Defaults to calling straight from the network thread.</summary>
    public Action<Action> Deliver { get; set; } = action => action();

    private readonly Configuration _config;
    private readonly object _lock = new();
    private readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cancel = new();
    private TcpClient? _tcp;
    private SslStream? _stream;
    private ushort _nextPacketId = 1;
    private bool _isOpen;
    private bool _finished;
    private Timer? _pingTimer;
    private long _lastInboundTicks = Environment.TickCount64;
    private BambuTrust.Verdict? _trustVerdict;

    public MqttClient(Configuration config) => _config = config;

    public void Connect() => _ = Task.Run(RunAsync);

    public void Subscribe(string topic)
    {
        var body = new List<byte>();
        AppendUInt16(body, AllocatePacketId());
        AppendString(body, topic);
        body.Add(0); // QoS 0
        Send(0x82, body);
    }

    public void Publish(string topic, byte[] payload)
    {
        var body = new List<byte>(payload.Length + topic.Length + 2);
        AppendString(body, topic);
        body.AddRange(payload);
        Send(0x30, body);
    }

    public void Disconnect()
    {
        bool open;
        lock (_lock) open = _isOpen;
        if (open) Send(0xE0, []);
        // Let the DISCONNECT go out before the socket closes.
        _ = Task.Delay(50).ContinueWith(_ => Finish(new Failure.Closed(), notify: false), TaskScheduler.Default);
    }

    // MARK: - Connection

    private async Task RunAsync()
    {
        var token = _cancel.Token;
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            connectCts.CancelAfter(_config.ConnectTimeout + TimeSpan.FromSeconds(4));

            var tcp = new TcpClient { NoDelay = true };
            lock (_lock)
            {
                if (_finished) { tcp.Dispose(); return; }
                _tcp = tcp;
            }
            ConfigureKeepAlive(tcp.Client);
            using (var tcpTimeout = CancellationTokenSource.CreateLinkedTokenSource(connectCts.Token))
            {
                tcpTimeout.CancelAfter(_config.ConnectTimeout);
                try
                {
                    await tcp.ConnectAsync(_config.Host, _config.Port, tcpTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    Finish(new Failure.Unreachable("The printer didn't answer."));
                    return;
                }
            }

            var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, ValidateCertificate);
            lock (_lock) _stream = ssl;
            var options = new SslClientAuthenticationOptions
            {
                TargetHost = _config.Host,
                // P2S firmware never answers a TLS 1.3 ClientHello.
                EnabledSslProtocols = SslProtocols.Tls12,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            };
            try
            {
                await ssl.AuthenticateAsClientAsync(options, connectCts.Token).ConfigureAwait(false);
            }
            catch (Exception) when (_trustVerdict is BambuTrust.Verdict.Rejected rejected)
            {
                Finish(new Failure.Certificate(rejected.Reason));
                return;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                Finish(new Failure.TimedOut());
                return;
            }

            _ = Task.Run(() => WriteLoopAsync(ssl, token));
            SendConnect();
            var connectDeadline = new Timer(_ =>
            {
                bool open;
                lock (_lock) open = _isOpen;
                if (!open) Finish(new Failure.TimedOut());
            }, null, _config.ConnectTimeout + TimeSpan.FromSeconds(4), Timeout.InfiniteTimeSpan);
            try
            {
                await ReadLoopAsync(ssl, token).ConfigureAwait(false);
            }
            finally
            {
                connectDeadline.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            Finish(new Failure.Closed());
        }
        catch (SocketException e)
        {
            Finish(new Failure.Unreachable(Describe(e)));
        }
        catch (IOException e) when (e.InnerException is SocketException se)
        {
            Finish(new Failure.Unreachable(Describe(se)));
        }
        catch (AuthenticationException e)
        {
            Finish(new Failure.Unreachable(e.Message));
        }
        catch (ObjectDisposedException)
        {
            Finish(new Failure.Closed());
        }
        catch (IOException)
        {
            Finish(new Failure.Closed());
        }
        catch (Exception e)
        {
            Finish(new Failure.Unreachable(e.Message));
        }
    }

    private bool ValidateCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (_config.Insecure) return true;
        var verdict = BambuTrust.Evaluate(certificate, chain, _config.Serial);
        _trustVerdict = verdict;
        return verdict is BambuTrust.Verdict.Trusted;
    }

    private static void ConfigureKeepAlive(Socket socket)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 20);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
        }
        catch (SocketException) { }
        catch (PlatformNotSupportedException) { }
    }

    internal static string Describe(SocketException e) => e.SocketErrorCode switch
    {
        SocketError.ConnectionRefused => "The printer refused the connection.",
        SocketError.HostUnreachable or SocketError.HostDown or SocketError.HostNotFound => "The printer isn't reachable on the network.",
        SocketError.NetworkDown or SocketError.NetworkUnreachable => "This PC isn't on a network.",
        SocketError.TimedOut => "The printer didn't answer.",
        SocketError.ConnectionReset or SocketError.ConnectionAborted => "The connection to the printer closed.",
        _ => e.Message,
    };

    private void SendConnect()
    {
        var body = new List<byte>();
        AppendString(body, "MQTT");
        body.Add(4);    // protocol level 3.1.1
        body.Add(0xC2); // username, password, clean session
        AppendUInt16(body, (ushort)_config.KeepAlive);
        AppendString(body, "bambubar-" + Guid.NewGuid().ToString("N")[..12]);
        AppendString(body, _config.Username);
        AppendString(body, _config.Password);
        Send(0x10, body);
    }

    private async Task ReadLoopAsync(SslStream stream, CancellationToken token)
    {
        var chunk = new byte[256 * 1024];
        var buffer = new List<byte>(64 * 1024);
        while (!token.IsCancellationRequested)
        {
            int n = await stream.ReadAsync(chunk, token).ConfigureAwait(false);
            if (n == 0)
            {
                Finish(new Failure.Closed());
                return;
            }
            Interlocked.Exchange(ref _lastInboundTicks, Environment.TickCount64);
            buffer.AddRange(new ArraySegment<byte>(chunk, 0, n));
            if (!DrainPackets(buffer)) return;
        }
    }

    /// <summary>Handles every complete packet in <paramref name="buffer"/>; false once the session is over.</summary>
    private bool DrainPackets(List<byte> buffer)
    {
        while (true)
        {
            lock (_lock) if (_finished) return false;
            if (buffer.Count < 2) return true;
            // Remaining length: up to four 7-bit groups.
            int multiplier = 1, length = 0, index = 1;
            var done = false;
            for (var i = 0; i < 4; i++)
            {
                if (index >= buffer.Count) return true;
                int b = buffer[index];
                length += (b & 0x7F) * multiplier;
                multiplier *= 128;
                index++;
                if ((b & 0x80) == 0) { done = true; break; }
            }
            if (!done)
            {
                Finish(new Failure.Malformed());
                return false;
            }
            if (buffer.Count - index < length) return true;
            var header = buffer[0];
            var body = buffer.GetRange(index, length).ToArray();
            buffer.RemoveRange(0, index + length);
            HandlePacket(header, body);
        }
    }

    private void HandlePacket(byte header, byte[] body)
    {
        switch (header >> 4)
        {
            case 2: // CONNACK
                if (body.Length < 2) { Finish(new Failure.Malformed()); return; }
                var code = body[1];
                if (code != 0) { Finish(new Failure.Refused(code)); return; }
                lock (_lock) _isOpen = true;
                StartPings();
                Emit(new Event.Connected());
                break;
            case 3: // PUBLISH
                var qos = (header >> 1) & 0x03;
                if (body.Length < 2) return;
                var topicLength = body[0] << 8 | body[1];
                var offset = 2 + topicLength;
                if (offset > body.Length) return;
                var topic = Encoding.UTF8.GetString(body, 2, topicLength);
                if (qos > 0)
                {
                    if (offset + 2 > body.Length) return;
                    var id = new[] { body[offset], body[offset + 1] };
                    offset += 2;
                    if (qos == 1) Send(0x40, [.. id]); // PUBACK
                }
                Emit(new Event.Message(topic, body[offset..]));
                break;
            default:
                break; // SUBACK, PINGRESP, PUBACK
        }
    }

    private void StartPings()
    {
        var interval = TimeSpan.FromSeconds(_config.KeepAlive);
        _pingTimer = new Timer(_ =>
        {
            var silence = TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastInboundTicks));
            if (silence > interval * 1.5 + TimeSpan.FromSeconds(5)) Finish(new Failure.TimedOut());
            else Send(0xC0, []);
        }, null, interval, interval);
    }

    private void Send(byte type, List<byte> body)
    {
        lock (_lock) if (_finished) return;
        var packet = new List<byte>(body.Count + 5) { type };
        var length = body.Count;
        do
        {
            var b = (byte)(length % 128);
            length /= 128;
            if (length > 0) b |= 0x80;
            packet.Add(b);
        } while (length > 0);
        packet.AddRange(body);
        _outbox.Writer.TryWrite(packet.ToArray());
    }

    private async Task WriteLoopAsync(SslStream stream, CancellationToken token)
    {
        try
        {
            await foreach (var packet in _outbox.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                await stream.WriteAsync(packet, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (IOException e) when (e.InnerException is SocketException se) { Finish(new Failure.Unreachable(Describe(se))); }
        catch (Exception e) { Finish(new Failure.Unreachable(e.Message)); }
    }

    private ushort AllocatePacketId()
    {
        lock (_lock)
        {
            var id = _nextPacketId;
            _nextPacketId = _nextPacketId == ushort.MaxValue ? (ushort)1 : (ushort)(_nextPacketId + 1);
            return id;
        }
    }

    private void Finish(Failure failure, bool notify = true)
    {
        lock (_lock)
        {
            if (_finished) return;
            _finished = true;
            _isOpen = false;
        }
        _pingTimer?.Dispose();
        _outbox.Writer.TryComplete();
        _cancel.Cancel();
        try { _stream?.Dispose(); } catch { /* closing anyway */ }
        try { _tcp?.Dispose(); } catch { /* closing anyway */ }
        if (notify) Emit(new Event.Disconnected(failure));
    }

    private void Emit(Event e) => Deliver(() => OnEvent?.Invoke(e));

    private static void AppendUInt16(List<byte> data, ushort value)
    {
        data.Add((byte)(value >> 8));
        data.Add((byte)(value & 0xFF));
    }

    private static void AppendString(List<byte> data, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        AppendUInt16(data, (ushort)bytes.Length);
        data.AddRange(bytes);
    }
}
