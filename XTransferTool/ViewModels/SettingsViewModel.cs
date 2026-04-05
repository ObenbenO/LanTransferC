using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XTransferTool.Config;
using Avalonia;
using Avalonia.Styling;

namespace XTransferTool.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsStore? _store;
    private CancellationTokenSource? _saveCts;

    [ObservableProperty]
    private string _nickname = "";

    [ObservableProperty]
    private string _tags = "";

    [ObservableProperty]
    private string _downloadFolder = "";

    [ObservableProperty]
    private bool _enableDiscovery = true;

    [ObservableProperty]
    private string _theme = "Dark";

    [ObservableProperty]
    private bool _allowRemoteControl = true;

    [ObservableProperty]
    private string _saveFeedback = "";

    public string ConfigPath => _store?.SettingsPath ?? "(not initialized)";

    public Func<Task<string?>>? PickFolderAsync { get; set; }

    public SettingsViewModel(SettingsStore? store = null)
    {
        _store = store;
        LoadFromStore();
    }

    private void LoadFromStore()
    {
        if (_store is null)
        {
            Nickname = "赵小明";
            Tags = "会场1, A片区";
            DownloadFolder = "";
            EnableDiscovery = true;
            Theme = "Dark";
            AllowRemoteControl = true;
            return;
        }

        var s = _store.Current;
        Nickname = s.Identity.Nickname;
        Tags = string.Join(", ", s.Identity.Tags);
        DownloadFolder = s.Receive.DefaultFolder;
        EnableDiscovery = s.Network.EnableDiscovery;
        Theme = s.Appearance.Theme;
        AllowRemoteControl = s.Remote.AllowRemoteControl;
    }

    partial void OnNicknameChanged(string value) => DebouncedSave();
    partial void OnTagsChanged(string value) => DebouncedSave();
    partial void OnDownloadFolderChanged(string value) => DebouncedSave();
    partial void OnEnableDiscoveryChanged(bool value) => DebouncedSave();
    partial void OnThemeChanged(string value) => DebouncedSave();
    partial void OnAllowRemoteControlChanged(bool value) => DebouncedSave();

    [RelayCommand]
    private async Task SaveNow()
    {
        _saveCts?.Cancel();
        await SaveInternalAsync(CancellationToken.None);
        SaveFeedback = "已保存";
        _ = ClearFeedbackLaterAsync();
    }

    [RelayCommand]
    private async Task BrowseFolder()
    {
        if (PickFolderAsync is null)
            return;

        var picked = await PickFolderAsync();
        if (!string.IsNullOrWhiteSpace(picked))
            DownloadFolder = picked;
    }

    private void DebouncedSave()
    {
        if (_store is null)
            return;

        _saveCts?.Cancel();
        _saveCts = new CancellationTokenSource();
        var token = _saveCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(400, token);
                await SaveInternalAsync(token);
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    private async Task SaveInternalAsync(CancellationToken ct)
    {
        if (_store is null)
            return;

        var tags = (Tags ?? "")
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(8)
            .ToArray();

        await _store.UpdateAsync(s =>
        {
            s.Identity.Nickname = (Nickname ?? "").Trim();
            s.Identity.Tags = tags;
            s.Receive.DefaultFolder = (DownloadFolder ?? "").Trim();
            s.Network.EnableDiscovery = EnableDiscovery;
            s.Appearance.Theme = (Theme ?? "Dark").Trim();
            s.Remote.AllowRemoteControl = AllowRemoteControl;
        }, ct);

        ApplyThemeVariant(Theme);
    }

    private static void ApplyThemeVariant(string? theme)
    {
        if (Application.Current is null)
            return;

        var t = (theme ?? "Dark").Trim();
        Application.Current.RequestedThemeVariant = t switch
        {
            "Light" => ThemeVariant.Light,
            "Default" => ThemeVariant.Default,
            _ => ThemeVariant.Dark
        };
    }

    private async Task ClearFeedbackLaterAsync()
    {
        try
        {
            await Task.Delay(1200);
            SaveFeedback = "";
        }
        catch
        {
            // ignore
        }
    }
}

