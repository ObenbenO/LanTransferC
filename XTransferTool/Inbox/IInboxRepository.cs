using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace XTransferTool.Inbox;

public sealed record InboxItemRecord(
    string InboxId,
    long ReceivedAtMs,
    string FromId,
    string FromDisplay,
    string TransferId,
    string FileName,
    string SavedPath,
    string Message,
    string Status
);

public sealed record QueryInboxRequest(
    string Status,
    string Search,
    string? FromId,
    int Limit,
    int Offset
);

public sealed record QueryInboxResponse(
    IReadOnlyList<InboxItemRecord> Items,
    int? Total
);

public interface IInboxRepository
{
    event System.EventHandler? Changed;
    Task AddRecordAsync(InboxItemRecord record, CancellationToken ct = default);
    Task<QueryInboxResponse> QueryAsync(QueryInboxRequest request, CancellationToken ct = default);
    Task MarkReadAsync(string inboxId, CancellationToken ct = default);
}

