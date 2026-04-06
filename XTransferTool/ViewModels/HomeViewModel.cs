using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XTransferTool.Config;
using XTransferTool.Control;
using XTransferTool.Control.Proto;
using XTransferTool.Discovery;
using XTransferTool.Inbox;
using XTransferTool.Transfer;

namespace XTransferTool.ViewModels;

public partial class HomeViewModel : ViewModelBase
{
    private readonly IPeerDirectory? _directory;
    private readonly IInboxRepository? _inbox;
    private readonly SettingsStore? _settings;
    private readonly GrpcMessagingClient _messaging = new();
    private readonly GrpcFileTransferClient _fileTransfer = new();

    public ObservableCollection<UserTreeNode> Users { get; } = [];

    [ObservableProperty]
    private string _searchQuery = "";

    [ObservableProperty]
    private int _visiblePeerCount;

    [ObservableProperty]
    private UserTreeNode? _selectedNode;

    [ObservableProperty]
    private string _selectedTargetHint = "请先在左侧选择标签或用户";

    private string? _selectedStableKey;

    public ObservableCollection<TransferItem> TransferQueue { get; } = [];
    public ObservableCollection<TransferItem> RecentSends { get; } = [];

    public ObservableCollection<string> DropFiles { get; } = [];

    [ObservableProperty]
    private string _dropHint = "拖拽文件到此处，或点击选择文件";

    public HomeViewModel(IPeerDirectory? directory = null, IInboxRepository? inbox = null, SettingsStore? settings = null)
    {
        _directory = directory;
        _inbox = inbox;
        _settings = settings;
        if (_directory is not null)
        {
            _directory.Changed += OnDirectoryChanged;
            RebuildTree();
        }
    }

    private (string FromId, string FromDisplay) SenderIdentity()
    {
        var cur = _settings?.Current;
        return (IdentityDisplayFormatter.SenderFromId(cur), IdentityDisplayFormatter.FormatSummary(cur));
    }

    partial void OnSelectedNodeChanged(UserTreeNode? value)
    {
        if (value is not null)
            _selectedStableKey = value.StableKey();

        SelectedTargetHint = value is null
            ? "请先在左侧选择标签或用户"
            : value.Kind == NodeKind.User
                ? $"目标：{value.Name}"
                : $"目标标签：{value.TagPathDisplay()}";

        // Dragging/clicking outside the TreeView may clear SelectedItem; restore it if we can.
        if (value is null && !string.IsNullOrWhiteSpace(_selectedStableKey))
        {
            var restored = FindByStableKey(Users, _selectedStableKey);
            if (restored is not null)
                SelectedNode = restored;
        }
    }

    public void SetDropFiles(IEnumerable<string> paths)
    {
        DropFiles.Clear();
        foreach (var p in paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct())
            DropFiles.Add(p);

        DropHint = DropFiles.Count == 0
            ? "拖拽文件到此处，或点击选择文件"
            : $"已选择 {DropFiles.Count} 个文件（后续将进入投递流程）";
    }

    public bool HasSelectedTarget()
        => SelectedNode is not null && (SelectedNode.Kind == NodeKind.Tag || (SelectedNode.Kind == NodeKind.User && !string.IsNullOrWhiteSpace(SelectedNode.PeerId)));

    public IReadOnlyList<ResolvedPeer> ResolveRecipientsSnapshot()
    {
        if (_directory is null || SelectedNode is null)
            return Array.Empty<ResolvedPeer>();

        var all = _directory.Snapshot().Select(r => r.Peer).ToList();

        if (SelectedNode.Kind == NodeKind.User && !string.IsNullOrWhiteSpace(SelectedNode.PeerId))
        {
            var one = all.FirstOrDefault(p => string.Equals(p.Id, SelectedNode.PeerId, StringComparison.OrdinalIgnoreCase));
            return one is null ? Array.Empty<ResolvedPeer>() : new[] { one };
        }

        // Tag node: match by prefix segments.
        var segs = SelectedNode.TagSegments ?? [];
        if (segs.Length == 0)
            return Array.Empty<ResolvedPeer>();

        bool Match(ResolvedPeer p)
        {
            var tags = p.Tags ?? [];
            if (segs.Length >= 1)
            {
                if (tags.Length < 1 || !string.Equals(tags[0], segs[0], StringComparison.Ordinal))
                    return false;
            }
            if (segs.Length >= 2)
            {
                if (tags.Length < 2 || !string.Equals(tags[1], segs[1], StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        return all.Where(Match).ToList();
    }

    public sealed record DeliveryTargetSnapshot(
        NodeKind Kind,
        string Display,
        string? TagDisplay,
        IReadOnlyList<ResolvedPeer> Recipients);

    public DeliveryTargetSnapshot? CreateDeliveryTargetSnapshot()
    {
        if (!HasSelectedTarget() || SelectedNode is null)
            return null;

        var recipients = ResolveRecipientsSnapshot();
        if (recipients.Count == 0)
            return new DeliveryTargetSnapshot(
                SelectedNode.Kind,
                SelectedNode.Kind == NodeKind.User ? $"目标：{SelectedNode.Name}" : $"目标标签：{SelectedNode.TagPathDisplay()}",
                SelectedNode.Kind == NodeKind.Tag ? SelectedNode.TagPathDisplay() : null,
                Array.Empty<ResolvedPeer>());

        return new DeliveryTargetSnapshot(
            SelectedNode.Kind,
            SelectedNode.Kind == NodeKind.User ? $"目标：{recipients[0].Nickname}" : $"目标标签：{SelectedNode.TagPathDisplay()}",
            SelectedNode.Kind == NodeKind.Tag ? SelectedNode.TagPathDisplay() : null,
            recipients);
    }

    public async Task SendFilesToSelectedAsync(IReadOnlyList<string> filePaths, string message)
    {
        if (_directory is null)
            return;

        var recipients = ResolveRecipientsSnapshot();
        if (recipients.Count == 0)
            return;

        // For now, send to each peer individually (server supports per-peer transfer).
        foreach (var peer in recipients)
        {
            if (filePaths.Count == 0)
                continue;

            var queueItem = new TransferItem(
                fileName: filePaths.Count == 1 ? Path.GetFileName(filePaths[0]) : $"{filePaths.Count} 个文件",
                target: $"发送到：{peer.Nickname}",
                progress: 0,
                state: "准备中");
            TransferQueue.Insert(0, queueItem);

            try
            {
                var address = peer.Addresses?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a)) ?? "";
                if (string.IsNullOrWhiteSpace(address))
                    throw new IOException("peer address missing");

                // CreateTransfer for multiple items.
                var (fromId, fromDisplay) = SenderIdentity();
                var req = new CreateTransferRequest
                {
                    RequestId = Guid.NewGuid().ToString(),
                    FromId = fromId,
                    FromDisplay = fromDisplay,
                    ToPeerId = peer.Id,
                    Message = message ?? string.Empty,
                    OverwritePolicy = "ask",
                    WantResume = false
                };

                var items = new List<(string ItemId, string Path, long Size)>();
                foreach (var p in filePaths)
                {
                    var fi = new FileInfo(p);
                    if (!fi.Exists)
                        continue;
                    var itemId = Guid.NewGuid().ToString();
                    items.Add((itemId, p, fi.Length));
                    req.Items.Add(new TransferItemMeta
                    {
                        ItemId = itemId,
                        FileName = fi.Name,
                        SizeBytes = fi.Length,
                        MtimeMs = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds()
                    });
                }

                if (req.Items.Count == 0)
                    throw new IOException("no existing files");

                queueItem.State = "创建传输…";
                var res = await _fileTransfer.CreateTransferAsync(address, peer.ControlPort, req);
                if (!res.Accepted)
                    throw new IOException($"createTransfer rejected: {res.Reason}");

                long totalBytes = items.Sum(i => Math.Max(0, i.Size));
                long sentBytes = 0;

                foreach (var it in items)
                {
                    queueItem.State = $"上传中：{Path.GetFileName(it.Path)}";
                    await _fileTransfer.UploadFileAsync(
                        address,
                        peer.ControlPort,
                        transferId: res.TransferId,
                        uploadToken: res.UploadToken,
                        itemId: it.ItemId,
                        filePath: it.Path,
                        chunkSizeBytes: res.ChunkSizeBytes,
                        onProgress: (acceptedDelta) =>
                        {
                            sentBytes += acceptedDelta;
                            queueItem.Progress = totalBytes <= 0 ? 0 : Math.Clamp((double)sentBytes / totalBytes, 0, 1);
                        });
                }

                queueItem.State = "完成…";
                var complete = new CompleteTransferRequest
                {
                    TransferId = res.TransferId,
                    UploadToken = res.UploadToken
                };
                foreach (var it in items)
                {
                    complete.Items.Add(new CompletedItem
                    {
                        ItemId = it.ItemId,
                        SizeBytes = it.Size
                    });
                }

                var completeRes = await _fileTransfer.CompleteTransferAsync(address, peer.ControlPort, complete);
                if (!string.Equals(completeRes.Status, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"completeTransfer failed: {completeRes.Status} {completeRes.Reason}");

                queueItem.Progress = 1;
                queueItem.State = "已完成";

                RecentSends.Insert(0, new TransferItem(
                    fileName: queueItem.FileName,
                    target: queueItem.Target,
                    progress: 1,
                    state: "已完成"));
            }
            catch (Exception ex)
            {
                queueItem.State = $"失败：{ex.Message}";
            }
        }
    }

    public async Task SendFilesToRecipientsAsync(IReadOnlyList<ResolvedPeer> recipients, IReadOnlyList<string> filePaths, string message)
    {
        if (_directory is null)
            return;
        if (recipients.Count == 0)
            return;

        // For now, send to each peer individually (server supports per-peer transfer).
        foreach (var peer in recipients)
        {
            if (filePaths.Count == 0)
                continue;

            var queueItem = new TransferItem(
                fileName: filePaths.Count == 1 ? Path.GetFileName(filePaths[0]) : $"{filePaths.Count} 个文件",
                target: $"发送到：{peer.Nickname}",
                progress: 0,
                state: "准备中");
            TransferQueue.Insert(0, queueItem);

            try
            {
                var address = peer.Addresses?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a)) ?? "";
                if (string.IsNullOrWhiteSpace(address))
                    throw new IOException("peer address missing");

                var (fromIdR, fromDisplayR) = SenderIdentity();
                var req = new CreateTransferRequest
                {
                    RequestId = Guid.NewGuid().ToString(),
                    FromId = fromIdR,
                    FromDisplay = fromDisplayR,
                    ToPeerId = peer.Id,
                    Message = message ?? string.Empty,
                    OverwritePolicy = "ask",
                    WantResume = false
                };

                var items = new List<(string ItemId, string Path, long Size)>();
                foreach (var p in filePaths)
                {
                    var fi = new FileInfo(p);
                    if (!fi.Exists)
                        continue;
                    var itemId = Guid.NewGuid().ToString();
                    items.Add((itemId, p, fi.Length));
                    req.Items.Add(new TransferItemMeta
                    {
                        ItemId = itemId,
                        FileName = fi.Name,
                        SizeBytes = fi.Length,
                        MtimeMs = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds()
                    });
                }

                if (req.Items.Count == 0)
                    throw new IOException("no existing files");

                queueItem.State = "创建传输…";
                var res = await _fileTransfer.CreateTransferAsync(address, peer.ControlPort, req);
                if (!res.Accepted)
                    throw new IOException($"createTransfer rejected: {res.Reason}");

                long totalBytes = items.Sum(i => Math.Max(0, i.Size));
                long sentBytes = 0;

                foreach (var it in items)
                {
                    queueItem.State = $"上传中：{Path.GetFileName(it.Path)}";
                    await _fileTransfer.UploadFileAsync(
                        address,
                        peer.ControlPort,
                        transferId: res.TransferId,
                        uploadToken: res.UploadToken,
                        itemId: it.ItemId,
                        filePath: it.Path,
                        chunkSizeBytes: res.ChunkSizeBytes,
                        onProgress: (acceptedDelta) =>
                        {
                            sentBytes += acceptedDelta;
                            queueItem.Progress = totalBytes <= 0 ? 0 : Math.Clamp((double)sentBytes / totalBytes, 0, 1);
                        });
                }

                queueItem.State = "完成…";
                var complete = new CompleteTransferRequest
                {
                    TransferId = res.TransferId,
                    UploadToken = res.UploadToken
                };
                foreach (var it in items)
                {
                    complete.Items.Add(new CompletedItem
                    {
                        ItemId = it.ItemId,
                        SizeBytes = it.Size
                    });
                }

                var completeRes = await _fileTransfer.CompleteTransferAsync(address, peer.ControlPort, complete);
                if (!string.Equals(completeRes.Status, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"completeTransfer failed: {completeRes.Status} {completeRes.Reason}");

                queueItem.Progress = 1;
                queueItem.State = "已完成";

                RecentSends.Insert(0, new TransferItem(
                    fileName: queueItem.FileName,
                    target: queueItem.Target,
                    progress: 1,
                    state: "已完成"));
            }
            catch (Exception ex)
            {
                queueItem.State = $"失败：{ex.Message}";
            }
        }
    }

    partial void OnSearchQueryChanged(string value) => RebuildTree();

    [RelayCommand]
    private void RefreshDiscovery()
    {
        AppServices.RefreshDiscovery();
        RebuildTree();
    }

    private void OnDirectoryChanged(object? sender, System.EventArgs e) => RebuildTree();

    private void RebuildTree()
    {
        if (_directory is null)
            return;

        // Preserve UI expansion state across rebuilds.
        var expandedKeys = CaptureExpandedKeys(Users);

        var peers = _directory.Snapshot();
        var q = (SearchQuery ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(q))
        {
            peers = peers.Where(r =>
            {
                if (r.Peer.Nickname.Contains(q, StringComparison.OrdinalIgnoreCase))
                    return true;
                foreach (var t in r.Peer.Tags ?? [])
                {
                    if (t.Contains(q, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }).ToList();
        }

        var venueMap = new Dictionary<string, UserTreeNode>(StringComparer.Ordinal);

        void EnsurePath(string venue, string area, UserTreeNode userNode)
        {
            if (!venueMap.TryGetValue(venue, out var venueNode))
            {
                venueNode = new UserTreeNode(venue, NodeKind.Tag)
                {
                    TagSegments = [venue]
                };
                venueNode.IsExpanded = expandedKeys.Contains(KeyFor(venueNode));
                venueMap[venue] = venueNode;
            }

            var areaNode = venueNode.Children.FirstOrDefault(c => c.Kind == NodeKind.Tag && c.Name == area);
            if (areaNode is null)
            {
                areaNode = new UserTreeNode(area, NodeKind.Tag)
                {
                    TagSegments = [venue, area]
                };
                areaNode.IsExpanded = expandedKeys.Contains(KeyFor(venueNode, areaNode));
                venueNode.Children.Add(areaNode);
            }

            areaNode.Children.Add(userNode);
        }

        var next = new List<UserTreeNode>();
        VisiblePeerCount = peers.Count;

        foreach (var rec in peers)
        {
            var tags = rec.Peer.Tags ?? [];
            var venue = tags.Length >= 1 && !string.IsNullOrWhiteSpace(tags[0]) ? tags[0] : "未分会场";
            var area = tags.Length >= 2 && !string.IsNullOrWhiteSpace(tags[1]) ? tags[1] : "未分片区";
            var status = rec.Presence == PeerPresenceState.Online ? "在线" : "可能离线";
            var userNode = new UserTreeNode(rec.Peer.Nickname, NodeKind.User)
            {
                Status = status,
                PeerId = rec.Peer.Id,
                TagSegments = [venue, area]
            };
            EnsurePath(venue, area, userNode);
        }

        next.AddRange(venueMap.Values.OrderBy(v => v.Name, StringComparer.Ordinal));

        Users.Clear();
        foreach (var n in next)
            Users.Add(n);

        // Keep selection if possible (by stable key).
        if (!string.IsNullOrWhiteSpace(_selectedStableKey))
            SelectedNode = FindByStableKey(Users, _selectedStableKey);
    }

    private static UserTreeNode? FindByStableKey(IEnumerable<UserTreeNode> roots, string key)
    {
        foreach (var v in roots)
        {
            if (v.StableKey() == key)
                return v;
            foreach (var a in v.Children)
            {
                if (a.StableKey() == key)
                    return a;
                foreach (var u in a.Children)
                {
                    if (u.StableKey() == key)
                        return u;
                }
            }
        }
        return null;
    }

    private static HashSet<string> CaptureExpandedKeys(IEnumerable<UserTreeNode> roots)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);

        foreach (var venue in roots)
        {
            if (venue.Kind == NodeKind.Tag && venue.IsExpanded)
                set.Add(KeyFor(venue));

            foreach (var area in venue.Children)
            {
                if (area.Kind == NodeKind.Tag && area.IsExpanded)
                    set.Add(KeyFor(venue, area));
            }
        }

        return set;
    }

    private static string KeyFor(UserTreeNode venue) => $"v:{venue.Name}";
    private static string KeyFor(UserTreeNode venue, UserTreeNode area) => $"v:{venue.Name}/a:{area.Name}";
}

public enum NodeKind
{
    Tag,
    User
}

public partial class UserTreeNode : ObservableObject
{
    public UserTreeNode(string name, NodeKind kind)
    {
        Name = name;
        Kind = kind;
    }

    public string Name { get; }
    public NodeKind Kind { get; }

    [ObservableProperty]
    private string _status = "在线";

    [ObservableProperty]
    private bool _isExpanded;

    // If Kind==User this is the peer id. If Kind==Tag it is null.
    public string? PeerId { get; init; }

    // Tag path segments (0..2) used for filtering and display.
    public string[] TagSegments { get; init; } = [];

    public ObservableCollection<UserTreeNode> Children { get; set; } = [];

    public string TagPathDisplay()
        => TagSegments is { Length: > 0 } ? string.Join(" / ", TagSegments) : Name;

    public string StableKey()
        => Kind == NodeKind.User
            ? $"u:{PeerId ?? Name}"
            : $"t:{TagPathDisplay()}";
}

public partial class TransferItem : ObservableObject
{
    public TransferItem(string fileName, string target, double progress, string state)
    {
        FileName = fileName;
        Target = target;
        Progress = progress;
        State = state;
    }

    public string FileName { get; }
    public string Target { get; }

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _state;
}

