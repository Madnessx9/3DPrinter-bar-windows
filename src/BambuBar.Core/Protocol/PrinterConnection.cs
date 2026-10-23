using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BambuBar.Models;
using BambuBar.Services;

namespace BambuBar.Protocol;

/// <summary>Where the connection stands.</summary>
public abstract record ConnectionPhase
{
    public sealed record Idle : ConnectionPhase;
    public sealed record Connecting : ConnectionPhase;
    public sealed record Online : ConnectionPhase;
    public sealed record Offline(string Reason) : ConnectionPhase;
    public sealed record Unauthorized : ConnectionPhase;
    public sealed record CertificateRejected(string Reason) : ConnectionPhase;

    public static readonly ConnectionPhase IdlePhase = new Idle();
    public static readonly ConnectionPhase ConnectingPhase = new Connecting();
    public static readonly ConnectionPhase OnlinePhase = new Online();
}

/// <summary>
/// Keeps one MQTT session to the printer alive: connects, re-requests a full report, merges the deltas
/// into a status, and reconnects with back-off when the printer drops off the network or the PC sleeps.
/// Lives on the main thread.
/// </summary>
public sealed class PrinterConnection : IDisposable
{
    public event Action<ConnectionPhase>? PhaseChanged;
    public event Action<PrinterStatus, ReportDocument>? StatusChanged;
    /// <summary>Any message from the printer, changed or not.</summary>
    public event Action? Heartbeat;
    /// <summary>A command acknowledgement: command name, success, reason.</summary>
    public event Action<string, bool, string?>? Acknowledged;

    private ConnectionPhase _phase = ConnectionPhase.IdlePhase;
    public ConnectionPhase Phase
    {
        get => _phase;
        private set
        {
            if (_phase == value) return;
            _phase = value;
            PhaseChanged?.Invoke(value);
        }
    }

    private PrinterConfig? _config;
    private string _accessCode = "";
    private bool _insecure;
    private MqttClient? _client;
    private readonly ReportDocument _document = new();
    private PrinterStatus? _lastStatus;
    private DateTime _lastMessage = DateTime.MinValue;
    private DateTime _lastPushall = DateTime.MinValue;
    private int _attempt;
    private MainTimer? _retry;
    private MainTimer? _watchdog;
    private int _sequence;

    public PrinterConnection()
    {
        PowerEvents.DidWake += OnWake;
        PowerEvents.WillSleep += Suspend;
    }

    public void Dispose()
    {
        PowerEvents.DidWake -= OnWake;
        PowerEvents.WillSleep -= Suspend;
        Stop();
    }

    private void OnWake() => ReconnectSoon(TimeSpan.FromSeconds(2));

    public void Start(PrinterConfig config, string accessCode, bool insecure = false)
    {
        Stop();
        _config = config;
        _accessCode = accessCode;
        _insecure = insecure;
        _attempt = 0;
        Open();
        _watchdog = MainTimer.Every(TimeSpan.FromSeconds(10), CheckHealth);
    }

    public void Stop()
    {
        _retry?.Dispose();
        _watchdog?.Dispose();
        _retry = null;
        _watchdog = null;
        CloseClient();
        _config = null;
        _lastStatus = null;
        _document.Reset();
        Phase = ConnectionPhase.IdlePhase;
    }

    /// <summary>Asks for a full report now (after wake, or when the person clicks refresh).</summary>
    public void Refresh()
    {
        if (Phase is ConnectionPhase.Online) RequestFullReport();
        else if (_config is not null) ReconnectSoon(TimeSpan.Zero);
    }

    // MARK: - Commands

    public void Pause() => SendPrint(new JsonObject { ["command"] = "pause" });
    public void Resume() => SendPrint(new JsonObject { ["command"] = "resume" });
    public void StopPrint() => SendPrint(new JsonObject { ["command"] = "stop" });
    public void SetSpeed(SpeedLevel level) => SendPrint(new JsonObject { ["command"] = "print_speed", ["param"] = ((int)level).ToString() });

    public void SetChamberLight(bool on) => Send(new JsonObject
    {
        ["system"] = new JsonObject
        {
            ["command"] = "ledctrl", ["led_node"] = "chamber_light", ["led_mode"] = on ? "on" : "off",
            ["led_on_time"] = 500, ["led_off_time"] = 500, ["loop_times"] = 0, ["interval_time"] = 0,
        },
    }, "system");

    private void SendPrint(JsonObject body) => Send(new JsonObject { ["print"] = body }, "print");

    private void Send(JsonObject message, string key)
    {
        if (_config is null || _client is null || Phase is not ConnectionPhase.Online) return;
        _sequence++;
        if (message[key] is JsonObject body) body["sequence_id"] = _sequence.ToString();
        _client.Publish($"device/{_config.Serial}/request", Encoding.UTF8.GetBytes(message.ToJsonString()));
    }

    // MARK: - Session

    private void Open()
    {
        if (_config is null) return;
        CloseClient();
        Phase = ConnectionPhase.ConnectingPhase;
        // The watchdog measures silence from here, so it doesn't cut off an attempt that's still connecting.
        _lastMessage = DateTime.UtcNow;
        var (host, port) = _config.Endpoint;
        var client = new MqttClient(new MqttClient.Configuration
        {
            Host = host, Port = port, Password = _accessCode, Serial = _config.Serial, Insecure = _insecure,
        });
        client.Deliver = MainThread.Post;
        client.OnEvent = e =>
        {
            if (!ReferenceEquals(client, _client)) return;
            Handle(e);
        };
        _client = client;
        client.Connect();
    }

    private void CloseClient()
    {
        if (_client is null) return;
        _client.OnEvent = null;
        _client.Disconnect();
        _client = null;
    }

    private void Handle(MqttClient.Event e)
    {
        if (_config is null) return;
        switch (e)
        {
            case MqttClient.Event.Connected:
                _lastMessage = DateTime.UtcNow;
                _client?.Subscribe($"device/{_config.Serial}/report");
                SendRaw(new JsonObject { ["info"] = new JsonObject { ["sequence_id"] = "0", ["command"] = "get_version" } });
                RequestFullReport();
                // Stay "connecting" until the first report arrives, so a silent broker doesn't look online.
                break;

            case MqttClient.Event.Message message:
                _lastMessage = DateTime.UtcNow;
                Heartbeat?.Invoke();
                if (Decode(message.Payload) is not JsonObject json) return;
                if (json["print"] is JsonObject p && J.String(p["command"]) is { } command && command != "push_status"
                    && J.String(p["result"]) is { } result)
                    Acknowledged?.Invoke(command, result.Equals("success", StringComparison.OrdinalIgnoreCase), J.String(p["reason"]));
                if (!_document.Apply(json)) return;
                if (!_document.HasFullReport && !_document.Print.ContainsKey("gcode_state")) return;
                if (Phase is not ConnectionPhase.Online)
                {
                    // Back-off resets only once the printer is really talking, not on a CONNACK alone.
                    _attempt = 0;
                    Phase = ConnectionPhase.OnlinePhase;
                }
                PublishStatus();
                break;

            case MqttClient.Event.Disconnected disconnected:
                _client = null;
                switch (disconnected.Failure)
                {
                    case MqttClient.Failure.Refused { Code: 4 or 5 }:
                        // A wrong access code won't fix itself; wait for the person to change it.
                        Phase = new ConnectionPhase.Unauthorized();
                        return;
                    case MqttClient.Failure.Certificate certificate:
                        // A certificate that doesn't check out won't fix itself either; don't keep sending it the code.
                        Phase = new ConnectionPhase.CertificateRejected(certificate.Reason);
                        return;
                    case MqttClient.Failure.Unreachable unreachable:
                        Phase = new ConnectionPhase.Offline(unreachable.Reason);
                        break;
                    case MqttClient.Failure.TimedOut:
                        Phase = new ConnectionPhase.Offline("The printer stopped answering.");
                        break;
                    default:
                        Phase = new ConnectionPhase.Offline("The connection to the printer closed.");
                        break;
                }
                ScheduleReconnect();
                break;
        }
    }

    private void PublishStatus()
    {
        var status = ReportParser.Status(_document, HmsCatalog.Shared);
        if (_lastStatus is { } last)
        {
            // Keep the timestamp the remaining time was measured at, so the ETA doesn't drift between reports.
            var same = status with { UpdatedAt = last.UpdatedAt };
            if (same == last) return;
            if (status.RemainingMinutes == last.RemainingMinutes && status.Progress == last.Progress && status.State == last.State)
                status.UpdatedAt = last.UpdatedAt;
        }
        _lastStatus = status;
        StatusChanged?.Invoke(status, _document);
    }

    private void RequestFullReport()
    {
        _lastPushall = DateTime.UtcNow;
        SendRaw(new JsonObject { ["pushing"] = new JsonObject { ["sequence_id"] = "0", ["command"] = "pushall" } });
    }

    private void SendRaw(JsonObject message)
    {
        if (_config is null || _client is null) return;
        _client.Publish($"device/{_config.Serial}/request", Encoding.UTF8.GetBytes(message.ToJsonString()));
    }

    private void CheckHealth()
    {
        if (_config is null) return;
        var silence = DateTime.UtcNow - _lastMessage;
        switch (Phase)
        {
            case ConnectionPhase.Online:
                if (silence > TimeSpan.FromSeconds(95))
                {
                    Phase = new ConnectionPhase.Offline("The printer stopped sending updates.");
                    ReconnectSoon(TimeSpan.Zero);
                }
                else if (silence > TimeSpan.FromSeconds(60) || DateTime.UtcNow - _lastPushall > TimeSpan.FromSeconds(300))
                {
                    RequestFullReport();
                }
                break;
            case ConnectionPhase.Connecting:
                if (_client is not null && silence > TimeSpan.FromSeconds(25)) ReconnectSoon(TimeSpan.Zero);
                break;
        }
    }

    private static readonly TimeSpan[] Delays =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];

    private void ScheduleReconnect()
    {
        var delay = Delays[Math.Min(_attempt, Delays.Length - 1)];
        _attempt++;
        ReconnectSoon(delay);
    }

    private void ReconnectSoon(TimeSpan delay)
    {
        if (_config is null) return;
        _retry?.Dispose();
        _retry = MainTimer.After(delay, Open);
    }

    private void Suspend()
    {
        _retry?.Dispose();
        _retry = null;
        CloseClient();
    }

    /// <summary>Some firmware sends bytes that aren't valid UTF-8; fall back to Latin-1 rather than drop the report.</summary>
    private static JsonObject? Decode(byte[] data)
    {
        try
        {
            return JsonNode.Parse(data) as JsonObject;
        }
        catch (JsonException)
        {
            try { return JsonNode.Parse(Encoding.Latin1.GetString(data)) as JsonObject; }
            catch (JsonException) { return null; }
        }
        catch (InvalidOperationException) { return null; }
    }
}
