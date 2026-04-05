using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace XTransferTool.Inbox;

public sealed class InMemoryInboxRepository : IInboxRepository
{
    private readonly object _gate = new();
    private readonly List<InboxItemRecord> _items = [];

    public event EventHandler? Changed;

    public Task AddRecordAsync(InboxItemRecord record, CancellationToken ct = default)
    {
        lock (_gate)
            _items.Add(record);
        Console.WriteLine($"[inbox] add id={record.InboxId} file={record.FileName} status={record.Status} total={_items.Count}");
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task<QueryInboxResponse> QueryAsync(QueryInboxRequest request, CancellationToken ct = default)
    {
        IEnumerable<InboxItemRecord> q;
        lock (_gate)
            q = _items.ToArray();

        if (!string.IsNullOrWhiteSpace(request.FromId))
            q = q.Where(i => i.FromId == request.FromId);

        var status = request.Status?.Trim().ToLowerInvariant() ?? "all";
        if (status is "unread" or "read")
            q = q.Where(i => string.Equals(i.Status, status, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var s = request.Search.Trim();
            q = q.Where(i =>
                (i.FileName?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (i.FromDisplay?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (i.Message?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        q = q.OrderByDescending(i => i.ReceivedAtMs)
            .Skip(Math.Max(0, request.Offset))
            .Take(Math.Clamp(request.Limit, 1, 200));

        var list = q.ToList();
        Console.WriteLine($"[inbox] query status={request.Status} search={(request.Search ?? "").Trim()} -> {list.Count} items (total={_items.Count})");
        return Task.FromResult(new QueryInboxResponse(list, Total: null));
    }

    public Task MarkReadAsync(string inboxId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var idx = _items.FindIndex(i => i.InboxId == inboxId);
            if (idx >= 0)
            {
                var item = _items[idx];
                _items[idx] = item with { Status = "read" };
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }
}

