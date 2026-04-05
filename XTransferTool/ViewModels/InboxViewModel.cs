using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using XTransferTool.Ui;
using CommunityToolkit.Mvvm.Input;
using XTransferTool.Inbox;

namespace XTransferTool.ViewModels;

public partial class InboxViewModel : ViewModelBase
{
    private readonly IInboxRepository? _repo;

    public ObservableCollection<InboxItem> Items { get; } = [];

    [ObservableProperty]
    private string _statusFilter = "all"; // all/unread/read

    [ObservableProperty]
    private string _search = "";

    [ObservableProperty]
    private InboxItem? _selectedItem;

    public InboxViewModel(IInboxRepository? repo = null)
    {
        _repo = repo;
        if (_repo is not null)
            _repo.Changed += OnRepoChanged;
        _ = LoadAsync();
    }

    private void OnRepoChanged(object? sender, EventArgs e)
    {
        // Repo may raise from gRPC thread; marshal to UI thread.
        Dispatcher.UIThread.Post(() => _ = Refresh());
    }

    [RelayCommand]
    private async Task Refresh()
    {
        Items.Clear();

        if (_repo is null)
        {
            Items.Add(new InboxItem(DateTime.Now.AddMinutes(-6), "陈小红（会场1 / A片区）", "会议材料.zip", "请转发给会场2", "已保存"));
            Items.Add(new InboxItem(DateTime.Now.AddMinutes(-18), "李小军（会场2 / 左片区）", "开场视频.mp4", "大屏备用", "未读"));
            Items.Add(new InboxItem(DateTime.Now.AddHours(-2), "赵小明（会场1 / A片区）", "议程.pdf", "请帮忙投到主屏", "已读"));
            return;
        }

        var res = await _repo.QueryAsync(new QueryInboxRequest(
            Status: StatusFilter,
            Search: Search,
            FromId: null,
            Limit: 50,
            Offset: 0));

        foreach (var r in res.Items)
        {
            Items.Add(new InboxItem(
                r.InboxId,
                DateTimeOffset.FromUnixTimeMilliseconds(r.ReceivedAtMs).LocalDateTime,
                r.FromDisplay,
                r.FileName,
                r.Message,
                r.Status));
        }
    }

    private Task LoadAsync() => Refresh();

    partial void OnStatusFilterChanged(string value) => _ = Refresh();
    partial void OnSearchChanged(string value) => _ = Refresh();

    [RelayCommand]
    private async Task MarkSelectedRead()
    {
        if (_repo is null || SelectedItem is null)
            return;

        await _repo.MarkReadAsync(SelectedItem.InboxId);
        await Refresh();
    }
}

public partial class InboxItem : ObservableObject
{
    public InboxItem(DateTime time, string from, string fileName, string message, string status)
    {
        InboxId = Guid.NewGuid().ToString();
        Time = time;
        From = from;
        FileName = fileName;
        Message = message;
        Status = status;
        (FileTypeIconGeometry, FileTypeIconBrush) = FileTypeGlyph.CreateVisuals(fileName);
    }

    public InboxItem(string inboxId, DateTime time, string from, string fileName, string message, string status)
    {
        InboxId = inboxId;
        Time = time;
        From = from;
        FileName = fileName;
        Message = message;
        Status = status;
        (FileTypeIconGeometry, FileTypeIconBrush) = FileTypeGlyph.CreateVisuals(fileName);
    }

    public string InboxId { get; }
    public DateTime Time { get; }
    public string From { get; }
    public string FileName { get; }
    public string Message { get; }
    public string Status { get; }

    public Geometry FileTypeIconGeometry { get; }
    public IBrush FileTypeIconBrush { get; }
}

