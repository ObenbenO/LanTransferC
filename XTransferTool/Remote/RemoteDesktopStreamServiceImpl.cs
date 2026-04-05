using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Serilog;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using XTransferTool.Config;
using XTransferTool.Control.Proto;

namespace XTransferTool.Remote;

public sealed class RemoteDesktopStreamServiceImpl : RemoteDesktopStreamService.RemoteDesktopStreamServiceBase
{
    private readonly SettingsStore? _settings;

    public RemoteDesktopStreamServiceImpl(SettingsStore? settings = null)
    {
        _settings = settings;
    }

    public override async Task SubscribeHevcStream(SubscribeHevcStreamRequest request, IServerStreamWriter<HevcStreamChunk> responseStream, ServerCallContext context)
    {
        try
        {
            if (_settings is not null && !_settings.Current.Remote.AllowRemoteControl)
                throw new RpcException(new Status(StatusCode.PermissionDenied, "remote control disabled"));

            var targetFps = request.TargetFps <= 0 ? 30 : Math.Clamp(request.TargetFps, 1, 60);
            using var capturer = DesktopCapturer.Create();

            var width = capturer.Width;
            var height = capturer.Height;

            using var encoder = FfmpegHevcEncoder.Start(width, height, targetFps, request.QualityPreset ?? "");

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            var captureTask = Task.Run(() => CapturePumpAsync(capturer, encoder, targetFps, linkedCts.Token), linkedCts.Token);

            var seq = 0L;
            var buf = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                while (true)
                {
                    var n = await encoder.Stdout.ReadAsync(buf, 0, buf.Length, context.CancellationToken);
                    if (n <= 0)
                        break;

                    await responseStream.WriteAsync(new HevcStreamChunk
                    {
                        Seq = Interlocked.Increment(ref seq),
                        TsMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        Data = ByteString.CopyFrom(buf, 0, n),
                        Width = width,
                        Height = height,
                        Codec = "hevc"
                    }, context.CancellationToken);
                }

                if (seq == 0 && !context.CancellationToken.IsCancellationRequested)
                {
                    var err = await encoder.ReadStderrAsync();
                    var detail = string.IsNullOrWhiteSpace(err) ? "encoder produced no output" : err;
                    if (detail.Length > 1200)
                        detail = detail.Substring(0, 1200);
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, detail));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
                linkedCts.Cancel();
                try { await captureTask; } catch { }
            }
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[remote-desktop] SubscribeHevcStream failed");
            throw new RpcException(new Status(StatusCode.Unknown, ex.Message));
        }
        finally
        {
        }
    }

    private static async Task CapturePumpAsync(DesktopCapturer capturer, FfmpegHevcEncoder encoder, int fps, CancellationToken ct)
    {
        var frameBytes = capturer.Width * capturer.Height * 4;
        var frame = ArrayPool<byte>.Shared.Rent(frameBytes);
        try
        {
            var frameIntervalMs = 1000.0 / Math.Max(1, fps);
            var sw = Stopwatch.StartNew();
            long nextTick = 0;

            while (!ct.IsCancellationRequested)
            {
                var elapsedMs = sw.Elapsed.TotalMilliseconds;
                if (elapsedMs < nextTick)
                {
                    var delay = (int)Math.Max(0, Math.Min(50, nextTick - elapsedMs));
                    if (delay > 0)
                        await Task.Delay(delay, ct);
                    continue;
                }

                capturer.CaptureBgraPacked(frame);
                await encoder.Stdin.WriteAsync(frame, 0, frameBytes, ct);

                nextTick += (long)frameIntervalMs;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
            try { encoder.Stdin.Close(); } catch { }
        }
    }

    private sealed class DesktopCapturer : IDisposable
    {
        private readonly IDesktopCapturer _impl;

        public int Width => _impl.Width;
        public int Height => _impl.Height;

        private DesktopCapturer(IDesktopCapturer impl)
        {
            _impl = impl;
        }

        public static DesktopCapturer Create()
        {
            if (OperatingSystem.IsWindows())
                return new DesktopCapturer(new DxgiDesktopDuplicator());

            if (OperatingSystem.IsMacOS())
                return new DesktopCapturer(ScreenCaptureKitHelperCapturer.StartDefault());

            throw new PlatformNotSupportedException("Desktop capture is not supported on this OS.");
        }

        public void CaptureBgraPacked(byte[] dest)
        {
            _impl.CaptureBgraPacked(dest);
        }

        public void Dispose()
        {
            _impl.Dispose();
        }
    }

    private interface IDesktopCapturer : IDisposable
    {
        int Width { get; }
        int Height { get; }
        void CaptureBgraPacked(byte[] dest);
    }

    private sealed class ScreenCaptureKitHelperCapturer : IDesktopCapturer
    {
        private readonly Process _proc;
        private readonly Stream _stdout;
        private readonly int _frameBytes;

        public int Width { get; }
        public int Height { get; }

        private ScreenCaptureKitHelperCapturer(Process proc, int width, int height)
        {
            _proc = proc;
            _stdout = proc.StandardOutput.BaseStream;
            Width = width;
            Height = height;
            _frameBytes = width * height * 4;
        }

        public static ScreenCaptureKitHelperCapturer StartDefault()
        {
            var baseDir = AppContext.BaseDirectory;
            var exe1 = Path.Combine(baseDir, "native", "macos", "sck_capture", "sck_capture");
            var exe2 = Path.Combine(baseDir, "native", "macos", "sck_capture");
            var exe = File.Exists(exe1) ? exe1 : exe2;
            if (!File.Exists(exe))
                throw new RpcException(new Status(StatusCode.FailedPrecondition, $"缺少屏幕采集组件：{exe1}"));

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            Process? proc;
            try
            {
                proc = Process.Start(psi);
            }
            catch (Exception ex)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, $"启动屏幕采集组件失败：{ex.Message}"));
            }
            if (proc is null)
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "启动屏幕采集组件失败"));

            var stderrTask = Task.Run(async () => { try { return await proc.StandardError.ReadToEndAsync(); } catch { return ""; } });

            var header = new byte[8];
            try
            {
                ReadExact(proc.StandardOutput.BaseStream, header, 0, 8);
            }
            catch
            {
                var err = "";
                try
                {
                    err = stderrTask.IsCompleted ? stderrTask.Result : "";
                }
                catch
                {
                }

                if (!string.IsNullOrWhiteSpace(err))
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, err.Trim()));

                throw new RpcException(new Status(StatusCode.FailedPrecondition, "屏幕采集组件无输出（请检查 macOS 屏幕录制权限）"));
            }
            var width = BitConverter.ToInt32(header, 0);
            var height = BitConverter.ToInt32(header, 4);
            if (width <= 0 || height <= 0)
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "屏幕采集组件返回了无效的尺寸"));

            return new ScreenCaptureKitHelperCapturer(proc, width, height);
        }

        public void CaptureBgraPacked(byte[] dest)
        {
            if (dest.Length < _frameBytes)
                throw new ArgumentException("dest too small", nameof(dest));

            ReadExact(_stdout, dest, 0, _frameBytes);
        }

        private static void ReadExact(Stream s, byte[] buf, int offset, int count)
        {
            var read = 0;
            while (read < count)
            {
                var n = s.Read(buf, offset + read, count - read);
                if (n <= 0)
                    throw new EndOfStreamException();
                read += n;
            }
        }

        public void Dispose()
        {
            try { _stdout.Dispose(); } catch { }
            try
            {
                if (!_proc.HasExited)
                    _proc.Kill(entireProcessTree: true);
            }
            catch
            {
            }
            try { _proc.Dispose(); } catch { }
        }
    }

    private sealed class FfmpegHevcEncoder : IDisposable
    {
        private readonly Process _proc;
        private readonly Task<string> _stderrTask;

        public Stream Stdin { get; }
        public Stream Stdout { get; }

        private FfmpegHevcEncoder(Process proc)
        {
            _proc = proc;
            Stdin = proc.StandardInput.BaseStream;
            Stdout = proc.StandardOutput.BaseStream;
            _stderrTask = Task.Run(async () =>
            {
                try { return await proc.StandardError.ReadToEndAsync(); } catch { return ""; }
            });
        }

        public static FfmpegHevcEncoder Start(int width, int height, int fps, string qualityPreset)
        {
            var encoder = ChooseEncoder();
            var (bitrate, preset) = QualityToParams(qualityPreset);

            var args =
                $"-hide_banner -loglevel error " +
                $"-fflags nobuffer -flags low_delay " +
                $"-f rawvideo -pix_fmt bgra -video_size {width}x{height} -framerate {fps} -i pipe:0 " +
                $"{encoder} {preset} {bitrate} -bf 0 -g {Math.Max(1, fps * 2)} -pix_fmt yuv420p " +
                $"-f hevc pipe:1";

            var psi = new ProcessStartInfo
            {
                FileName = FfmpegLocator.ResolveFfmpegPath(),
                Arguments = args,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            Process? proc;
            try
            {
                proc = Process.Start(psi);
            }
            catch (Exception ex)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, $"ffmpeg 启动失败：{ex.Message}"));
            }
            if (proc is null)
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "ffmpeg 启动失败"));

            return new FfmpegHevcEncoder(proc);
        }

        private static string ChooseEncoder()
        {
            if (OperatingSystem.IsWindows())
                return "-c:v hevc_nvenc";
            if (OperatingSystem.IsMacOS())
                return "-c:v hevc_videotoolbox";
            return "-c:v libx265";
        }

        private static (string Bitrate, string Preset) QualityToParams(string preset)
        {
            var p = (preset ?? "").Trim().ToLowerInvariant();
            return p switch
            {
                "clear" or "清晰" => ("-b:v 12M -maxrate 16M -bufsize 24M", "-preset p3"),
                "balanced" or "平衡" => ("-b:v 8M -maxrate 10M -bufsize 16M", "-preset p2"),
                _ => ("-b:v 5M -maxrate 7M -bufsize 10M", "-preset p1")
            };
        }

        public async Task<string> ReadStderrAsync()
        {
            try { return (await _stderrTask).Trim(); } catch { return ""; }
        }

        public void Dispose()
        {
            try { Stdin.Dispose(); } catch { }
            try { Stdout.Dispose(); } catch { }

            try
            {
                if (!_proc.HasExited)
                    _proc.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            try { _proc.Dispose(); } catch { }
        }
    }

    private sealed class DxgiDesktopDuplicator : IDesktopCapturer
    {
        private readonly IDXGIFactory1 _factory;
        private readonly IDXGIAdapter1 _adapter;
        private readonly IDXGIOutput _output;
        private readonly IDXGIOutput1 _output1;
        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _ctx;
        private IDXGIOutputDuplication? _dup;
        private ID3D11Texture2D? _staging;

        public int Width { get; private set; }
        public int Height { get; private set; }

        public DxgiDesktopDuplicator()
        {
            _factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            _factory.EnumAdapters1(0, out var adapter);
            _adapter = adapter;
            _adapter.EnumOutputs(0, out var output);
            _output = output;
            _output1 = _output.QueryInterface<IDXGIOutput1>();

            _device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
            _ctx = _device.ImmediateContext;
            ResetDuplication();
        }

        public void CaptureBgraPacked(byte[] dest)
        {
            var expected = Width * Height * 4;
            if (dest.Length < expected)
                throw new ArgumentException("dest too small", nameof(dest));

            try
            {
                EnsureDuplication();

                var dup = _dup!;
                var hr = dup.AcquireNextFrame(50, out _, out var res);
                if (hr == Vortice.DXGI.ResultCode.WaitTimeout)
                    return;

                hr.CheckError();
                using (res)
                {
                    using var tex = res.QueryInterface<ID3D11Texture2D>();
                    EnsureStaging(tex.Description.Width, tex.Description.Height, tex.Description.Format);

                    _ctx.CopyResource(_staging!, tex);
                    var map = _ctx.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                    try
                    {
                        var srcStride = map.RowPitch;
                        var dstStride = Width * 4;
                        unsafe
                        {
                            fixed (byte* dstPtr = dest)
                            {
                                var src = (byte*)map.DataPointer;
                                for (var y = 0; y < Height; y++)
                                {
                                    Buffer.MemoryCopy(
                                        src + y * srcStride,
                                        dstPtr + y * dstStride,
                                        dstStride,
                                        dstStride);
                                }
                            }
                        }
                    }
                    finally
                    {
                        _ctx.Unmap(_staging!, 0);
                    }
                }
                dup.ReleaseFrame();
            }
            catch
            {
                ResetDuplication();
            }
        }

        private void EnsureDuplication()
        {
            if (_dup is not null)
                return;
            ResetDuplication();
        }

        private void ResetDuplication()
        {
            try { _dup?.Dispose(); } catch { }
            _dup = null;
            try { _staging?.Dispose(); } catch { }
            _staging = null;

            var desc = _output.Description;
            var r = desc.DesktopCoordinates;
            Width = Math.Max(1, r.Right - r.Left);
            Height = Math.Max(1, r.Bottom - r.Top);

            _dup = _output1.DuplicateOutput(_device);
        }

        private void EnsureStaging(uint width, uint height, Vortice.DXGI.Format format)
        {
            if (_staging is not null && Width == (int)width && Height == (int)height)
                return;

            Width = (int)width;
            Height = (int)height;

            _staging?.Dispose();
            _staging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None
            });
        }

        public void Dispose()
        {
            try { _staging?.Dispose(); } catch { }
            try { _dup?.Dispose(); } catch { }
            _ctx.Dispose();
            _device.Dispose();
            _output1.Dispose();
            _output.Dispose();
            _adapter.Dispose();
            _factory.Dispose();
        }
    }
}
