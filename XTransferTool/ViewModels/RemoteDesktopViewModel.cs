using System.Collections.ObjectModel;
using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Threading;
using System.Threading.Tasks;
using XTransferTool.Control.Proto;
using Grpc.Net.Client;

namespace XTransferTool.ViewModels;

public partial class RemoteDesktopViewModel : ViewModelBase
{
    public ObservableCollection<string> Devices { get; } =
    [
        "陈小红（会场1 / A片区） · Windows",
        "李小军（会场2 / 左片区） · macOS",
        "投影电脑（会场1 / 主控） · Windows",
    ];

    [ObservableProperty]
    private string _selectedDevice = "陈小红（会场1 / A片区） · Windows";

    [ObservableProperty]
    private string _latency = "9 ms";

    [ObservableProperty]
    private string _fps = "60 fps";

    [ObservableProperty]
    private string _bitrate = "18 Mbps";

    private CancellationTokenSource? _statsCts;
    private long _inputSeq;

    [RelayCommand]
    private async Task Connect()
    {
        _statsCts?.Cancel();
        _statsCts = new CancellationTokenSource();

        // V1 demo: local remote-control service.
        using var channel = GrpcChannel.ForAddress("http://127.0.0.1:50051");
        var client = new RemoteControlService.RemoteControlServiceClient(channel);

        var create = await client.CreateSessionAsync(new CreateRemoteSessionRequest
        {
            RequestId = System.Guid.NewGuid().ToString(),
            FromId = "local",
            ToPeerId = "local",
            Mode = "viewOnly",
            Preferred = new RemotePreference { QualityPreset = "smooth", MaxResolution = "1920x1080" }
        });

        using var stats = client.SubscribeStats(new SubscribeRemoteStatsRequest
        {
            SessionId = create.SessionId,
            FromId = "local",
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
    }

    [RelayCommand]
    private Task Disconnect()
    {
        _statsCts?.Cancel();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task SendDemoInput()
    {
        using var channel = GrpcChannel.ForAddress("http://127.0.0.1:50051");
        var client = new RemoteControlService.RemoteControlServiceClient(channel);

        using var call = client.InputStream(cancellationToken: CancellationToken.None);
        await call.RequestStream.WriteAsync(new RemoteInputEvent
        {
            SessionId = "demo",
            Seq = Interlocked.Increment(ref _inputSeq),
            TsMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Type = "mouseMove",
            Payload = Google.Protobuf.ByteString.CopyFrom(new byte[] { 0x01, 0x02 })
        });
        await call.RequestStream.CompleteAsync();

        // Drain acks (optional)
        while (await call.ResponseStream.MoveNext(CancellationToken.None))
        {
            // no-op
        }
    }
}

