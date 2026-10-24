using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BambuBar.Models;
using BambuBar.Services;

namespace BambuBar.Protocol;

/// <summary>
/// Connects to a Klipper / Moonraker / Fluidd printer (such as the Snapmaker U1 running paxx12 Extended Firmware)
/// over Moonraker's WebSocket and HTTP JSON-RPC API. Lives on the main thread.
/// </summary>
public sealed class MoonrakerConnection : IDisposable
{
    public event Action<ConnectionPhase>? PhaseChanged;
    public event Action<PrinterStatus, bool>? StatusChanged;
    public event Action? Heartbeat;
    public event Action<string, bool, string?>? Acknowledged;

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    })
    {
        Timeout = TimeSpan.FromSeconds(8),
    };

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
    private string _apiKey = "";
    private CancellationTokenSource? _cts;
    private ClientWebSocket? _ws;
    private MainTimer? _retry;
    private MainTimer? _pollTimer;
    private int _attempt;
    private string _baseUrl = "";
    private string _wsUrl = "";

    // Merged Moonraker object state (e.g. _objects["print_stats"], _objects["extruder"], etc.)
    private readonly JsonObject _objects = new();
    private List<string> _subscribedObjects = [];
    private string? _lightObject;
    private string? _klipperVersion;
    private string? _cachedMetaFile;
    private JsonObject? _fileMeta;
    private PrinterStatus? _lastStatus;
    private bool _lastCanceled;

    public MoonrakerConnection()
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

    public void Start(PrinterConfig config, string apiKey = "")
    {
        Stop();
        _config = config;
        _apiKey = apiKey.Trim();
        _attempt = 0;
        Open();
    }

    public void Stop()
    {
        _retry?.Dispose();
        _pollTimer?.Dispose();
        _retry = null;
        _pollTimer = null;
        CancelSession();
        _config = null;
        _lastStatus = null;
        _objects.Clear();
        _cachedMetaFile = null;
        _fileMeta = null;
        Phase = ConnectionPhase.IdlePhase;
    }

    public void Refresh()
    {
        if (_config is null) return;
        if (Phase is ConnectionPhase.Online && _baseUrl.Length > 0)
            _ = QueryFullStateAsync(_cts?.Token ?? CancellationToken.None);
        else
            ReconnectSoon(TimeSpan.Zero);
    }

    // MARK: - Commands

    public void Pause() => SendPost("pause", "/printer/print/pause");
    public void Resume() => SendPost("resume", "/printer/print/resume");

    public void StopPrint()
    {
        _lastCanceled = true;
        SendPost("stop", "/printer/print/cancel");
    }

    public void SetSpeed(SpeedLevel level)
    {
        var pct = level.Percent();
        SendGcode("print_speed", $"M220 S{pct}");
    }

    public void SetChamberLight(bool on)
    {
        if (_lightObject is { } light)
        {
            var val = on ? "1" : "0";
            if (light.StartsWith("output_pin ", StringComparison.Ordinal))
            {
                var pin = light["output_pin ".Length..];
                SendGcode("ledctrl", $"SET_PIN PIN={pin} VALUE={val}");
                return;
            }
            if (light.StartsWith("led ", StringComparison.Ordinal) || light.StartsWith("neopixel ", StringComparison.Ordinal))
            {
                var name = light[(light.IndexOf(' ') + 1)..];
                SendGcode("ledctrl", $"SET_LED LED={name} WHITE={val} RED={val} GREEN={val} BLUE={val}");
                return;
            }
        }
        // Fallback for common Klipper/Snapmaker U1 light macros
        SendGcode("ledctrl", on ? "CHAMBER_LIGHT_ON" : "CHAMBER_LIGHT_OFF");
    }

    private void SendGcode(string commandName, string script) =>
        SendPost(commandName, $"/printer/gcode/script?script={Uri.EscapeDataString(script)}");

    private void SendPost(string commandName, string pathAndQuery)
    {
        if (_baseUrl.Length == 0 || Phase is not ConnectionPhase.Online) return;
        var url = _baseUrl + pathAndQuery;
        var key = _apiKey;
        _ = Task.Run(async () =>
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                if (key.Length > 0) req.Headers.TryAddWithoutValidation("X-Api-Key", key);
                using var resp = await Http.SendAsync(req).ConfigureAwait(false);
                var ok = resp.IsSuccessStatusCode;
                string? reason = ok ? null : $"HTTP {(int)resp.StatusCode}";
                MainThread.Post(() =>
                {
                    Acknowledged?.Invoke(commandName, ok, reason);
                    if (ok) Refresh();
                });
            }
            catch (Exception ex)
            {
                MainThread.Post(() => Acknowledged?.Invoke(commandName, false, ex.Message));
            }
        });
    }

    // MARK: - Connection & WebSocket Loop

    private void Open()
    {
        if (_config is null) return;
        CancelSession();
        Phase = ConnectionPhase.ConnectingPhase;
        var cts = new CancellationTokenSource();
        _cts = cts;
        var config = _config;
        var key = _apiKey;
        _ = Task.Run(() => RunSessionAsync(config, key, cts.Token));
    }

    private void CancelSession()
    {
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        _cts = null;
        try { _ws?.Dispose(); } catch { }
        _ws = null;
    }

    private async Task RunSessionAsync(PrinterConfig config, string apiKey, CancellationToken token)
    {
        try
        {
            var resolved = await ResolveEndpointAsync(config, apiKey, token).ConfigureAwait(false);
            if (resolved is null) return; // Phase already updated inside ResolveEndpointAsync
            var (baseUrl, wsUrl, version) = resolved.Value;

            var objects = await FetchObjectListAsync(baseUrl, apiKey, token).ConfigureAwait(false);
            var lightObj = FindLightObject(objects);

            MainThread.Post(() =>
            {
                if (token.IsCancellationRequested) return;
                _baseUrl = baseUrl;
                _wsUrl = wsUrl;
                _klipperVersion = version;
                _subscribedObjects = objects;
                _lightObject = lightObj;
            });

            // Query initial state immediately over HTTP so UI updates right away
            await QueryFullStateAsync(baseUrl, apiKey, objects, token).ConfigureAwait(false);

            // Start periodic poll timer on main thread as a heartbeat & metadata refresher
            MainThread.Post(() =>
            {
                if (token.IsCancellationRequested) return;
                _pollTimer?.Dispose();
                _pollTimer = MainTimer.Every(TimeSpan.FromSeconds(10), () =>
                {
                    if (!token.IsCancellationRequested && _baseUrl.Length > 0)
                        _ = QueryFullStateAsync(_baseUrl, _apiKey, _subscribedObjects, token);
                });
            });

            // Connect WebSocket for instant push updates
            using var ws = new ClientWebSocket();
            if (apiKey.Length > 0) ws.Options.SetRequestHeader("X-Api-Key", apiKey);
            _ws = ws;
            await ws.ConnectAsync(new Uri(wsUrl), token).ConfigureAwait(false);

            // Subscribe to printer objects over WebSocket JSON-RPC
            var subObjects = new JsonObject();
            foreach (var obj in objects) subObjects[obj] = null;
            var subPayload = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "printer.objects.subscribe",
                ["params"] = new JsonObject { ["objects"] = subObjects },
                ["id"] = 1,
            };
            var subBytes = Encoding.UTF8.GetBytes(subPayload.ToJsonString());
            await ws.SendAsync(subBytes, WebSocketMessageType.Text, true, token).ConfigureAwait(false);

            var buffer = new byte[16384];
            using var ms = new MemoryStream();
            while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text && ms.Length > 0)
                {
                    var text = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                    HandleWebSocketMessage(text, baseUrl, apiKey, token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            MainThread.Post(() =>
            {
                if (token.IsCancellationRequested) return;
                Phase = new ConnectionPhase.Offline($"Connection lost: {ex.Message}");
                ScheduleReconnect();
            });
        }
    }

    private async Task<(string BaseUrl, string WsUrl, string? Version)?> ResolveEndpointAsync(
        PrinterConfig config, string apiKey, CancellationToken token)
    {
        var (host, port) = config.Endpoint;
        var hasExplicitPort = config.Host.Contains(':') && !config.Host.EndsWith("://", StringComparison.Ordinal);
        var portsToTry = hasExplicitPort ? new[] { port } : new[] { 80, 7125 };

        Exception? lastError = null;
        foreach (var candidatePort in portsToTry)
        {
            var baseUrl = candidatePort == 80 ? $"http://{host}" : $"http://{host}:{candidatePort}";
            var wsUrl = candidatePort == 80 ? $"ws://{host}/websocket" : $"ws://{host}:{candidatePort}/websocket";
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/printer/info");
                if (apiKey.Length > 0) req.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
                using var resp = await Http.SendAsync(req, token).ConfigureAwait(false);
                if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    MainThread.Post(() =>
                    {
                        if (!token.IsCancellationRequested) Phase = new ConnectionPhase.Unauthorized();
                    });
                    return null;
                }
                if (!resp.IsSuccessStatusCode) continue;
                var body = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                if (JsonNode.Parse(body) is JsonObject root && root["result"] is JsonObject res)
                {
                    var version = J.String(res["software_version"]);
                    return (baseUrl, wsUrl, version);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { lastError = ex; }
        }

        MainThread.Post(() =>
        {
            if (token.IsCancellationRequested) return;
            Phase = new ConnectionPhase.Offline(lastError?.Message ?? $"Could not reach Moonraker/Fluidd at {host}.");
            ScheduleReconnect();
        });
        return null;
    }

    private static async Task<List<string>> FetchObjectListAsync(string baseUrl, string apiKey, CancellationToken token)
    {
        var defaults = new List<string>
        {
            "print_stats", "virtual_sdcard", "display_status", "webhooks",
            "gcode_move", "toolhead", "extruder", "heater_bed", "fan",
        };
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/printer/objects/list");
            if (apiKey.Length > 0) req.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
            using var resp = await Http.SendAsync(req, token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return defaults;
            var body = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (JsonNode.Parse(body)?["result"]?["objects"] is not JsonArray arr) return defaults;

            var chosen = new HashSet<string>(defaults);
            foreach (var item in arr)
            {
                if (item?.GetValue<string>() is not { } name) continue;
                if (name is "extruder1" or "extruder2" or "extruder3"
                    or "print_task_config" or "filament_detect" or "filament_parameters"
                    || name.StartsWith("filament_feed ", StringComparison.Ordinal)
                    || name.StartsWith("tmc", StringComparison.Ordinal)
                    || name.StartsWith("temperature_sensor ", StringComparison.Ordinal)
                    || name.StartsWith("temperature_fan ", StringComparison.Ordinal)
                    || name.StartsWith("heater_generic ", StringComparison.Ordinal)
                    || name.StartsWith("fan_generic ", StringComparison.Ordinal)
                    || name.StartsWith("filament_switch_sensor ", StringComparison.Ordinal)
                    || name.StartsWith("filament_motion_sensor ", StringComparison.Ordinal)
                    || name.StartsWith("output_pin ", StringComparison.Ordinal)
                    || name.StartsWith("led ", StringComparison.Ordinal)
                    || name.StartsWith("neopixel ", StringComparison.Ordinal))
                {
                    chosen.Add(name);
                }
            }
            return chosen.ToList();
        }
        catch
        {
            return defaults;
        }
    }

    private static string? FindLightObject(IEnumerable<string> objects)
    {
        foreach (var obj in objects)
        {
            var lower = obj.ToLowerInvariant();
            if ((lower.StartsWith("output_pin ") || lower.StartsWith("led ") || lower.StartsWith("neopixel "))
                && (lower.Contains("light") || lower.Contains("led") || lower.Contains("case") || lower.Contains("chamber") || lower.Contains("cavity")))
            {
                return obj;
            }
        }
        return null;
    }

    private Task QueryFullStateAsync(CancellationToken token) =>
        QueryFullStateAsync(_baseUrl, _apiKey, _subscribedObjects, token);

    private async Task QueryFullStateAsync(string baseUrl, string apiKey, List<string> objects, CancellationToken token)
    {
        if (baseUrl.Length == 0 || objects.Count == 0) return;
        try
        {
            var query = string.Join("&", objects.Select(Uri.EscapeDataString));
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/printer/objects/query?{query}");
            if (apiKey.Length > 0) req.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
            using var resp = await Http.SendAsync(req, token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;
            var body = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (JsonNode.Parse(body)?["result"]?["status"] is JsonObject statusObj)
            {
                await CheckFileMetadataIfNeededAsync(statusObj, baseUrl, apiKey, token).ConfigureAwait(false);
                MainThread.Post(() =>
                {
                    if (token.IsCancellationRequested) return;
                    Heartbeat?.Invoke();
                    MergeAndPublish(statusObj);
                });
            }
        }
        catch { }
    }

    private void HandleWebSocketMessage(string text, string baseUrl, string apiKey, CancellationToken token)
    {
        try
        {
            if (JsonNode.Parse(text) is not JsonObject msg) return;
            JsonObject? statusDelta = null;
            if (J.String(msg["method"]) == "notify_status_update" && msg["params"] is JsonArray { Count: > 0 } parms)
                statusDelta = parms[0] as JsonObject;
            else if (msg["result"] is JsonObject res && res["status"] is JsonObject initialStatus)
                statusDelta = initialStatus;

            if (statusDelta is not null)
            {
                _ = CheckFileMetadataIfNeededAsync(statusDelta, baseUrl, apiKey, token);
                MainThread.Post(() =>
                {
                    if (token.IsCancellationRequested) return;
                    Heartbeat?.Invoke();
                    MergeAndPublish(statusDelta);
                });
            }
        }
        catch { }
    }

    private async Task CheckFileMetadataIfNeededAsync(JsonObject delta, string baseUrl, string apiKey, CancellationToken token)
    {
        var filename = J.String(delta["print_stats"]?["filename"]) ?? J.String(_objects["print_stats"]?["filename"]);
        if (string.IsNullOrEmpty(filename) || filename == _cachedMetaFile) return;
        _cachedMetaFile = filename;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/server/files/metadata?filename={Uri.EscapeDataString(filename)}");
            if (apiKey.Length > 0) req.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
            using var resp = await Http.SendAsync(req, token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;
            var body = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (JsonNode.Parse(body)?["result"] is JsonObject meta)
            {
                MainThread.Post(() =>
                {
                    _fileMeta = meta;
                    PublishCurrent();
                });
            }
        }
        catch { }
    }

    private void MergeAndPublish(JsonObject delta)
    {
        foreach (var (key, val) in delta)
        {
            if (val is not JsonObject incoming) continue;
            if (_objects[key] is JsonObject existing)
            {
                foreach (var (k, v) in incoming) existing[k] = v?.DeepClone();
            }
            else
            {
                _objects[key] = incoming.DeepClone();
            }
        }

        if (Phase is not ConnectionPhase.Online)
        {
            _attempt = 0;
            Phase = ConnectionPhase.OnlinePhase;
        }
        PublishCurrent();
    }

    private void PublishCurrent()
    {
        if (_config is null) return;
        var status = ParseMoonrakerStatus(_objects, _fileMeta, _klipperVersion, _lightObject, _config);
        var canceled = _lastCanceled && status.State is JobState.Idle or JobState.Failed;
        if (status.State.IsActive()) _lastCanceled = false;
        if (canceled && status.State == JobState.Failed) status.Canceled = true;

        if (_lastStatus is { } last)
        {
            var same = status with { UpdatedAt = last.UpdatedAt };
            if (same == last) return;
            if (status.RemainingMinutes == last.RemainingMinutes && status.Progress == last.Progress && status.State == last.State)
                status.UpdatedAt = last.UpdatedAt;
        }
        _lastStatus = status;
        StatusChanged?.Invoke(status, canceled);
    }

    // MARK: - Moonraker -> PrinterStatus Parser

    public static PrinterStatus ParseMoonrakerStatus(
        JsonObject objects, JsonObject? fileMeta, string? klipperVersion, string? lightObject, PrinterConfig config)
    {
        var printStats = objects["print_stats"] as JsonObject;
        var sdcard = objects["virtual_sdcard"] as JsonObject;
        var display = objects["display_status"] as JsonObject;
        var webhooks = objects["webhooks"] as JsonObject;
        var gcodeMove = objects["gcode_move"] as JsonObject;
        var toolhead = objects["toolhead"] as JsonObject;
        var heaterBed = objects["heater_bed"] as JsonObject;
        var partFanObj = objects["fan"] as JsonObject;

        var extruders = new[]
        {
            objects["extruder"] as JsonObject,
            objects["extruder1"] as JsonObject,
            objects["extruder2"] as JsonObject,
            objects["extruder3"] as JsonObject,
        };

        // Determine active toolhead / extruder index (0..3)
        var activeExtruderIdx = 0;
        var foundActive = false;
        for (var i = 0; i < extruders.Length; i++)
        {
            if (extruders[i] is not { } ext) continue;
            if (string.Equals(J.String(ext["state"]), "ACTIVATE", StringComparison.OrdinalIgnoreCase)
                || (ext["active_pin"] is JsonValue ap && ap.TryGetValue<bool>(out var pin) && pin))
            {
                activeExtruderIdx = i;
                foundActive = true;
                break;
            }
        }
        if (!foundActive && J.String(toolhead?["extruder"]) is { } thExt)
        {
            activeExtruderIdx = thExt switch
            {
                "extruder1" => 1,
                "extruder2" => 2,
                "extruder3" => 3,
                _ => 0,
            };
        }

        var primaryExtruder = extruders[activeExtruderIdx] ?? extruders[0];

        var rawState = (J.String(printStats?["state"]) ?? "standby").ToLowerInvariant();
        var webhookState = (J.String(webhooks?["state"]) ?? "ready").ToLowerInvariant();

        var state = rawState switch
        {
            "printing" => JobState.Printing,
            "paused" => JobState.Paused,
            "complete" => JobState.Finished,
            "error" => JobState.Failed,
            "cancelled" or "canceled" => JobState.Failed,
            _ => JobState.Idle,
        };

        var canceled = rawState is "cancelled" or "canceled";

        // Progress (0..100)
        var rawProgress = J.Double(sdcard?["progress"]) ?? J.Double(display?["progress"]) ?? 0.0;
        var progress = Math.Clamp((int)Math.Round(rawProgress * 100.0), 0, 100);
        if (state == JobState.Finished) progress = 100;

        // Heating/preparing detection before progress reaches 1%
        var nozzleTemp = J.Double(primaryExtruder?["temperature"]) ?? 0;
        var nozzleTarget = J.Double(primaryExtruder?["target"]) ?? 0;
        var bedTemp = J.Double(heaterBed?["temperature"]) ?? 0;
        var bedTarget = J.Double(heaterBed?["target"]) ?? 0;

        int? stageId = state == JobState.Printing ? 0 : null;
        if (state == JobState.Printing && progress == 0 && (J.Double(printStats?["print_duration"]) ?? 0) < 90)
        {
            if (bedTarget > 0 && Math.Abs(bedTemp - bedTarget) > 3) { state = JobState.Preparing; stageId = 2; }
            else if (nozzleTarget > 0 && Math.Abs(nozzleTemp - nozzleTarget) > 5) { state = JobState.Preparing; stageId = 7; }
        }

        // Job name
        var filename = J.String(printStats?["filename"]) ?? "";
        var jobName = Path.GetFileNameWithoutExtension(filename);

        // Layers
        var info = printStats?["info"] as JsonObject;
        var currentLayer = J.Int(info?["current_layer"]) ?? 0;
        var totalLayers = J.Int(info?["total_layer"]) ?? J.Int(fileMeta?["layer_count"]) ?? 0;
        if (totalLayers > 0 && currentLayer == 0 && progress > 0)
            currentLayer = Math.Max(1, (int)Math.Round(totalLayers * (progress / 100.0)));

        // Remaining time (minutes)
        var printDuration = J.Double(printStats?["print_duration"]) ?? 0.0;
        var metaEstimated = J.Double(fileMeta?["estimated_time"]);
        var remainingMinutes = 0;
        if (state.IsActive())
        {
            if (metaEstimated is double est && est > 0 && rawProgress > 0.01)
            {
                var leftSec = Math.Max(0, est * (1.0 - rawProgress));
                remainingMinutes = (int)Math.Ceiling(leftSec / 60.0);
            }
            else if (rawProgress > 0.02 && printDuration > 10)
            {
                var totalSec = printDuration / rawProgress;
                var leftSec = Math.Max(0, totalSec - printDuration);
                remainingMinutes = (int)Math.Ceiling(leftSec / 60.0);
            }
        }

        // All toolhead temperatures (T1..T4) and secondary nozzle fallback
        var toolheadTemps = new List<Temperature>();
        foreach (var ext in extruders)
        {
            if (ext is null) continue;
            toolheadTemps.Add(new Temperature(
                Math.Round(J.Double(ext["temperature"]) ?? 0, 1),
                Math.Round(J.Double(ext["target"]) ?? 0, 1)));
        }

        Temperature? secondNozzle = null;
        JsonObject? secondaryExtruder = null;
        for (var i = 0; i < extruders.Length; i++)
        {
            if (i == activeExtruderIdx || extruders[i] is not { } ext) continue;
            if ((J.Double(ext["target"]) ?? 0) > 0) { secondaryExtruder = ext; break; }
            secondaryExtruder ??= ext;
        }
        if (secondaryExtruder is not null)
        {
            secondNozzle = new Temperature(
                Math.Round(J.Double(secondaryExtruder["temperature"]) ?? 0, 1),
                Math.Round(J.Double(secondaryExtruder["target"]) ?? 0, 1));
        }

        // Chamber temperature sensor & X/Y stepper driver temperatures
        double? chamberTemp = null;
        double? chamberTarget = null;
        double? driverXTemp = null;
        double? driverYTemp = null;
        foreach (var (k, v) in objects)
        {
            if (v is not JsonObject sensorObj) continue;
            var lower = k.ToLowerInvariant();
            if ( chamberTemp is null
                && (lower.StartsWith("temperature_sensor ") || lower.StartsWith("temperature_fan ") || lower.StartsWith("heater_generic "))
                && (lower.Contains("chamber") || lower.Contains("enclosure") || lower.Contains("cavity")))
            {
                chamberTemp = Math.Round(J.Double(sensorObj["temperature"]) ?? 0, 1);
                if (J.Double(sensorObj["target"]) is double ct && ct > 0) chamberTarget = Math.Round(ct, 1);
            }
            else if (lower.StartsWith("tmc") && J.Double(sensorObj["temperature"]) is double drvTemp)
            {
                if (lower.EndsWith("stepper_x") || lower.Contains("stepper_x "))
                    driverXTemp = Math.Round(drvTemp, 1);
                else if (lower.EndsWith("stepper_y") || lower.Contains("stepper_y "))
                    driverYTemp = Math.Round(drvTemp, 1);
            }
        }

        // Fans
        int? partFan = J.Double(partFanObj?["speed"]) is double pf ? (int)Math.Round(pf * 100) : null;
        int? auxFan = null;
        int? exhaustFan = null;
        foreach (var (k, v) in objects)
        {
            if (v is not JsonObject fanObj) continue;
            var lower = k.ToLowerInvariant();
            if (!lower.StartsWith("fan_generic ")) continue;
            var speedPct = J.Double(fanObj["speed"]) is double sp ? (int)Math.Round(sp * 100) : (int?)null;
            if (lower.Contains("aux") || lower.Contains("circulation") || lower.Contains("cavity")) auxFan ??= speedPct;
            else if (lower.Contains("exhaust") || lower.Contains("chamber") || lower.Contains("filter")) exhaustFan ??= speedPct;
        }

        // Speed factor
        var speedFactor = (J.Double(gcodeMove?["speed_factor"]) ?? 1.0) * 100.0;
        var speed = speedFactor switch
        {
            <= 70 => SpeedLevel.Silent,
            <= 112 => SpeedLevel.Standard,
            <= 145 => SpeedLevel.Sport,
            _ => SpeedLevel.Ludicrous,
        };

        // Chamber light
        bool? chamberLight = null;
        if (lightObject is not null && objects[lightObject] is JsonObject lightNode)
        {
            if (J.Double(lightNode["value"]) is double val)
                chamberLight = val > 0.01;
            else if (lightNode["color_data"] is JsonArray { Count: > 0 } cdata && cdata[0] is JsonArray rgb)
                chamberLight = rgb.Any(c => (J.Double(c) ?? 0) > 0.01);
        }

        // Issues / Klipper errors
        var issues = new List<PrinterIssue>();
        if (webhookState is "error" or "shutdown")
        {
            var msg = J.String(webhooks?["state_message"])?.Split('\n').FirstOrDefault() ?? "Klipper reported an error.";
            issues.Add(new PrinterIssue("KLIPPER_ERR", IssueSeverity.Fatal, msg, null));
        }
        else if (J.String(printStats?["message"]) is { Length: > 0 } statMsg)
        {
            issues.Add(new PrinterIssue("PRINT_MSG", IssueSeverity.Serious, statMsg, null));
        }

        // Spools / Toolheads (Snapmaker U1 has up to 4 toolheads/extruders: extruder, extruder1, extruder2, extruder3)
        var amsUnits = BuildSpools(objects, fileMeta);

        return new PrinterStatus
        {
            State = state,
            StageId = stageId,
            JobName = jobName,
            Progress = progress,
            RemainingMinutes = remainingMinutes,
            Layer = currentLayer,
            TotalLayers = totalLayers,
            Nozzle = new Temperature(Math.Round(nozzleTemp, 1), Math.Round(nozzleTarget, 1)),
            SecondNozzle = secondNozzle,
            ToolheadTemps = toolheadTemps.Count > 1 ? toolheadTemps.ToValueList() : [],
            DriverXTemp = driverXTemp,
            DriverYTemp = driverYTemp,
            Bed = new Temperature(Math.Round(bedTemp, 1), Math.Round(bedTarget, 1)),
            Chamber = chamberTemp,
            ChamberTarget = chamberTarget,
            PartFan = partFan,
            AuxFan = auxFan,
            ExhaustFan = exhaustFan,
            Speed = speed,
            ChamberLightOn = chamberLight,
            Ams = amsUnits.ToValueList(),
            ActiveSpoolId = amsUnits.Count > 0 ? activeExtruderIdx : null,
            Issues = issues.ToValueList(),
            Firmware = klipperVersion,
            NozzleDiameter = J.Double(primaryExtruder?["nozzle_diameter"]) ?? J.Double(fileMeta?["nozzle_diameter"]) ?? 0.4,
            TaskId = filename.Length > 0 ? filename : null,
            Canceled = canceled,
            UpdatedAt = DateTime.UtcNow,
        };
    }

    private static List<AmsUnit> BuildSpools(JsonObject objects, JsonObject? fileMeta)
    {
        var taskConfig = objects["print_task_config"] as JsonObject;
        var taskColors = taskConfig?["filament_color_rgba"] as JsonArray;
        var taskMultiColors = taskConfig?["filament_color_multi"] as JsonArray;
        var taskVendors = taskConfig?["filament_vendor"] as JsonArray;
        var taskTypes = taskConfig?["filament_type"] as JsonArray;
        var taskSubTypes = taskConfig?["filament_sub_type"] as JsonArray;
        var taskExist = taskConfig?["filament_exist"] as JsonArray;
        var taskOfficial = taskConfig?["filament_official"] as JsonArray;

        var detectInfo = (objects["filament_detect"] as JsonObject)?["info"] as JsonArray;
        var feedLeft = objects["filament_feed left"] as JsonObject;
        var feedRight = objects["filament_feed right"] as JsonObject;

        var metaMaterials = SplitSemicolon(J.String(fileMeta?["filament_type"]));
        var metaNames = SplitSemicolon(J.String(fileMeta?["filament_name"]));
        var metaColors = SplitSemicolon(J.String(fileMeta?["filament_color"]));

        var extruderKeys = new[] { "extruder", "extruder1", "extruder2", "extruder3" }
            .Where(objects.ContainsKey).ToList();
        if (extruderKeys.Count == 0)
        {
            var count = taskColors?.Count ?? 1;
            for (var c = 0; c < count; c++) extruderKeys.Add(c == 0 ? "extruder" : $"extruder{c}");
        }

        var spools = new List<Spool>();
        for (var i = 0; i < extruderKeys.Count; i++)
        {
            var rfid = detectInfo is not null && i < detectInfo.Count ? detectInfo[i] as JsonObject : null;
            var rfidMainType = J.String(rfid?["MAIN_TYPE"]);
            var rfidSubType = J.String(rfid?["SUB_TYPE"]);
            var rfidVendor = J.String(rfid?["VENDOR"]);
            var hasRfid = !string.IsNullOrEmpty(rfidMainType) && !string.Equals(rfidMainType, "NONE", StringComparison.OrdinalIgnoreCase);

            // 1. Loaded state: check print_task_config.filament_exist, then filament_feed left/right, then filament_switch_sensor
            var loaded = true;
            if (taskExist is not null && i < taskExist.Count && taskExist[i] is JsonValue existVal && existVal.TryGetValue<bool>(out var ex))
            {
                loaded = ex;
            }
            else
            {
                var feedChannel = (feedLeft?[ $"extruder{i}"] ?? feedRight?[$"extruder{i}"]) as JsonObject;
                if (feedChannel?["filament_detected"] is JsonValue fdVal && fdVal.TryGetValue<bool>(out var fd))
                {
                    loaded = fd;
                }
                else
                {
                    foreach (var (k, v) in objects)
                    {
                        if (v is not JsonObject sensor) continue;
                        var lower = k.ToLowerInvariant();
                        if ((lower.StartsWith("filament_switch_sensor ") || lower.StartsWith("filament_motion_sensor "))
                            && (extruderKeys.Count == 1 || lower.EndsWith(i.ToString()) || lower.Contains($"t{i}") || lower.Contains($"e{i}")))
                        {
                            if (sensor["filament_detected"] is JsonValue fv && fv.TryGetValue<bool>(out var det))
                                loaded = det;
                        }
                    }
                }
            }

            // 2. Material & subtype/name
            var rawType = StringAt(taskTypes, i);
            if (string.IsNullOrEmpty(rawType) || string.Equals(rawType, "NONE", StringComparison.OrdinalIgnoreCase))
                rawType = hasRfid ? rfidMainType : null;
            var mat = !string.IsNullOrEmpty(rawType)
                ? rawType!
                : (i < metaMaterials.Count ? metaMaterials[i] : metaMaterials.FirstOrDefault() ?? "PLA");

            var rawSubType = StringAt(taskSubTypes, i);
            if (string.IsNullOrEmpty(rawSubType) || string.Equals(rawSubType, "NONE", StringComparison.OrdinalIgnoreCase))
                rawSubType = hasRfid && !string.Equals(rfidSubType, "NONE", StringComparison.OrdinalIgnoreCase) ? rfidSubType : null;

            string name;
            if (!string.IsNullOrEmpty(rawSubType))
                name = rawSubType!.Contains(mat, StringComparison.OrdinalIgnoreCase) ? rawSubType : $"{mat} {rawSubType}";
            else if (taskTypes is null && i < metaNames.Count)
                name = metaNames[i];
            else
                name = mat;

            // 3. Brand / Vendor
            var rawVendor = StringAt(taskVendors, i);
            if (string.IsNullOrEmpty(rawVendor) || string.Equals(rawVendor, "NONE", StringComparison.OrdinalIgnoreCase))
                rawVendor = hasRfid && !string.Equals(rfidVendor, "NONE", StringComparison.OrdinalIgnoreCase) ? rfidVendor : null;
            string? brand = !string.IsNullOrEmpty(rawVendor) && !string.Equals(rawVendor, "NONE", StringComparison.OrdinalIgnoreCase)
                ? rawVendor
                : null;

            // 4. Color (RRGGBBAA)
            var rawColor = StringAt(taskColors, i)?.TrimStart('#');
            if (string.IsNullOrEmpty(rawColor) && hasRfid && rfid?["RGB_1"] is JsonValue rgbVal && rgbVal.TryGetValue<int>(out var rgbInt) && rgbInt > 0)
                rawColor = (rgbInt & 0xFFFFFF).ToString("X6") + "FF";
            if (string.IsNullOrEmpty(rawColor))
            {
                rawColor = i < metaColors.Count ? metaColors[i].TrimStart('#') : (i switch
                {
                    0 => "2ECC71",
                    1 => "3498DB",
                    2 => "E67E22",
                    _ => "9B59B6",
                });
            }
            var hexColor = NormalizeRgbaHex(rawColor!);

            // Multi-color array if present
            var colorsList = new List<string> { hexColor };
            if (taskMultiColors is not null && i < taskMultiColors.Count
                && taskMultiColors[i] is JsonObject multiObj
                && multiObj["colors"] is JsonArray multiArr && multiArr.Count > 1)
            {
                colorsList.Clear();
                foreach (var cNode in multiArr)
                {
                    if (J.String(cNode) is { Length: > 0 } cStr)
                        colorsList.Add(NormalizeRgbaHex(cStr.TrimStart('#')));
                }
                if (colorsList.Count == 0) colorsList.Add(hexColor);
            }

            // 5. RFID weight & nozzle temperature range
            int? weight = null;
            IntRange? nozzleRange = null;
            if (hasRfid && rfid is not null)
            {
                if (J.Int(rfid["WEIGHT"]) is int w && w > 0) weight = w;
                if (J.Int(rfid["HOTEND_MIN_TEMP"]) is int minT && J.Int(rfid["HOTEND_MAX_TEMP"]) is int maxT && minT > 0 && maxT >= minT)
                    nozzleRange = new IntRange(minT, maxT);
            }

            var isOfficial = (taskOfficial is not null && i < taskOfficial.Count && taskOfficial[i] is JsonValue offVal && offVal.TryGetValue<bool>(out var off) && off)
                || (hasRfid && rfid?["OFFICIAL"] is JsonValue rfidOff && rfidOff.TryGetValue<bool>(out var ro) && ro);

            var colorName = DescribeHexColor(hexColor);

            spools.Add(new Spool
            {
                Id = i,
                Unit = 0,
                Slot = i,
                CustomLabel = $"T{i + 1}",
                Material = loaded ? mat : "",
                Name = loaded ? name : "",
                Brand = loaded ? brand : null,
                Color = hexColor,
                Colors = colorsList.ToValueList(),
                ColorName = loaded ? colorName : null,
                Weight = loaded ? weight : null,
                NozzleRange = loaded ? nozzleRange : null,
                IsLoaded = loaded,
                IsBambu = isOfficial,
            });
        }

        return
        [
            new AmsUnit
            {
                Id = 0,
                Kind = AmsKind.Toolheads,
                Spools = spools.ToValueList(),
            },
        ];
    }

    private static string? StringAt(JsonArray? arr, int index) =>
        arr is not null && index >= 0 && index < arr.Count ? J.String(arr[index])?.Trim() : null;

    private static string NormalizeRgbaHex(string raw)
    {
        var clean = raw.Trim().TrimStart('#').ToUpperInvariant();
        return clean.Length switch
        {
            6 => clean + "FF",
            8 => clean,
            _ => "2ECC71FF",
        };
    }

    private static string? DescribeHexColor(string hexRgba)
    {
        if (hexRgba.Length < 6) return null;
        if (!byte.TryParse(hexRgba[0..2], System.Globalization.NumberStyles.HexNumber, null, out var r) ||
            !byte.TryParse(hexRgba[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g) ||
            !byte.TryParse(hexRgba[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b))
            return null;

        // Convert RGB to HSL to give a clean, human-readable color name
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        double max = Math.Max(rf, Math.Max(gf, bf)), min = Math.Min(rf, Math.Min(gf, bf));
        double l = (max + min) / 2.0;
        double d = max - min;

        if (l <= 0.08) return "Black";
        if (l >= 0.93 && d < 0.15) return "White";
        if (d < 0.10) return l < 0.35 ? "Dark Grey" : l > 0.72 ? "Light Grey" : "Grey";

        double s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
        if (s < 0.14) return l < 0.35 ? "Charcoal" : l > 0.72 ? "Silver" : "Grey";

        double h;
        if (max == rf) h = ((gf - bf) / d + (gf < bf ? 6 : 0)) * 60.0;
        else if (max == gf) h = ((bf - rf) / d + 2) * 60.0;
        else h = ((rf - gf) / d + 4) * 60.0;

        if (h is >= 12 and < 42 && l < 0.45) return "Brown";
        if (h is >= 32 and < 52 && s < 0.55 && l > 0.65) return "Beige";

        return h switch
        {
            < 15 or >= 345 => "Red",
            < 42 => "Orange",
            < 68 => "Yellow",
            < 165 => "Green",
            < 200 => "Cyan",
            < 255 => "Blue",
            < 295 => "Purple",
            _ => "Pink",
        };
    }

    private static List<string> SplitSemicolon(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return raw.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.Trim('"', '\'')).Where(s => s.Length > 0).ToList();
    }

    // MARK: - Reconnect & Power

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
        _pollTimer?.Dispose();
        _retry = null;
        _pollTimer = null;
        CancelSession();
    }
}
