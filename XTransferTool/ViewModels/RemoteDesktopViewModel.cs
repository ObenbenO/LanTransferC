using System.Collections.ObjectModel;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using XTransferTool.Config;
using XTransferTool.Discovery;
using XTransferTool.Control.Proto;
using Grpc.Core;
using Grpc.Net.Client;

namespace XTransferTool.ViewModels;

public partial class RemoteDesktopViewModel : ViewModelBase
{
    public sealed record RemoteDeviceItem(string Display, ResolvedPeer Peer);

    private readonly IPeerDirectory? _directory;
    private readonly SettingsStore? _settings;
    private readonly int? _localControlPort;

    public ObservableCollection<RemoteDeviceItem> Devices { get; } = [];

    [ObservableProperty]
    private RemoteDeviceItem? _selectedDevice;

    [ObservableProperty]
    private string _latency = "9 ms";

    [ObservableProperty]
    private string _fps = "60 fps";

    [ObservableProperty]
    private string _bitrate = "18 Mbps";

    [ObservableProperty]
    private string _status = "未连接";

    [ObservableProperty]
    private WriteableBitmap? _frame;

    [ObservableProperty]
    private int _remoteWidth;

    [ObservableProperty]
    private int _remoteHeight;

    private CancellationTokenSource? _statsCts;
    private CancellationTokenSource? _streamCts;
    private long _inputSeq;
    private AsyncDuplexStreamingCall<RemoteInputEvent, RemoteInputAck>? _inputCall;
    private Process? _decoder;
    private Stream? _decoderIn;
    private Stream? _decoderOut;
    private string? _sessionId;

    public RemoteDesktopViewModel(IPeerDirectory? directory = null, SettingsStore? settings = null, int? localControlPort = null)
    {
        _directory = directory;
        _settings = settings;
        _localControlPort = localControlPort;
        if (_directory is not null)
        {
            _directory.Changed += OnDirectoryChanged;
            RebuildDevices();
        }
    }

    [RelayCommand]
    private async Task Connect()
    {
        var targetPeer = SelectedDevice?.Peer;
        var targetDisplay = SelectedDevice?.Display;

        try
        {
            await Disconnect();

            Status = "连接中…";
            _statsCts = new CancellationTokenSource();
            _streamCts = new CancellationTokenSource();

            var peer = targetPeer;
            if (peer is null)
            {
                Status = "请选择设备";
                return;
            }

            var address = peer.Addresses?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a)) ?? "";
            if (string.IsNullOrWhiteSpace(address))
            {
                Status = "设备地址为空";
                return;
            }

            var localId = _settings?.Current.Identity.DeviceId;
            if (string.IsNullOrWhiteSpace(localId))
                localId = "local";

            var channel = GrpcChannel.ForAddress($"http://{address}:{peer.ControlPort}");
            var control = new RemoteControlService.RemoteControlServiceClient(channel);
            var stream = new RemoteDesktopStreamService.RemoteDesktopStreamServiceClient(channel);

            var create = await control.CreateSessionAsync(new CreateRemoteSessionRequest
            {
                RequestId = Guid.NewGuid().ToString(),
                FromId = localId,
                ToPeerId = peer.Id,
                Mode = "control",
                Preferred = new RemotePreference { QualityPreset = "smooth", MaxResolution = "1920x1080" }
            }, cancellationToken: _streamCts.Token);
            _sessionId = create.SessionId;

            _inputCall = control.InputStream(cancellationToken: _streamCts.Token);
            _ = DrainAcksAsync(_inputCall, _streamCts.Token);

            var stats = control.SubscribeStats(new SubscribeRemoteStatsRequest
            {
                SessionId = _sessionId,
                FromId = localId,
                IntervalMs = 500
            }, cancellationToken: _statsCts.Token);

            _ = Task.Run(async () =>
            {
                try
                {
                    while (await stats.ResponseStream.MoveNext(_statsCts.Token))
                    {
                        var s = stats.ResponseStream.Current;
                        Latency = $"{s.RttMs} ms";
                        Fps = $"{s.Fps:0} fps";
                        Bitrate = $"{s.BitrateMbps:0.#} Mbps";
                    }
                }
                catch (OperationCanceledException) { }
            }, _statsCts.Token);

            var call = stream.SubscribeHevcStream(new SubscribeHevcStreamRequest
            {
                SessionId = _sessionId,
                FromId = localId,
                TargetFps = 30,
                MaxResolution = "1920x1080",
                QualityPreset = "smooth"
            }, cancellationToken: _streamCts.Token);

            _ = Task.Run(() => ConsumeHevcAsync(call, _streamCts.Token), _streamCts.Token);
        }
        catch (RpcException ex)
        {
            Status = $"{(string.IsNullOrWhiteSpace(targetDisplay) ? "" : targetDisplay + " - ")}{ex.StatusCode}: {ex.Status.Detail}";
        }
        catch (Exception ex)
        {
            Status = $"{(string.IsNullOrWhiteSpace(targetDisplay) ? "" : targetDisplay + " - ")}{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task Disconnect()
    {
        _statsCts?.Cancel();
        _streamCts?.Cancel();

        try
        {
            if (_inputCall is not null)
                await _inputCall.RequestStream.CompleteAsync();
        }
        catch
        {
        }

        try { _decoderIn?.Dispose(); } catch { }
        try { _decoderOut?.Dispose(); } catch { }
        try
        {
            if (_decoder is not null && !_decoder.HasExited)
                _decoder.Kill(entireProcessTree: true);
        }
        catch
        {
        }
        try { _decoder?.Dispose(); } catch { }

        _inputCall = null;
        _decoder = null;
        _decoderIn = null;
        _decoderOut = null;
        _sessionId = null;
        Frame = null;
        RemoteWidth = 0;
        RemoteHeight = 0;
        Status = "未连接";
    }

    [RelayCommand]
    private async Task SendDemoInput()
    {
        SendMouseMove(80, 80);
        SendMouseDown(80, 80, button: 0);
        await Task.Delay(40);
        SendMouseUp(80, 80, button: 0);
    }

    public void SendMouseMove(int x, int y)
    {
        var call = _inputCall;
        if (call is null)
            return;
        if (string.IsNullOrWhiteSpace(_sessionId))
            return;

        var payload = new byte[8];
        BitConverter.GetBytes(x).CopyTo(payload, 0);
        BitConverter.GetBytes(y).CopyTo(payload, 4);

        _ = call.RequestStream.WriteAsync(new RemoteInputEvent
        {
            SessionId = _sessionId,
            Seq = Interlocked.Increment(ref _inputSeq),
            TsMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Type = "mouseMove",
            Payload = Google.Protobuf.ByteString.CopyFrom(payload)
        });
    }

    public void SendMouseDown(int x, int y, byte button)
    {
        SendMouseMove(x, y);
        SendMouseButton(button, isDown: true);
    }

    public void SendMouseUp(int x, int y, byte button)
    {
        SendMouseMove(x, y);
        SendMouseButton(button, isDown: false);
    }

    private void SendMouseButton(byte button, bool isDown)
    {
        var call = _inputCall;
        if (call is null)
            return;
        if (string.IsNullOrWhiteSpace(_sessionId))
            return;

        _ = call.RequestStream.WriteAsync(new RemoteInputEvent
        {
            SessionId = _sessionId,
            Seq = Interlocked.Increment(ref _inputSeq),
            TsMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Type = isDown ? "mouseDown" : "mouseUp",
            Payload = Google.Protobuf.ByteString.CopyFrom([button])
        });
    }

    private static async Task DrainAcksAsync(AsyncDuplexStreamingCall<RemoteInputEvent, RemoteInputAck> call, CancellationToken ct)
    {
        try
        {
            while (await call.ResponseStream.MoveNext(ct))
            {
            }
        }
        catch
        {
        }
    }

    private async Task ConsumeHevcAsync(AsyncServerStreamingCall<HevcStreamChunk> call, CancellationToken ct)
    {
        long bytesInWindow = 0;
        var windowStart = Stopwatch.StartNew();

        try
        {
            while (await call.ResponseStream.MoveNext(ct))
            {
                var chunk = call.ResponseStream.Current;
                if (chunk is null)
                    continue;

                if (RemoteWidth <= 0 || RemoteHeight <= 0)
                {
                    RemoteWidth = chunk.Width;
                    RemoteHeight = chunk.Height;
                    if (!TryStartDecoder(RemoteWidth, RemoteHeight, out var err))
                    {
                        Status = err;
                        return;
                    }
                }

                var data = chunk.Data.ToByteArray();
                bytesInWindow += data.Length;
                try
                {
                    await _decoderIn!.WriteAsync(data, 0, data.Length, ct);
                }
                catch (Exception ex)
                {
                    Status = $"解码管道异常：{ex.Message}";
                    return;
                }
                Status = "已连接";

                if (windowStart.ElapsedMilliseconds >= 1000)
                {
                    var mbps = bytesInWindow * 8.0 / 1_000_000.0;
                    Bitrate = $"{mbps:0.#} Mbps";
                    bytesInWindow = 0;
                    windowStart.Restart();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (RpcException ex)
        {
            Status = $"{ex.StatusCode}: {ex.Status.Detail}";
        }
        catch
        {
            Status = "流已断开";
        }
        finally
        {
            try { _decoderIn?.Close(); } catch { }
        }
    }

    private bool TryStartDecoder(int width, int height, out string error)
    {
        error = "";
        if (_decoder is not null)
            return true;

        var args =
            "-hide_banner -loglevel error " +
            "-fflags nobuffer -flags low_delay " +
            "-f hevc -i pipe:0 " +
            "-f rawvideo -pix_fmt bgra pipe:1";

        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = args,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            _decoder = Process.Start(psi);
        }
        catch (Exception ex)
        {
            error = $"启动 ffmpeg 解码失败：{ex.Message}";
            return false;
        }
        if (_decoder is null)
        {
            error = "启动 ffmpeg 解码失败";
            return false;
        }

        _decoderIn = _decoder.StandardInput.BaseStream;
        _decoderOut = _decoder.StandardOutput.BaseStream;
        _ = Task.Run(async () => { try { await _decoder.StandardError.ReadToEndAsync(); } catch { } });

        var fb = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        Frame = fb;

        var cts = _streamCts;
        if (cts is null)
            return true;

        _ = Task.Run(() => DecodeLoopAsync(fb, width, height, _decoderOut!, cts.Token), cts.Token);
        return true;
    }

    private async Task DecodeLoopAsync(WriteableBitmap bitmap, int width, int height, Stream src, CancellationToken ct)
    {
        var frameSize = width * height * 4;
        var buf = new byte[frameSize];
        var sw = Stopwatch.StartNew();
        var frames = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = 0;
                while (read < frameSize)
                {
                    var n = await src.ReadAsync(buf, read, frameSize - read, ct);
                    if (n <= 0)
                        return;
                    read += n;
                }

                var copy = new byte[frameSize];
                Buffer.BlockCopy(buf, 0, copy, 0, frameSize);
                frames++;

                if (sw.ElapsedMilliseconds >= 1000)
                {
                    Fps = $"{frames:0} fps";
                    frames = 0;
                    sw.Restart();
                }

                _ = Dispatcher.UIThread.InvokeAsync(() => Blit(bitmap, width, height, copy));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private static unsafe void Blit(WriteableBitmap bitmap, int width, int height, byte[] frame)
    {
        using var fb = bitmap.Lock();
        var dstStride = fb.RowBytes;
        fixed (byte* srcPtr = frame)
        {
            var srcStride = width * 4;
            for (var y = 0; y < height; y++)
            {
                Buffer.MemoryCopy(
                    srcPtr + y * srcStride,
                    (byte*)fb.Address + y * dstStride,
                    dstStride,
                    srcStride);
            }
        }
    }

    private void OnDirectoryChanged(object? sender, EventArgs e) => RebuildDevices();

    private void RebuildDevices()
    {
        var selectedId = SelectedDevice?.Peer.Id;
        Devices.Clear();

        var localId = _settings?.Current.Identity.DeviceId;
        if (!string.IsNullOrWhiteSpace(localId))
        {
            var localPort = _localControlPort ?? _settings?.Current.Control.PreferredPort ?? 50051;
            var os = OperatingSystem.IsWindows() ? "windows" : (OperatingSystem.IsMacOS() ? "macos" : "unknown");
            var localPeer = new ResolvedPeer(
                Id: localId,
                Nickname: $"{_settings?.Current.Identity.Nickname ?? "本机"}（本机）",
                Tags: _settings?.Current.Identity.Tags ?? Array.Empty<string>(),
                Addresses: ["127.0.0.1"],
                ControlPort: localPort,
                Capabilities: ["remote"],
                Os: os,
                Ver: "0.1.0",
                LastSeenAt: DateTimeOffset.UtcNow,
                InstanceName: "local"
            );
            Devices.Add(new RemoteDeviceItem(localPeer.Nickname, localPeer));
        }

        if (_directory is null)
        {
            if (SelectedDevice is null && Devices.Count > 0)
                SelectedDevice = Devices[0];
            return;
        }

        var peers = _directory.Snapshot()
            .Select(r => r.Peer)
            .Where(p => p.Capabilities.Any(c => c.Equals("remote", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p.Nickname, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var p in peers)
        {
            var tags = p.Tags.Length > 0 ? string.Join(" / ", p.Tags) : "";
            var display = string.IsNullOrWhiteSpace(tags) ? $"{p.Nickname} · {p.Os}" : $"{p.Nickname}（{tags}） · {p.Os}";
            Devices.Add(new RemoteDeviceItem(display, p));
        }

        if (!string.IsNullOrWhiteSpace(selectedId))
        {
            var match = Devices.FirstOrDefault(d => string.Equals(d.Peer.Id, selectedId, StringComparison.Ordinal));
            if (match is not null)
            {
                SelectedDevice = match;
                return;
            }
        }

        if (SelectedDevice is null && Devices.Count > 0)
        {
            var nonLocal = Devices.FirstOrDefault(d => d.Peer.Addresses.Length == 0 || d.Peer.Addresses[0] != "127.0.0.1");
            SelectedDevice = nonLocal ?? Devices[0];
        }
    }
}
