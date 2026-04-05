using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Serilog;
using XTransferTool.Control.Proto;

namespace XTransferTool.Transfer;

public sealed class GrpcFileTransferClient
{
    private static void LogEndpoint(string op, string address, int port) =>
        Log.Information("[grpc-transfer] {Op} -> http://{Address}:{Port}", op, address, port);

    public async Task<CreateTransferResponse> CreateTransferAsync(string address, int port, CreateTransferRequest request, CancellationToken ct = default)
    {
        LogEndpoint(nameof(CreateTransferAsync), address, port);
        using var channel = GrpcChannel.ForAddress($"http://{address}:{port}");
        var client = new FileTransferService.FileTransferServiceClient(channel);
        return await client.CreateTransferAsync(request, cancellationToken: ct);
    }

    public async Task<UploadChunksResponse> UploadRandomAsync(
        string address,
        int port,
        string transferId,
        Google.Protobuf.ByteString uploadToken,
        string itemId,
        int chunkSizeBytes,
        int totalBytes,
        CancellationToken ct = default)
    {
        LogEndpoint(nameof(UploadRandomAsync), address, port);
        using var channel = GrpcChannel.ForAddress($"http://{address}:{port}");
        var client = new FileTransferService.FileTransferServiceClient(channel);
        using var call = client.UploadChunks(cancellationToken: ct);

        var buf = new byte[chunkSizeBytes];
        long offset = 0;
        var remaining = totalBytes;

        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            var n = Math.Min(remaining, chunkSizeBytes);
            Random.Shared.NextBytes(buf);

            await call.RequestStream.WriteAsync(new UploadChunkRequest
            {
                TransferId = transferId,
                UploadToken = uploadToken,
                ItemId = itemId,
                OffsetBytes = offset,
                Data = Google.Protobuf.ByteString.CopyFrom(buf, 0, n),
                IsLastChunk = (remaining - n) == 0
            });

            offset += n;
            remaining -= n;
        }

        await call.RequestStream.CompleteAsync();
        return await call.ResponseAsync;
    }

    public async Task<UploadChunksResponse> UploadFileAsync(
        string address,
        int port,
        string transferId,
        Google.Protobuf.ByteString uploadToken,
        string itemId,
        string filePath,
        int chunkSizeBytes,
        Action<long>? onProgress = null,
        CancellationToken ct = default)
    {
        LogEndpoint(nameof(UploadFileAsync), address, port);
        using var channel = GrpcChannel.ForAddress($"http://{address}:{port}");
        var client = new FileTransferService.FileTransferServiceClient(channel);
        using var call = client.UploadChunks(cancellationToken: ct);

        await using var fs = File.OpenRead(filePath);
        var buf = new byte[Math.Max(64 * 1024, chunkSizeBytes)];
        long offset = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var n = await fs.ReadAsync(buf, 0, chunkSizeBytes, ct);
            if (n <= 0)
                break;

            var isLast = fs.Position >= fs.Length;
            await call.RequestStream.WriteAsync(new UploadChunkRequest
            {
                TransferId = transferId,
                UploadToken = uploadToken,
                ItemId = itemId,
                OffsetBytes = offset,
                Data = Google.Protobuf.ByteString.CopyFrom(buf, 0, n),
                IsLastChunk = isLast
            });

            offset += n;
            onProgress?.Invoke(n);
        }

        await call.RequestStream.CompleteAsync();
        return await call.ResponseAsync;
    }

    public async Task<CompleteTransferResponse> CompleteTransferAsync(
        string address,
        int port,
        CompleteTransferRequest request,
        CancellationToken ct = default)
    {
        LogEndpoint(nameof(CompleteTransferAsync), address, port);
        using var channel = GrpcChannel.ForAddress($"http://{address}:{port}");
        var client = new FileTransferService.FileTransferServiceClient(channel);
        return await client.CompleteTransferAsync(request, cancellationToken: ct);
    }
}

