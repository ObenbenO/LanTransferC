using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using XTransferTool.Config;
using XTransferTool.Inbox;

namespace XTransferTool.ViewModels;

public enum AppPage
{
    Home,
    Inbox,
    RemoteDesktop,
    Settings
}

public partial class NavItem : ObservableObject
{
    public NavItem(AppPage page, string title, string iconGlyph)
    {
        Page = page;
        Title = title;
        IconGlyph = iconGlyph;
    }

    public AppPage Page { get; }
    public string Title { get; }
    public string IconGlyph { get; }
}

public partial class MainWindowViewModel : ViewModelBase
{
    public ObservableCollection<NavItem> NavigationItems { get; } =
    [
        new NavItem(AppPage.Home, "主页", "⌂"),
        new NavItem(AppPage.Inbox, "收件箱", "⇣"),
        new NavItem(AppPage.RemoteDesktop, "远程桌面", "▣"),
        new NavItem(AppPage.Settings, "设置", "⚙"),
    ];

    [ObservableProperty]
    private NavItem? _selectedNavItem;

    [ObservableProperty]
    private ViewModelBase? _currentPage;

    private readonly Discovery.IPeerDirectory? _peerDirectory;
    private readonly IInboxRepository? _inbox;
    private readonly SettingsStore? _settings;

    [ObservableProperty]
    private string _identitySummary = "";

    public MainWindowViewModel(Discovery.IPeerDirectory? peerDirectory = null, IInboxRepository? inboxRepository = null, SettingsStore? settingsStore = null)
    {
        _peerDirectory = peerDirectory;
        _inbox = inboxRepository;
        _settings = settingsStore;
        _identitySummary = BuildIdentitySummary(_settings?.Current);

        if (_settings is not null)
            _settings.Changed += OnSettingsChanged;

        SelectedNavItem = NavigationItems[0];
        CurrentPage = new HomeViewModel(_peerDirectory, _inbox, _settings);
    }

    partial void OnSelectedNavItemChanged(NavItem? value)
    {
        if (value is null)
            return;

        CurrentPage = value.Page switch
        {
            AppPage.Home => new HomeViewModel(_peerDirectory, _inbox, _settings),
            AppPage.Inbox => new InboxViewModel(_inbox),
            AppPage.RemoteDesktop => new RemoteDesktopViewModel(_peerDirectory, _settings, AppServices.ControlPort),
            AppPage.Settings => new SettingsViewModel(_settings),
            _ => CurrentPage
        };
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        IdentitySummary = BuildIdentitySummary(settings);
    }

    private static string BuildIdentitySummary(AppSettings? s) => IdentityDisplayFormatter.FormatSummary(s);
}
