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
using Avalonia.Input;
using System.Text;
using System.Buffers;
using System.Threading.Channels;
using System.Net.Http;
using XTransferTool.Config;
using XTransferTool.Discovery;
using XTransferTool.Control.Proto;
using Grpc.Core;
using Grpc.Net.Client;
using Serilog;

namespace XTransferTool.ViewModels;

public partial class RemoteDesktopViewModel : ViewModelBase
{
    public sealed record RemoteDeviceItem(string Display, ResolvedPeer Peer);

    private readonly IPeerDirectory? _directory;
    private readonly SettingsStore? _settings;
    private readonly int? _localControlPort;
    private string? _remoteAddress;
    private int _remotePort;
    private string? _localId;

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
    private CancellationTokenSource? _videoCts;
    private Task? _videoTask;
    private Task? _autoQualityTask;
    private long _inputSeq;
    private AsyncDuplexStreamingCall<RemoteInputEvent, RemoteInputAck>? _inputCall;
    private Channel<RemoteInputEvent>? _inputQueue;
    private CancellationTokenSource? _inputSendCts;
    private Task? _inputSendLoop;
    private Process? _decoder;
    private Stream? _decoderIn;
    private Stream? _decoderOut;
    private string? _sessionId;
    private string? _streamCodec;
    private int _decoderFrameSize;
    private byte[]? _pendingFrame;
    private int _uiBlitScheduled;
    private CancellationTokenSource? _mouseMoveCts;
    private int _pendingMouseX;
    private int _pendingMouseY;
    private int _hasPendingMouseMove;
    private int _lastSentMouseX;
    private int _lastSentMouseY;

    private int _e2eEmaMs;
    private int _e2eMinMs;
    private long _e2eMinUpdatedAtMs;
    private int _qualityTier;
    private long _lastQualityChangeMs;
    private int _videoGeneration;
    private long _lastResyncMs;

    private enum QualityTier
    {
        Smooth = 0,
        Balanced = 1,
        Clear = 2
    }

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
            _remoteAddress = address;
            _remotePort = peer.ControlPort;

            var localId = _settings?.Current.Identity.DeviceId;
            if (string.IsNullOrWhiteSpace(localId))
                localId = "local";
            _localId = localId;

            Log.Information("[remote-desktop] connect to {Address}:{Port}", address, peer.ControlPort);
            var controlChannel = CreateGrpcChannel(address, peer.ControlPort);
            var control = new RemoteControlService.RemoteControlServiceClient(controlChannel);

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
            StartInputSendLoop(_inputCall, _streamCts.Token);
            _ = DrainAcksAsync(_inputCall, _streamCts.Token);
            StartMouseMovePump(_streamCts.Token);
            EnqueueMouseButtonUp(0);
            EnqueueMouseButtonUp(1);

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

            _qualityTier = (int)QualityTier.Clear;
            _e2eEmaMs = 0;
            _e2eMinMs = 0;
            _e2eMinUpdatedAtMs = 0;
            _lastQualityChangeMs = 0;
            _lastResyncMs = 0;
            _videoGeneration = 0;

            await StartVideoAsync((QualityTier)_qualityTier, _streamCts.Token);
            _autoQualityTask = Task.Run(() => AutoQualityLoopAsync(_streamCts.Token), _streamCts.Token);
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(5000, _streamCts.Token);
                }
                catch
                {
                    return;
                }

                if (_streamCts.IsCancellationRequested)
                    return;
                if (Volatile.Read(ref _e2eEmaMs) <= 0)
                    Status = "连接超时：未收到视频流";
            }, _streamCts.Token);
        }
        catch (RpcException ex)
        {
            Status = $"{(string.IsNullOrWhiteSpace(targetDisplay) ? "" : targetDisplay + " - ")}{ex.StatusCode}: {ex.Status.Detail}";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[remote-desktop] connect failed");
            var msg = ex.GetBaseException().Message;
            Status = $"{(string.IsNullOrWhiteSpace(targetDisplay) ? "" : targetDisplay + " - ")}{msg}";
        }
    }

    [RelayCommand]
    private async Task Disconnect()
    {
        _statsCts?.Cancel();
        _streamCts?.Cancel();
        _videoCts?.Cancel();
        _mouseMoveCts?.Cancel();
        _inputSendCts?.Cancel();

        try
        {
            EnqueueMouseButtonUp(0);
            EnqueueMouseButtonUp(1);

            if (_inputCall is not null)
                await _inputCall.RequestStream.CompleteAsync();
        }
        catch
        {
        }

        ResetDecoder();

        _inputCall = null;
        _inputQueue = null;
        _inputSendCts = null;
        _inputSendLoop = null;
        _videoCts = null;
        _videoTask = null;
        _autoQualityTask = null;
        _decoder = null;
        _decoderIn = null;
        _decoderOut = null;
        _sessionId = null;
        _streamCodec = null;
        _decoderFrameSize = 0;
        _remoteAddress = null;
        _remotePort = 0;
        _localId = null;
        _e2eEmaMs = 0;
        _e2eMinMs = 0;
        _e2eMinUpdatedAtMs = 0;
        _qualityTier = 0;
        _lastQualityChangeMs = 0;
        _lastResyncMs = 0;
        _videoGeneration = 0;

        var pending = Interlocked.Exchange(ref _pendingFrame, null);
        if (pending is not null)
            ArrayPool<byte>.Shared.Return(pending);
        Interlocked.Exchange(ref _uiBlitScheduled, 0);

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
        if (_inputCall is null || string.IsNullOrWhiteSpace(_sessionId))
            return;
        Volatile.Write(ref _pendingMouseX, x);
        Volatile.Write(ref _pendingMouseY, y);
        Volatile.Write(ref _hasPendingMouseMove, 1);
    }

    public void SendMouseDown(int x, int y, byte button)
    {
        SendMouseMoveImmediate(x, y);
        SendMouseButton(button, isDown: true);
    }

    public void SendMouseUp(int x, int y, byte button)
    {
        SendMouseMoveImmediate(x, y);
        SendMouseButton(button, isDown: false);
    }

    public void SendMouseWheel(int x, int y, int deltaY, int deltaX)
    {
        if (string.IsNullOrWhiteSpace(_sessionId))
            return;

        SendMouseMoveImmediate(x, y);

        var payload = new byte[8];
        BitConverter.GetBytes(deltaY).CopyTo(payload, 0);
        BitConverter.GetBytes(deltaX).CopyTo(payload, 4);

        Enqueue(new RemoteInputEvent
        {
            SessionId = _sessionId,
            Seq = Interlocked.Increment(ref _inputSeq),
            TsMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Type = "mouseWheel",
            Payload = Google.Protobuf.ByteString.CopyFrom(payload)
        });
    }

    public void SendKeyDown(int windowsVk)
    {
        SendKey(windowsVk, isDown: true);
    }

    public void SendKeyUp(int windowsVk)
    {
        SendKey(windowsVk, isDown: false);
    }

    public void SendText(string text)
    {
        if (string.IsNullOrWhiteSpace(_sessionId))
            return;
        if (string.IsNullOrEmpty(text))
            return;

        var bytes = Encoding.UTF8.GetBytes(text);
        Enqueue(new RemoteInputEvent
        {
            SessionId = _sessionId,
            Seq = Interlocked.Increment(ref _inputSeq),
            TsMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Type = "text",
            Payload = Google.Protobuf.ByteString.CopyFrom(bytes)
        });
    }

    private void EnqueueMouseButtonUp(byte button)
    {
        if (string.IsNullOrWhiteSpace(_sessionId))
            return;

        Enqueue(new RemoteInputEvent
        {
            SessionId = _sessionId,
            Seq = Interlocked.Increment(ref _inputSeq),
            TsMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Type = "mouseUp",
            Payload = Google.Protobuf.ByteString.CopyFrom([button])
        });
    }

    private bool Enqueue(RemoteInputEvent ev)
    {
        var q = _inputQueue;
        if (q is null)
            return false;
        return q.Writer.TryWrite(ev);
    }

    private void StartInputSendLoop(AsyncDuplexStreamingCall<RemoteInputEvent, RemoteInputAck> call, CancellationToken ct)
    {
        _inputSendCts?.Cancel();
        _inputSendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _inputSendCts.Token;

        _inputQueue = Channel.CreateUnbounded<RemoteInputEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        _inputSendLoop = Task.Run(async () =>
        {
            try
            {
                await foreach (var ev in _inputQueue.Reader.ReadAllAsync(token))
                    await call.RequestStream.WriteAsync(ev);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Status = $"输入通道异常：{ex.Message}";
            }
        }, token);
    }

    private void SendMouseButton(byte button, bool isDown)
    {
        if (string.IsNullOrWhiteSpace(_sessionId))
            return;

        Enqueue(new RemoteInputEvent
        {
            SessionId = _sessionId,
            Seq = Interlocked.Increment(ref _inputSeq),
            TsMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Type = isDown ? "mouseDown" : "mouseUp",
            Payload = Google.Protobuf.ByteString.CopyFrom([button])
        });
    }

    private void SendMouseMoveImmediate(int x, int y)
    {
        if (string.IsNullOrWhiteSpace(_sessionId))
            return;

        var payload = new byte[8];
        BitConverter.GetBytes(x).CopyTo(payload, 0);
        BitConverter.GetBytes(y).CopyTo(payload, 4);

        Enqueue(new RemoteInputEvent
        {
            SessionId = _sessionId,
            Seq = Interlocked.Increment(ref _inputSeq),
            TsMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Type = "mouseMove",
            Payload = Google.Protobuf.ByteString.CopyFrom(payload)
        });

        _lastSentMouseX = x;
        _lastSentMouseY = y;
    }

    private void StartMouseMovePump(CancellationToken ct)
    {
        _mouseMoveCts?.Cancel();
        _mouseMoveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _mouseMoveCts.Token;

        _hasPendingMouseMove = 0;
        _lastSentMouseX = 0;
        _lastSentMouseY = 0;

        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(16));
            while (await timer.WaitForNextTickAsync(token))
            {
                if (Volatile.Read(ref _hasPendingMouseMove) == 0)
                    continue;

                var x = Volatile.Read(ref _pendingMouseX);
                var y = Volatile.Read(ref _pendingMouseY);
                Volatile.Write(ref _hasPendingMouseMove, 0);

                if (x == _lastSentMouseX && y == _lastSentMouseY)
                    continue;

                SendMouseMoveImmediate(x, y);
            }
        }, token);
    }

    private void SendKey(int windowsVk, bool isDown)
    {
        if (string.IsNullOrWhiteSpace(_sessionId))
            return;
        if (windowsVk <= 0 || windowsVk > 0xFF)
            return;

        var payload = new byte[4];
        BitConverter.GetBytes(windowsVk).CopyTo(payload, 0);
        Enqueue(new RemoteInputEvent
        {
            SessionId = _sessionId,
            Seq = Interlocked.Increment(ref _inputSeq),
            TsMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Type = isDown ? "keyDown" : "keyUp",
            Payload = Google.Protobuf.ByteString.CopyFrom(payload)
        });
    }

    public static int? TryMapKeyToWindowsVk(Key key)
    {
        if (key is Key.None)
            return null;

        if (key is >= Key.A and <= Key.Z)
            return 'A' + (key - Key.A);

        if (key is >= Key.D0 and <= Key.D9)
            return '0' + (key - Key.D0);

        return key switch
        {
            Key.Space => 0x20,
            Key.Enter => 0x0D,
            Key.Tab => 0x09,
            Key.Back => 0x08,
            Key.Escape => 0x1B,
            Key.Left => 0x25,
            Key.Up => 0x26,
            Key.Right => 0x27,
            Key.Down => 0x28,
            Key.Delete => 0x2E,
            Key.Insert => 0x2D,
            Key.Home => 0x24,
            Key.End => 0x23,
            Key.PageUp => 0x21,
            Key.PageDown => 0x22,
            Key.LeftShift => 0xA0,
            Key.RightShift => 0xA1,
            Key.LeftCtrl => 0xA2,
            Key.RightCtrl => 0xA3,
            Key.LeftAlt => 0xA4,
            Key.RightAlt => 0xA5,
            Key.LWin => 0x5B,
            Key.RWin => 0x5C,
            Key.F1 => 0x70,
            Key.F2 => 0x71,
            Key.F3 => 0x72,
            Key.F4 => 0x73,
            Key.F5 => 0x74,
            Key.F6 => 0x75,
            Key.F7 => 0x76,
            Key.F8 => 0x77,
            Key.F9 => 0x78,
            Key.F10 => 0x79,
            Key.F11 => 0x7A,
            Key.F12 => 0x7B,
            _ => null
        };
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

    private async Task StartVideoAsync(QualityTier tier, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_sessionId) || string.IsNullOrWhiteSpace(_remoteAddress) || string.IsNullOrWhiteSpace(_localId) || _remotePort <= 0)
            return;

        _videoCts?.Cancel();
        var gen = Interlocked.Increment(ref _videoGeneration);
        _videoCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _videoCts.Token;

        ResetDecoder();
        RemoteWidth = 0;
        RemoteHeight = 0;

        var (preset, fps) = tier switch
        {
            QualityTier.Clear => ("clear", 60),
            QualityTier.Balanced => ("balanced", 60),
            _ => ("smooth", 45)
        };

        var streamChannel = CreateGrpcChannel(_remoteAddress, _remotePort);
        var stream = new RemoteDesktopStreamService.RemoteDesktopStreamServiceClient(streamChannel);

        var call = stream.SubscribeHevcStream(new SubscribeHevcStreamRequest
        {
            SessionId = _sessionId,
            FromId = _localId,
            TargetFps = fps,
            MaxResolution = "1920x1080",
            QualityPreset = preset
        }, cancellationToken: token);

        _videoTask = Task.Run(() => ConsumeHevcAsync(call, token, gen), token);
        await Task.CompletedTask;
    }

    private static GrpcChannel CreateGrpcChannel(string host, int port)
    {
        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            KeepAlivePingDelay = TimeSpan.FromSeconds(20),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10)
        };

        return GrpcChannel.ForAddress($"http://{host}:{port}", new GrpcChannelOptions
        {
            HttpHandler = handler
        });
    }

    private async Task AutoQualityLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000));
        var lowStable = 0;
        var highStable = 0;
        var resyncStable = 0;

        while (await timer.WaitForNextTickAsync(ct))
        {
            var ema = Volatile.Read(ref _e2eEmaMs);
            if (ema <= 0)
                continue;

            if (ema >= 900)
                resyncStable++;
            else
                resyncStable = 0;

            if (ema >= 320)
            {
                highStable++;
                lowStable = 0;
            }
            else if (ema <= 160)
            {
                lowStable++;
                highStable = 0;
            }
            else
            {
                lowStable = 0;
                highStable = 0;
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var sinceChange = now - Volatile.Read(ref _lastQualityChangeMs);
            var sinceResync = now - Volatile.Read(ref _lastResyncMs);

            var current = (QualityTier)Volatile.Read(ref _qualityTier);
            if (resyncStable >= 2 && sinceResync >= 5000)
            {
                Volatile.Write(ref _lastResyncMs, now);
                Status = "自适应重同步";
                await StartVideoAsync(current, ct);
                resyncStable = 0;
                continue;
            }

            if (highStable >= 2 && sinceChange >= 4000)
            {
                if (current > QualityTier.Smooth)
                {
                    var next = (QualityTier)((int)current - 1);
                    Volatile.Write(ref _qualityTier, (int)next);
                    Volatile.Write(ref _lastQualityChangeMs, now);
                    Status = $"自适应降档：{next}";
                    await StartVideoAsync(next, ct);
                }
                highStable = 0;
            }

            if (lowStable >= 8 && sinceChange >= 8000)
            {
                if (current < QualityTier.Clear)
                {
                    var next = (QualityTier)((int)current + 1);
                    Volatile.Write(ref _qualityTier, (int)next);
                    Volatile.Write(ref _lastQualityChangeMs, now);
                    Status = $"自适应升档：{next}";
                    await StartVideoAsync(next, ct);
                }
                lowStable = 0;
            }
        }
    }

    private async Task ConsumeHevcAsync(AsyncServerStreamingCall<HevcStreamChunk> call, CancellationToken ct, int gen)
    {
        long bytesInWindow = 0;
        var windowStart = Stopwatch.StartNew();
        var gotAny = false;

        try
        {
            if (gen != Volatile.Read(ref _videoGeneration))
                return;

            while (await call.ResponseStream.MoveNext(ct))
            {
                if (gen != Volatile.Read(ref _videoGeneration))
                    return;

                var chunk = call.ResponseStream.Current;
                if (chunk is null)
                    continue;

                gotAny = true;
                if (RemoteWidth <= 0 || RemoteHeight <= 0)
                {
                    RemoteWidth = chunk.Width;
                    RemoteHeight = chunk.Height;
                    _streamCodec = string.IsNullOrWhiteSpace(chunk.Codec) ? "h264" : chunk.Codec.Trim().ToLowerInvariant();
                    if (!TryStartDecoder(RemoteWidth, RemoteHeight, _streamCodec, out var err))
                    {
                        Status = err;
                        return;
                    }
                }

                try
                {
                    var mem = chunk.Data.Memory;
                    bytesInWindow += mem.Length;
                    await _decoderIn!.WriteAsync(mem, ct);
                }
                catch (Exception ex)
                {
                    Status = $"解码管道异常：{ex.Message}";
                    return;
                }
                Status = "已连接";

                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var e2eRaw = (int)Math.Clamp(nowMs - chunk.TsMs, 0, 30_000);
                var min = Volatile.Read(ref _e2eMinMs);
                var minAt = Volatile.Read(ref _e2eMinUpdatedAtMs);

                if (min == 0 || e2eRaw < min || nowMs - minAt > 5000)
                {
                    Volatile.Write(ref _e2eMinMs, e2eRaw);
                    Volatile.Write(ref _e2eMinUpdatedAtMs, nowMs);
                    min = e2eRaw;
                }

                var e2eExcess = Math.Max(0, e2eRaw - min);
                var prev = Volatile.Read(ref _e2eEmaMs);
                var ema = prev == 0 ? e2eExcess : (prev * 9 + e2eExcess) / 10;
                Volatile.Write(ref _e2eEmaMs, ema);
                Latency = $"{ema} ms";

                if (windowStart.ElapsedMilliseconds >= 1000)
                {
                    var mbps = bytesInWindow * 8.0 / 1_000_000.0;
                    Bitrate = $"{mbps:0.#} Mbps";
                    bytesInWindow = 0;
                    windowStart.Restart();
                }
            }

            if (!ct.IsCancellationRequested)
            {
                if (!gotAny)
                    Status = "连接失败：未收到视频流";
                else if (Status == "已连接")
                    Status = "连接已断开";
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

    private void ResetDecoder()
    {
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

        _decoder = null;
        _decoderIn = null;
        _decoderOut = null;
        _streamCodec = null;
        _decoderFrameSize = 0;

        var pending = Interlocked.Exchange(ref _pendingFrame, null);
        if (pending is not null)
            ArrayPool<byte>.Shared.Return(pending);
        Interlocked.Exchange(ref _uiBlitScheduled, 0);

        Frame = null;
    }

    private bool TryStartDecoder(int width, int height, out string error)
    {
        return TryStartDecoder(width, height, "h264", out error);
    }

    private bool TryStartDecoder(int width, int height, string codec, out string error)
    {
        error = "";
        if (_decoder is not null)
            return true;

        var c = (codec ?? "").Trim().ToLowerInvariant();
        var fmt = c is "hevc" or "h265" ? "hevc" : "h264";

        var args =
            "-hide_banner -loglevel error " +
            "-fflags nobuffer -flags low_delay " +
            "-probesize 32 -analyzeduration 0 " +
            $"-f {fmt} -i pipe:0 " +
            "-f rawvideo -pix_fmt bgra pipe:1";

        var psi = new ProcessStartInfo
        {
            FileName = XTransferTool.Remote.FfmpegLocator.ResolveFfmpegPath(),
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
        _decoderFrameSize = width * height * 4;

        var cts = _streamCts;
        if (cts is null)
            return true;

        var gen = Volatile.Read(ref _videoGeneration);
        _ = Task.Run(() => DecodeLoopAsync(fb, width, height, _decoderOut!, cts.Token, gen), cts.Token);
        return true;
    }

    private async Task DecodeLoopAsync(WriteableBitmap bitmap, int width, int height, Stream src, CancellationToken ct, int gen)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (gen != Volatile.Read(ref _videoGeneration))
                    return;

                var frameSize = _decoderFrameSize;
                if (frameSize <= 0)
                    return;

                var frame = ArrayPool<byte>.Shared.Rent(frameSize);
                var read = 0;
                while (read < frameSize)
                {
                    var n = await src.ReadAsync(frame.AsMemory(read, frameSize - read), ct);
                    if (n <= 0)
                    {
                        ArrayPool<byte>.Shared.Return(frame);
                        return;
                    }
                    read += n;
                }

                var prev = Interlocked.Exchange(ref _pendingFrame, frame);
                if (prev is not null)
                    ArrayPool<byte>.Shared.Return(prev);

                if (Interlocked.Exchange(ref _uiBlitScheduled, 1) == 0)
                    _ = Dispatcher.UIThread.InvokeAsync(() => DrainPendingFrame(bitmap, width, height));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private void DrainPendingFrame(WriteableBitmap bitmap, int width, int height)
    {
        Interlocked.Exchange(ref _uiBlitScheduled, 0);
        var frame = Interlocked.Exchange(ref _pendingFrame, null);
        if (frame is null)
            return;

        try
        {
            Blit(bitmap, width, height, frame);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
        }

        if (_pendingFrame is not null && Interlocked.Exchange(ref _uiBlitScheduled, 1) == 0)
            _ = Dispatcher.UIThread.InvokeAsync(() => DrainPendingFrame(bitmap, width, height));
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
