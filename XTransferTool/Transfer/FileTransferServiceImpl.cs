using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using XTransferTool.Control.Proto;
using XTransferTool.Config;
using XTransferTool.Inbox;

namespace XTransferTool.Transfer;

public sealed class FileTransferServiceImpl : FileTransferService.FileTransferServiceBase
{
    private readonly TransferStore _store;
    private readonly UploadStateStore _uploadStates;
    private readonly IInboxRepository _inbox;
    private readonly XTransferTool.Control.InMemoryEventBus _bus;
    private readonly SettingsStore? _settings;

    public FileTransferServiceImpl(TransferStore store, UploadStateStore uploadStates, IInboxRepository inbox, XTransferTool.Control.InMemoryEventBus bus, SettingsStore? settings = null)
    {
        _store = store;
        _uploadStates = uploadStates;
        _inbox = inbox;
        _bus = bus;
        _settings = settings;
    }

    public override Task<CreateTransferResponse> CreateTransfer(CreateTransferRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "requestId required"));
        if (string.IsNullOrWhiteSpace(request.FromId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "fromId required"));
        if (request.ToCase == CreateTransferRequest.ToOneofCase.None)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "to required"));
        if (request.Message is { Length: > 500 })
            throw new RpcException(new Status(StatusCode.InvalidArgument, "message too long"));
        if (request.Items.Count == 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "items required"));

        var response = _store.GetOrCreate(request.RequestId, () =>
        {
            var transferId = Guid.NewGuid().ToString();
            var token = TransferStore.NewUploadToken();
            var chunkSize = NegotiateChunkSize(request);
            var map = request.Items
                .Where(i => !string.IsNullOrWhiteSpace(i.ItemId))
                .GroupBy(i => i.ItemId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().FileName ?? "", StringComparer.Ordinal);

            var to = request.ToCase switch
            {
                CreateTransferRequest.ToOneofCase.ToPeerId => $"peer:{request.ToPeerId}",
                CreateTransferRequest.ToOneofCase.ToTagPath => $"tag:{string.Join("/", request.ToTagPath.Segments)}",
                _ => "unknown"
            };

            var fromDisplay = string.IsNullOrWhiteSpace(request.FromDisplay)
                ? request.FromId
                : request.FromDisplay.Trim();

            _store.PutRecord(new TransferRecord(
                TransferId: transferId,
                FromId: request.FromId,
                FromDisplay: fromDisplay,
                To: to,
                Message: request.Message ?? string.Empty,
                ItemIdToFileName: map,
                CreatedAt: DateTimeOffset.UtcNow,
                ChunkSizeBytes: chunkSize,
                UploadToken: token
            ));

            return new CreateTransferResponse
            {
                Accepted = true,
                TransferId = transferId,
                UploadToken = Google.Protobuf.ByteString.CopyFrom(token),
                ChunkSizeBytes = chunkSize,
                Reason = ""
            };
        });

        return Task.FromResult(response);
    }

    private static int NegotiateChunkSize(CreateTransferRequest request)
    {
        // Step-doc: 1–4MB and power-of-two recommended. Keep simple:
        // <= 64MB total -> 1MB, <= 512MB -> 2MB, else 4MB
        long total = 0;
        foreach (var i in request.Items)
            total += Math.Max(0, i.SizeBytes);

        const int mb = 1024 * 1024;
        if (total <= 64L * mb) return 1 * mb;
        if (total <= 512L * mb) return 2 * mb;
        return 4 * mb;
    }

    public override async Task<UploadChunksResponse> UploadChunks(IAsyncStreamReader<UploadChunkRequest> requestStream, ServerCallContext context)
    {
        UploadItemState? state = null;
        string? transferId = null;
        string? itemId = null;
        int chunkSizeBytes = 1024 * 1024;

        try
        {
            while (await requestStream.MoveNext(context.CancellationToken))
            {
                var req = requestStream.Current;

                if (transferId is null)
                {
                    transferId = req.TransferId;
                    itemId = req.ItemId;

                    if (string.IsNullOrWhiteSpace(transferId) || string.IsNullOrWhiteSpace(itemId))
                        return Fail("failed", "transferId/itemId required");

                    // In V1 we don't persist transfer records securely; just require token non-empty.
                    if (req.UploadToken.IsEmpty)
                        return Fail("failed", "uploadToken required");

                    // For demo, chunkSize is not stored; default 1MB.
                    chunkSizeBytes = 1024 * 1024;

                    var savePath = System.IO.Path.Combine(AppContext.BaseDirectory, ".recv", transferId, $"{itemId}.part");
                    state = _uploadStates.GetOrCreate(transferId, itemId, () => new UploadItemState(savePath));
                    state.EnsureOpen();
                }

                if (req.TransferId != transferId || req.ItemId != itemId)
                    return Fail("failed", "do not interleave items in one stream");

                if (req.Data.IsEmpty)
                    continue;

                var expected = state!.ExpectedOffset();
                if (req.OffsetBytes != expected)
                {
                    return new UploadChunksResponse
                    {
                        AcceptedBytes = expected,
                        NextOffsetBytes = expected,
                        Status = "need-resume",
                        Reason = "offset mismatch"
                    };
                }

                // offset alignment (except last chunk)
                if (!req.IsLastChunk && (req.OffsetBytes % chunkSizeBytes) != 0)
                    return Fail("failed", "offset must be chunk-aligned");

                // write chunk
                var bytes = req.Data.ToByteArray(); // V1: one copy; can optimize later
                state.Accept(bytes, bytes.Length);
            }

            var accepted = state?.ExpectedOffset() ?? 0;
            state?.Flush();

            return new UploadChunksResponse
            {
                AcceptedBytes = accepted,
                NextOffsetBytes = accepted,
                Status = "ok",
                Reason = ""
            };
        }
        catch (OperationCanceledException)
        {
            var accepted = state?.ExpectedOffset() ?? 0;
            return new UploadChunksResponse
            {
                AcceptedBytes = accepted,
                NextOffsetBytes = accepted,
                Status = "failed",
                Reason = "cancelled"
            };
        }
    }

    private static UploadChunksResponse Fail(string status, string reason)
        => new()
        {
            AcceptedBytes = 0,
            NextOffsetBytes = 0,
            Status = status,
            Reason = reason
        };

    public override async Task<CompleteTransferResponse> CompleteTransfer(CompleteTransferRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.TransferId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "transferId required"));
        if (request.UploadToken.IsEmpty)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "uploadToken required"));
        if (request.Items.Count == 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "items required"));

        if (!_store.TryGetRecord(request.TransferId, out var record))
            return new CompleteTransferResponse { Status = "failed", Reason = "unknown transferId" };

        if (!request.UploadToken.Span.SequenceEqual(record.UploadToken))
            return new CompleteTransferResponse { Status = "failed", Reason = "invalid token" };

        var resp = new CompleteTransferResponse { Status = "ok", Reason = "" };

        foreach (var item in request.Items)
        {
            var receipt = new ItemReceipt
            {
                ItemId = item.ItemId,
                SavedPath = "",
                Deduped = false,
                Error = ""
            };

            var path = _uploadStates.TryGetPath(request.TransferId, item.ItemId);
            if (path is null)
            {
                resp.Status = "need-resume";
                receipt.Error = "missing upload state";
                resp.Receipts.Add(receipt);
                continue;
            }

            var fileName = record.ItemIdToFileName.TryGetValue(item.ItemId, out var n) ? n : (item.ItemId + ".bin");
            fileName = SanitizeFileName(fileName);

            var finalPath = TryMoveToReceiveFolder(path, fileName);
            receipt.SavedPath = finalPath;

            // Write inbox record (V1: one record per completed item)
            Console.WriteLine($"[transfer] complete add inbox file={fileName} path={finalPath} msgLen={(record.Message ?? "").Length}");
            await _inbox.AddRecordAsync(new InboxItemRecord(
                InboxId: Guid.NewGuid().ToString(),
                ReceivedAtMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                FromId: record.FromId,
                FromDisplay: string.IsNullOrWhiteSpace(record.FromDisplay) ? record.FromId : record.FromDisplay,
                TransferId: record.TransferId,
                FileName: fileName,
                SavedPath: finalPath,
                Message: record.Message ?? "",
                Status: "unread"
            ), context.CancellationToken);

            // Push event (best-effort; session routing will be refined later)
            var display = string.IsNullOrWhiteSpace(record.FromDisplay) ? record.FromId : record.FromDisplay;
            var ev = new FileReceivedEvent
            {
                FromId = record.FromId,
                FromDisplay = display,
                TransferId = record.TransferId,
                FileName = fileName,
                SavedPath = finalPath,
                Message = record.Message,
                TsMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            _bus.Publish("local", "inbox", ev.ToByteArray());

            resp.Receipts.Add(receipt);
        }

        return resp;
    }

    private string TryMoveToReceiveFolder(string tempPath, string fileName)
    {
        var folder = _settings?.Current.Receive.DefaultFolder;
        if (string.IsNullOrWhiteSpace(folder))
            folder = Path.Combine(AppContext.BaseDirectory, "data", "recv");

        Directory.CreateDirectory(folder);
        var dest = Path.Combine(folder, fileName);
        dest = EnsureUniquePath(dest);

        // Best effort move; fall back to keeping temp file.
        try
        {
            if (File.Exists(dest))
                dest = EnsureUniquePath(dest);
            File.Move(tempPath, dest);
            return dest;
        }
        catch
        {
            return tempPath;
        }
    }

    private static string EnsureUniquePath(string path)
    {
        if (!File.Exists(path))
            return path;

        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 1; i <= 9999; i++)
        {
            var p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p))
                return p;
        }
        return Path.Combine(dir, $"{name} ({Guid.NewGuid():N}){ext}");
    }

    private static string SanitizeFileName(string fileName)
    {
        var n = string.IsNullOrWhiteSpace(fileName) ? "file.bin" : fileName.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            n = n.Replace(c, '_');
        return n;
    }
}

