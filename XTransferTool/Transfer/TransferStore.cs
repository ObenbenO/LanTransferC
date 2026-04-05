using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using XTransferTool.Control.Proto;

namespace XTransferTool.Transfer;

public sealed class TransferStore
{
    private readonly ConcurrentDictionary<string, CreateTransferResponse> _byRequestId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TransferRecord> _byTransferId = new(StringComparer.Ordinal);

    public CreateTransferResponse GetOrCreate(string requestId, Func<CreateTransferResponse> factory)
        => _byRequestId.GetOrAdd(requestId, _ => factory());

    public void PutRecord(TransferRecord record) => _byTransferId[record.TransferId] = record;

    public bool TryGetRecord(string transferId, out TransferRecord record)
        => _byTransferId.TryGetValue(transferId, out record!);

    public static byte[] NewUploadToken()
    {
        var token = new byte[32];
        RandomNumberGenerator.Fill(token);
        return token;
    }
}

public sealed record TransferRecord(
    string TransferId,
    string FromId,
    string FromDisplay,
    string To,
    string Message,
    IReadOnlyDictionary<string, string> ItemIdToFileName,
    DateTimeOffset CreatedAt,
    int ChunkSizeBytes,
    byte[] UploadToken
);

