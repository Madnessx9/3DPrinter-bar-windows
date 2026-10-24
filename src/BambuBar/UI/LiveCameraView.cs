using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BambuBar.Models;
using BambuBar.Services;
using static BambuBar.UI.Ui;

namespace BambuBar.UI;

/// <summary>
/// Interactive live camera viewport that replaces the 3D printer model in the job hero card.
/// Shows a "Live View" play overlay until clicked, then streams real-time video from either:
/// - Moonraker / Fluidd (/webcam/stream.mjpg or /webcam/snapshot.jpg) for Snapmaker U1 / Klipper printers, or
/// - Bambu Lab LAN chamber camera (TLS port 6000 JPEG stream on P1/A1 series).
/// </summary>
public sealed class LiveCameraView : Border
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(4),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private readonly Image _videoImage = new()
    {
        Stretch = Stretch.UniformToFill,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly Border _idleOverlay = new();
    private readonly Border _connectingOverlay = new();
    private readonly Border _errorOverlay = new();
    private readonly Border _liveControlsOverlay = new();
    private readonly TextBlock _errorText = Text("", 10.5, Medium, Theme.Secondary, wrap: true);

    private bool _compact;
    private string? _boundSerial;
    private bool _isPlaying;
    private CancellationTokenSource? _streamCts;

    public LiveCameraView()
    {
        ClipToBounds = true;
        CornerRadius = new CornerRadius(14);
        Background = new SolidColorBrush(Color.FromRgb(18, 20, 24));
        BorderBrush = Theme.Fill(0.12);
        BorderThickness = new Thickness(1);
        Cursor = Cursors.Hand;

        var root = new Grid { ClipToBounds = true };
        var roundedClip = new Border
        {
            CornerRadius = new CornerRadius(13),
            Background = new SolidColorBrush(Color.FromRgb(18, 20, 24)),
        };
        root.Children.Add(roundedClip);
        root.Children.Add(_videoImage);
        root.Children.Add(_idleOverlay);
        root.Children.Add(_connectingOverlay);
        root.Children.Add(_errorOverlay);
        root.Children.Add(_liveControlsOverlay);

        // Ensure child image clips cleanly to rounded corners
        SizeChanged += (_, e) =>
        {
            root.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 13, 13);
        };

        Child = root;

        MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_isPlaying) StopStream();
            else StartStream();
        };

        MouseEnter += (_, _) => UpdateLiveControlsVisibility(true);
        MouseLeave += (_, _) => UpdateLiveControlsVisibility(false);

        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible && _isPlaying) StopStream();
        };
        Unloaded += (_, _) => StopStream();

        RebuildOverlays();
        ShowIdleState();
    }

    public void Configure(bool compact)
    {
        var currentSerial = PrinterStore.Shared.Current?.Id;
        if (_boundSerial != currentSerial)
        {
            _boundSerial = currentSerial;
            StopStream();
        }

        if (_compact != compact)
        {
            _compact = compact;
            RebuildOverlays();
        }

        Width = compact ? 132 : 236;
        Height = compact ? 104 : 178;
    }

    private void RebuildOverlays()
    {
        var playCircleSize = _compact ? 34.0 : 46.0;
        var playIconSize = _compact ? 14.0 : 18.0;
        var labelSize = _compact ? 11.0 : 13.0;

        // 1. Idle ("Live View" + Play button)
        var playCircle = new Border
        {
            Width = playCircleSize,
            Height = playCircleSize,
            CornerRadius = new CornerRadius(playCircleSize / 2),
            Background = Theme.Brush(Palette.Accent, 0.22),
            BorderBrush = Theme.Brush(Palette.Accent, 0.65),
            BorderThickness = new Thickness(1.2),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = Icons.Make(Icon.Play, playIconSize, Brushes.White)
                .Aligned(HorizontalAlignment.Center, VerticalAlignment.Center),
        };
        var liveTitle = Text("Live View", labelSize, Semibold, Brushes.White);
        liveTitle.HorizontalAlignment = HorizontalAlignment.Center;
        var idleStack = Column(_compact ? 6 : 8, playCircle, liveTitle);
        idleStack.HorizontalAlignment = HorizontalAlignment.Center;
        idleStack.VerticalAlignment = VerticalAlignment.Center;

        _idleOverlay.Background = new SolidColorBrush(Color.FromArgb(140, 12, 14, 18));
        _idleOverlay.Child = idleStack;
        ToolTip = _isPlaying ? "Click to stop Live View" : "Click to start Live View";

        // 2. Connecting state
        var spinnerStack = Column(6,
            new Spinner(_compact ? 16 : 22) { HorizontalAlignment = HorizontalAlignment.Center },
            Text("Connecting…", _compact ? 10.5 : 12, Medium, Theme.Secondary).Aligned(HorizontalAlignment.Center));
        spinnerStack.HorizontalAlignment = HorizontalAlignment.Center;
        spinnerStack.VerticalAlignment = VerticalAlignment.Center;
        _connectingOverlay.Background = new SolidColorBrush(Color.FromArgb(180, 12, 14, 18));
        _connectingOverlay.Child = spinnerStack;

        // 3. Error state
        _errorText.TextAlignment = TextAlignment.Center;
        _errorText.HorizontalAlignment = HorizontalAlignment.Center;
        var retryBadge = new Border
        {
            Padding = new Thickness(8, 3, 8, 3),
            CornerRadius = new CornerRadius(10),
            Background = Theme.Fill(0.14),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = Row(4, Icons.Make(Icon.Refresh, 10, Brushes.White), Text("Retry", 10.5, Semibold, Brushes.White)),
        };
        var errStack = Column(6,
            Icons.Make(Icon.Video, _compact ? 14 : 18, Theme.Secondary).Aligned(HorizontalAlignment.Center),
            _errorText,
            retryBadge);
        errStack.Margin = new Thickness(8);
        errStack.HorizontalAlignment = HorizontalAlignment.Center;
        errStack.VerticalAlignment = VerticalAlignment.Center;
        _errorOverlay.Background = new SolidColorBrush(Color.FromArgb(200, 12, 14, 18));
        _errorOverlay.Child = errStack;

        // 4. Live badge + hover stop hint
        var livePill = new Border
        {
            Padding = new Thickness(6, 2, 6, 2),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(175, 16, 18, 22)),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(7),
            Child = Row(4, Dot(Palette.Failed, 5, glow: true), Text("LIVE", 9.5, Bold, Brushes.White)),
        };
        var stopPill = new Border
        {
            Padding = new Thickness(6, 2, 6, 2),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(175, 16, 18, 22)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(7),
            Child = Row(4, Icons.Make(Icon.Stop, 9, Brushes.White), Text("Stop", 9.5, Semibold, Brushes.White)),
        };
        var liveGrid = new Grid();
        liveGrid.Children.Add(livePill);
        liveGrid.Children.Add(stopPill);
        _liveControlsOverlay.Child = liveGrid;
    }

    private void UpdateLiveControlsVisibility(bool hovered)
    {
        if (!_isPlaying) return;
        if (_liveControlsOverlay.Child is Grid g && g.Children.Count >= 2)
            g.Children[1].Opacity = hovered ? 1.0 : 0.0;
    }

    private void ShowIdleState()
    {
        _idleOverlay.Visibility = Visibility.Visible;
        _connectingOverlay.Visibility = Visibility.Collapsed;
        _errorOverlay.Visibility = Visibility.Collapsed;
        _liveControlsOverlay.Visibility = Visibility.Collapsed;
        _videoImage.Opacity = 0.35; // Keep last frame dimmed as a nice preview if available
        ToolTip = "Click to start Live View";
    }

    private void ShowConnectingState()
    {
        _idleOverlay.Visibility = Visibility.Collapsed;
        _connectingOverlay.Visibility = Visibility.Visible;
        _errorOverlay.Visibility = Visibility.Collapsed;
        _liveControlsOverlay.Visibility = Visibility.Collapsed;
        ToolTip = "Click to cancel";
    }

    private void ShowStreamingState()
    {
        _idleOverlay.Visibility = Visibility.Collapsed;
        _connectingOverlay.Visibility = Visibility.Collapsed;
        _errorOverlay.Visibility = Visibility.Collapsed;
        _liveControlsOverlay.Visibility = Visibility.Visible;
        _videoImage.Opacity = 1.0;
        UpdateLiveControlsVisibility(IsMouseOver);
        ToolTip = "Click to stop Live View";
    }

    private void ShowErrorState(string message)
    {
        _isPlaying = false;
        _errorText.Text = message;
        _idleOverlay.Visibility = Visibility.Collapsed;
        _connectingOverlay.Visibility = Visibility.Collapsed;
        _errorOverlay.Visibility = Visibility.Visible;
        _liveControlsOverlay.Visibility = Visibility.Collapsed;
        ToolTip = "Click to retry Live View";
    }

    public void StartStream()
    {
        StopStream();
        var session = PrinterStore.Shared.Current;
        if (session is null)
        {
            ShowErrorState("No printer selected");
            return;
        }

        _isPlaying = true;
        ShowConnectingState();
        var cts = new CancellationTokenSource();
        _streamCts = cts;
        var config = session.Config;
        var accessCode = session.AccessCode ?? "";

        _ = Task.Run(async () =>
        {
            try
            {
                if (config.Protocol == PrinterProtocol.Moonraker)
                    await RunMoonrakerStreamAsync(config, accessCode, cts.Token).ConfigureAwait(false);
                else
                    await RunBambuStreamAsync(config, accessCode, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (!cts.Token.IsCancellationRequested)
                        ShowErrorState($"Live View unavailable ({ex.Message})");
                });
            }
        });
    }

    public void StopStream()
    {
        _isPlaying = false;
        try { _streamCts?.Cancel(); } catch { }
        _streamCts?.Dispose();
        _streamCts = null;
        ShowIdleState();
    }

    private void OnFrameReceived(byte[] jpegBytes, bool flipH, bool flipV, int rotation, CancellationToken token)
    {
        if (token.IsCancellationRequested || jpegBytes.Length < 4) return;
        try
        {
            using var ms = new MemoryStream(jpegBytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            if (rotation is 90) bmp.Rotation = Rotation.Rotate90;
            else if (rotation is 180) bmp.Rotation = Rotation.Rotate180;
            else if (rotation is 270) bmp.Rotation = Rotation.Rotate270;
            bmp.EndInit();
            bmp.Freeze();

            Dispatcher.BeginInvoke(() =>
            {
                if (token.IsCancellationRequested || !_isPlaying) return;
                _videoImage.Source = bmp;
                if (flipH || flipV)
                {
                    _videoImage.RenderTransformOrigin = new Point(0.5, 0.5);
                    _videoImage.RenderTransform = new ScaleTransform(flipH ? -1 : 1, flipV ? -1 : 1);
                }
                else
                {
                    _videoImage.RenderTransform = null;
                }
                ShowStreamingState();
            });
        }
        catch
        {
            // Ignore malformed frame
        }
    }

    // MARK: - Moonraker / Fluidd Webcam Streamer (Snapmaker U1 & Klipper)

    private async Task RunMoonrakerStreamAsync(PrinterConfig config, string apiKey, CancellationToken token)
    {
        var (host, port) = config.Endpoint;
        var baseUrl = port == 80 ? $"http://{host}" : $"http://{host}:{port}";

        var flipH = false;
        var flipV = false;
        var rotation = 0;
        var candidateStreams = new List<string>();
        var candidateSnapshots = new List<string>();

        // 1. Check /server/webcams/list for configured webcam URLs and orientation
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/server/webcams/list");
            if (apiKey.Length > 0) req.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
            using var ctsTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            ctsTimeout.CancelAfter(TimeSpan.FromSeconds(4));
            using var resp = await Http.SendAsync(req, ctsTimeout.Token).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ctsTimeout.Token).ConfigureAwait(false);
                if (JsonNode.Parse(body)?["result"]?["webcams"] is JsonArray { Count: > 0 } webcams
                    && webcams[0] is JsonObject cam)
                {
                    flipH = cam["flip_horizontal"] is JsonValue fh && fh.TryGetValue<bool>(out var h) && h;
                    flipV = cam["flip_vertical"] is JsonValue fv && fv.TryGetValue<bool>(out var v) && v;
                    rotation = cam["rotation"] is JsonValue rot && rot.TryGetValue<int>(out var r) ? r : 0;

                    if (cam["stream_url"]?.GetValue<string>() is { Length: > 0 } sUrl && !sUrl.EndsWith("/webrtc", StringComparison.OrdinalIgnoreCase))
                        candidateStreams.Add(ResolveCamUrl(baseUrl, sUrl));
                    if (cam["snapshot_url"]?.GetValue<string>() is { Length: > 0 } snapUrl)
                        candidateSnapshots.Add(ResolveCamUrl(baseUrl, snapUrl));
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { }

        // Common camera-streamer / ustreamer / mjpg-streamer endpoints on Fluidd & Snapmaker U1
        candidateStreams.Add($"{baseUrl}/webcam/stream.mjpg");
        candidateStreams.Add($"{baseUrl}/webcam/?action=stream");
        candidateStreams.Add($"{baseUrl}/webcam/stream");
        candidateSnapshots.Add($"{baseUrl}/webcam/snapshot.jpg");
        candidateSnapshots.Add($"{baseUrl}/webcam/?action=snapshot");

        // 2. Try MJPEG continuous stream first (lowest latency, 15fps on Snapmaker U1)
        foreach (var streamUrl in candidateStreams.Distinct())
        {
            if (await TryMjpegStreamAsync(streamUrl, apiKey, flipH, flipV, rotation, token).ConfigureAwait(false))
                return;
        }

        // 3. Fallback: poll JPEG snapshot endpoint at ~5 fps
        foreach (var snapUrl in candidateSnapshots.Distinct())
        {
            if (await TrySnapshotLoopAsync(snapUrl, apiKey, flipH, flipV, rotation, token).ConfigureAwait(false))
                return;
        }

        Dispatcher.BeginInvoke(() => ShowErrorState("Could not reach webcam stream"));
    }

    private static string ResolveCamUrl(string baseUrl, string pathOrUrl)
    {
        if (pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return pathOrUrl;
        return pathOrUrl.StartsWith('/') ? baseUrl + pathOrUrl : $"{baseUrl}/{pathOrUrl}";
    }

    private async Task<bool> TryMjpegStreamAsync(string url, string apiKey, bool flipH, bool flipV, int rotation, CancellationToken token)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (apiKey.Length > 0) req.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            connectCts.CancelAfter(TimeSpan.FromSeconds(4));
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, connectCts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;

            var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (!mediaType.Contains("multipart", StringComparison.OrdinalIgnoreCase) &&
                !mediaType.Contains("mjpeg", StringComparison.OrdinalIgnoreCase) &&
                !mediaType.Contains("jpeg", StringComparison.OrdinalIgnoreCase))
                return false;

            await using var stream = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var readBuf = new byte[32768];
            using var acc = new MemoryStream(262144);

            while (!token.IsCancellationRequested)
            {
                var n = await stream.ReadAsync(readBuf, token).ConfigureAwait(false);
                if (n <= 0) break;
                acc.Write(readBuf, 0, n);

                // Extract complete JPEG frames (SOI = FF D8, EOI = FF D9)
                while (TryExtractJpeg(acc, out var frame))
                {
                    OnFrameReceived(frame, flipH, flipV, rotation, token);
                }
            }
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryExtractJpeg(MemoryStream acc, out byte[] frame)
    {
        frame = [];
        var buf = acc.GetBuffer();
        var len = (int)acc.Length;
        if (len < 4) return false;

        var soi = -1;
        for (var i = 0; i < len - 1; i++)
        {
            if (buf[i] == 0xFF && buf[i + 1] == 0xD8) { soi = i; break; }
        }
        if (soi < 0)
        {
            acc.SetLength(0);
            return false;
        }

        var eoi = -1;
        for (var i = soi + 2; i < len - 1; i++)
        {
            if (buf[i] == 0xFF && buf[i + 1] == 0xD9) { eoi = i + 2; break; }
        }
        if (eoi < 0)
        {
            if (len > 4 * 1024 * 1024) acc.SetLength(0); // Reset if stream corrupted
            return false;
        }

        var frameLen = eoi - soi;
        frame = new byte[frameLen];
        Buffer.BlockCopy(buf, soi, frame, 0, frameLen);

        var remaining = len - eoi;
        if (remaining > 0)
            Buffer.BlockCopy(buf, eoi, buf, 0, remaining);
        acc.SetLength(remaining);
        return true;
    }

    private async Task<bool> TrySnapshotLoopAsync(string url, string apiKey, bool flipH, bool flipV, int rotation, CancellationToken token)
    {
        var gotFirst = false;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var sep = url.Contains('?') ? '&' : '?';
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{url}{sep}_t={Environment.TickCount64}");
                if (apiKey.Length > 0) req.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(TimeSpan.FromSeconds(4));
                using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return gotFirst;
                var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
                if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8) return gotFirst;
                gotFirst = true;
                OnFrameReceived(bytes, flipH, flipV, rotation, token);
                await Task.Delay(200, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { return gotFirst; }
        }
        return gotFirst;
    }

    // MARK: - Bambu Lab LAN Chamber Camera Streamer (Port 6000 TLS JPEG stream on P1/A1 series)

    private async Task RunBambuStreamAsync(PrinterConfig config, string accessCode, CancellationToken token)
    {
        if (string.IsNullOrEmpty(accessCode))
        {
            Dispatcher.BeginInvoke(() => ShowErrorState("Access code required for Live View"));
            return;
        }

        var (host, _) = config.Endpoint;
        using var tcp = new TcpClient();
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            connectCts.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await tcp.ConnectAsync(host, 6000, connectCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                Dispatcher.BeginInvoke(() => ShowErrorState("Camera stream not reachable on port 6000"));
                return;
            }
        }

        await using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = host,
        }, token).ConfigureAwait(false);

        // Build 80-byte Bambu chamber camera auth packet:
        // [0..3]: 0x40, 0x00, 0x00, 0x00
        // [4..7]: 0x00, 0x30, 0x00, 0x00
        // [8..15]: zeros
        // [16..47]: "bblp" padded with zeros (32 bytes)
        // [48..79]: accessCode padded with zeros (32 bytes)
        var auth = new byte[80];
        auth[0] = 0x40;
        auth[4] = 0x00;
        auth[5] = 0x30;
        var userBytes = Encoding.ASCII.GetBytes("bblp");
        Buffer.BlockCopy(userBytes, 0, auth, 16, Math.Min(userBytes.Length, 32));
        var passBytes = Encoding.ASCII.GetBytes(accessCode.Trim());
        Buffer.BlockCopy(passBytes, 0, auth, 48, Math.Min(passBytes.Length, 32));

        await ssl.WriteAsync(auth, token).ConfigureAwait(false);
        await ssl.FlushAsync(token).ConfigureAwait(false);

        var readBuf = new byte[32768];
        using var acc = new MemoryStream(262144);
        while (!token.IsCancellationRequested)
        {
            var n = await ssl.ReadAsync(readBuf, token).ConfigureAwait(false);
            if (n <= 0) break;
            acc.Write(readBuf, 0, n);

            while (TryExtractJpeg(acc, out var frame))
            {
                OnFrameReceived(frame, false, false, 0, token);
            }
        }
    }
}
