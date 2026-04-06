using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using XTransferTool.Config;
using XTransferTool.Control.Proto;

namespace XTransferTool.Remote;

public sealed class RemoteControlServiceImpl : RemoteControlService.RemoteControlServiceBase
{
    private readonly RemoteSessionStore _sessions;
    private readonly SettingsStore? _settings;
    private readonly ConcurrentDictionary<string, BlockingCollection<IceCandidate>> _iceQueues = new(StringComparer.Ordinal);

    public RemoteControlServiceImpl(RemoteSessionStore sessions, SettingsStore? settings = null)
    {
        _sessions = sessions;
        _settings = settings;
    }

    public override Task<CreateRemoteSessionResponse> CreateSession(CreateRemoteSessionRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId) ||
            string.IsNullOrWhiteSpace(request.FromId) ||
            string.IsNullOrWhiteSpace(request.ToPeerId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "requestId/fromId/toPeerId required"));

        var rec = _sessions.GetOrCreate(request.RequestId, () => new RemoteSessionRecord(
            SessionId: Guid.NewGuid().ToString(),
            FromId: request.FromId,
            ToPeerId: request.ToPeerId,
            Mode: request.Mode,
            CreatedAt: DateTimeOffset.UtcNow,
            SdpOffer: "",
            SdpAnswer: ""
        ));

        _iceQueues.TryAdd(rec.SessionId, new BlockingCollection<IceCandidate>(boundedCapacity: 2048));

        return Task.FromResult(new CreateRemoteSessionResponse
        {
            Accepted = true,
            SessionId = rec.SessionId,
            Reason = ""
        });
    }

    public override Task<RemoteOfferResponse> Offer(RemoteOfferRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId) || string.IsNullOrWhiteSpace(request.FromId) || string.IsNullOrWhiteSpace(request.SdpOffer))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "sessionId/fromId/sdpOffer required"));

        if (!_sessions.TryGet(request.SessionId, out _))
            return Task.FromResult(new RemoteOfferResponse { Accepted = false, Reason = "unknown sessionId" });

        // V1 stub answer (real implementation plugs WebRTC).
        var answer = "v=0\r\n" +
                     "o=xtransfer 0 0 IN IP4 127.0.0.1\r\n" +
                     "s=xtransfer-remote\r\n" +
                     "t=0 0\r\n";

        _sessions.UpdateOfferAnswer(request.SessionId, request.SdpOffer, answer);

        return Task.FromResult(new RemoteOfferResponse
        {
            Accepted = true,
            SdpAnswer = answer,
            Reason = ""
        });
    }

    public override async Task TrickleIce(IAsyncStreamReader<IceCandidate> requestStream, IServerStreamWriter<IceCandidate> responseStream, ServerCallContext context)
    {
        // Simple relay: any received candidate is echoed back to other side via per-session queue.
        // V1: since we don't track peers, we just echo back to the same stream.
        await foreach (var cand in requestStream.ReadAllAsync(context.CancellationToken))
        {
            await responseStream.WriteAsync(cand);
        }
    }

    public override async Task InputStream(IAsyncStreamReader<RemoteInputEvent> requestStream, IServerStreamWriter<RemoteInputAck> responseStream, ServerCallContext context)
    {
        if (_settings is not null && !_settings.Current.Remote.AllowRemoteControl)
            throw new RpcException(new Status(StatusCode.PermissionDenied, "remote control disabled"));

        await foreach (var ev in requestStream.ReadAllAsync(context.CancellationToken))
        {
            var accepted = InputInjector.TryApply(ev, out var reason);
            await responseStream.WriteAsync(new RemoteInputAck
            {
                Seq = ev.Seq,
                Accepted = accepted,
                Reason = reason
            });
        }
    }

    public override async Task SubscribeStats(SubscribeRemoteStatsRequest request, IServerStreamWriter<RemoteStatsEnvelope> responseStream, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "sessionId required"));

        var interval = request.IntervalMs <= 0 ? 500 : request.IntervalMs;
        interval = Math.Clamp(interval, 100, 2000); // clamp per doc: min 100ms, default 500ms

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(interval));
        while (await timer.WaitForNextTickAsync(context.CancellationToken))
        {
            var env = new RemoteStatsEnvelope
            {
                TsMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                RttMs = 9,
                Fps = 60,
                BitrateMbps = 18,
                PacketLoss = 0.0f,
                Resolution = "1920x1080"
            };
            await responseStream.WriteAsync(env);
        }
    }

    private static class InputInjector
    {
        public static bool TryApply(RemoteInputEvent ev, out string reason)
        {
            reason = "";
            var type = (ev.Type ?? "").Trim();
            if (type.Length == 0)
            {
                reason = "missing type";
                return false;
            }

            if (!OperatingSystem.IsWindows())
            {
                reason = "platform not supported";
                return false;
            }

            var payload = ev.Payload.Memory.Span;
            if (type.Equals("mouseMove", StringComparison.OrdinalIgnoreCase))
            {
                if (payload.Length < 8)
                {
                    reason = "payload too small";
                    return false;
                }

                var x = BitConverter.ToInt32(payload.Slice(0, 4));
                var y = BitConverter.ToInt32(payload.Slice(4, 4));
                return WindowsSendInput.MouseMove(x, y, out reason);
            }

            if (type.Equals("mouseDown", StringComparison.OrdinalIgnoreCase))
            {
                var button = payload.Length > 0 ? payload[0] : (byte)0;
                return WindowsSendInput.MouseButton(button, isDown: true, out reason);
            }

            if (type.Equals("mouseUp", StringComparison.OrdinalIgnoreCase))
            {
                var button = payload.Length > 0 ? payload[0] : (byte)0;
                return WindowsSendInput.MouseButton(button, isDown: false, out reason);
            }

            if (type.Equals("keyDown", StringComparison.OrdinalIgnoreCase))
            {
                if (payload.Length < 4)
                {
                    reason = "payload too small";
                    return false;
                }

                var vk = BitConverter.ToInt32(payload.Slice(0, 4));
                return WindowsSendInput.Key(vk, isDown: true, out reason);
            }

            if (type.Equals("keyUp", StringComparison.OrdinalIgnoreCase))
            {
                if (payload.Length < 4)
                {
                    reason = "payload too small";
                    return false;
                }

                var vk = BitConverter.ToInt32(payload.Slice(0, 4));
                return WindowsSendInput.Key(vk, isDown: false, out reason);
            }

            if (type.Equals("text", StringComparison.OrdinalIgnoreCase))
            {
                var text = System.Text.Encoding.UTF8.GetString(payload);
                if (text.Length == 0)
                {
                    reason = "empty text";
                    return false;
                }

                return WindowsSendInput.Text(text, out reason);
            }

            reason = "unsupported type";
            return false;
        }
    }

    private static class WindowsSendInput
    {
        private const int INPUT_MOUSE = 0;
        private const int INPUT_KEYBOARD = 1;
        private const uint MOUSEEVENTF_MOVE = 0x0001;
        private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;

        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;
        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public int type;
            public INPUTUNION u;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            [FieldOffset(0)]
            public MOUSEINPUT mi;
            [FieldOffset(0)]
            public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint cInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        public static bool MouseMove(int x, int y, out string reason)
        {
            reason = "";
            var vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
            var vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            var vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            var vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (vw <= 1 || vh <= 1)
            {
                reason = "virtual screen size unavailable";
                return false;
            }

            var clampedX = Math.Clamp(x, vx, vx + vw - 1);
            var clampedY = Math.Clamp(y, vy, vy + vh - 1);

            var absX = (int)Math.Round((clampedX - vx) * 65535.0 / (vw - 1));
            var absY = (int)Math.Round((clampedY - vy) * 65535.0 / (vh - 1));

            var input = new INPUT
            {
                type = INPUT_MOUSE,
                u = new INPUTUNION
                {
                    mi = new MOUSEINPUT
                    {
                        dx = absX,
                        dy = absY,
                        mouseData = 0,
                        dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };

            var sent = SendInput(1, [input], Marshal.SizeOf<INPUT>());
            if (sent != 1)
            {
                reason = $"SendInput failed: {Marshal.GetLastWin32Error()}";
                return false;
            }

            return true;
        }

        public static bool MouseButton(byte button, bool isDown, out string reason)
        {
            reason = "";
            uint flag = button switch
            {
                1 => isDown ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
                _ => isDown ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP
            };

            var input = new INPUT
            {
                type = INPUT_MOUSE,
                u = new INPUTUNION
                {
                    mi = new MOUSEINPUT
                    {
                        dx = 0,
                        dy = 0,
                        mouseData = 0,
                        dwFlags = flag,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };

            var sent = SendInput(1, [input], Marshal.SizeOf<INPUT>());
            if (sent != 1)
            {
                reason = $"SendInput failed: {Marshal.GetLastWin32Error()}";
                return false;
            }

            return true;
        }

        public static bool Key(int vk, bool isDown, out string reason)
        {
            reason = "";
            if (vk <= 0 || vk > 0xFF)
            {
                reason = "bad vk";
                return false;
            }

            var flags = isDown ? 0u : KEYEVENTF_KEYUP;
            if (IsExtendedKey(vk))
                flags |= KEYEVENTF_EXTENDEDKEY;

            var input = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = (ushort)vk,
                        wScan = 0,
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };

            var sent = SendInput(1, [input], Marshal.SizeOf<INPUT>());
            if (sent != 1)
            {
                reason = $"SendInput failed: {Marshal.GetLastWin32Error()}";
                return false;
            }

            return true;
        }

        public static bool Text(string text, out string reason)
        {
            reason = "";
            try
            {
                foreach (var ch in text)
                {
                    if (!UnicodeChar(ch, isDown: true, out reason))
                        return false;
                    if (!UnicodeChar(ch, isDown: false, out reason))
                        return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return false;
            }
        }

        private static bool UnicodeChar(char ch, bool isDown, out string reason)
        {
            reason = "";
            var flags = KEYEVENTF_UNICODE | (isDown ? 0u : KEYEVENTF_UNICODE | KEYEVENTF_KEYUP);
            var input = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = ch,
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };

            var sent = SendInput(1, [input], Marshal.SizeOf<INPUT>());
            if (sent != 1)
            {
                reason = $"SendInput failed: {Marshal.GetLastWin32Error()}";
                return false;
            }

            return true;
        }

        private static bool IsExtendedKey(int vk) => vk switch
        {
            0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E => true,
            _ => false
        };
    }
}

