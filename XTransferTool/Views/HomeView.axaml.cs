using Avalonia.Input;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Platform.Storage;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using XTransferTool.ViewModels;

namespace XTransferTool.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
    }

    private void NodeHeader_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Single-click on tag rows toggles expand/collapse.
        // This makes the interaction feel responsive and avoids the tiny expander hitbox.
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed != true)
            return;

        if (sender is not Avalonia.Controls.Control c)
            return;

        if (c.DataContext is not UserTreeNode n)
            return;

        if (n.Kind != NodeKind.Tag || n.Children.Count == 0)
            return;

        n.IsExpanded = !n.IsExpanded;
    }

    private void DropZone_DragOver(object? sender, DragEventArgs e)
    {
        if (DataContext is HomeViewModel vm && !vm.HasSelectedTarget())
        {
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (e.Data.Contains(DataFormats.Files))
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
            return;
        }

        e.DragEffects = DragDropEffects.None;
        e.Handled = true;
    }

    private void DropZone_Drop(object? sender, DragEventArgs e)
    {
        try
        {
            if (DataContext is not HomeViewModel vm)
                return;

            if (!vm.HasSelectedTarget())
            {
                var owner = TopLevel.GetTopLevel(this) as Window;
                _ = ShowInfoAsync(owner, "请先选择标签或用户", "请在左侧“其它用户”中先选中一个标签节点或用户节点，然后再拖拽/选择文件。");
                return;
            }

            if (!e.Data.Contains(DataFormats.Files))
                return;

            var files = e.Data.GetFiles();
            if (files is null)
                return;

            var paths = files
                .Select(f => f.TryGetLocalPath())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Cast<string>()
                .ToArray();

            vm.SetDropFiles(paths);
            _ = BeginDeliverAsync(vm, paths);
            e.Handled = true;
        }
        catch
        {
            // ignore
        }
    }

    private async void DropZone_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Click to pick files.
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed != true)
            return;

        if (DataContext is not HomeViewModel vm)
            return;

        if (!vm.HasSelectedTarget())
        {
            var owner = TopLevel.GetTopLevel(this) as Window;
            await ShowInfoAsync(owner, "请先选择标签或用户", "请在左侧“其它用户”中先选中一个标签节点或用户节点，然后再选择文件。");
            return;
        }

        var tl = TopLevel.GetTopLevel(this);
        var sp = tl?.StorageProvider;
        if (sp is null)
            return;

        try
        {
            var res = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择要投递的文件",
                AllowMultiple = true
            });

            var paths = (res ?? Array.Empty<IStorageFile>())
                .Select(f => f.TryGetLocalPath())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Cast<string>()
                .ToArray();

            vm.SetDropFiles(paths);
            _ = BeginDeliverAsync(vm, paths);
        }
        catch
        {
            // ignore
        }
    }

    private async Task BeginDeliverAsync(HomeViewModel vm, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return;

        var owner = TopLevel.GetTopLevel(this) as Window;
        var target = vm.CreateDeliveryTargetSnapshot();
        if (target is null)
        {
            await ShowInfoAsync(owner, "请先选择标签或用户", "请在左侧“其它用户”中先选中一个标签节点或用户节点，然后再投递。");
            return;
        }

        // Step 1: message input
        var msg = await ShowMessageInputAsync(owner, target.Display);
        if (msg is null)
            return; // cancelled

        // Step 2: confirm recipients
        if (target.Recipients.Count == 0)
        {
            await ShowInfoAsync(owner, "没有可投递的用户", "当前选中的标签下没有任何在线用户，或用户已离线。");
            return;
        }

        var confirmText = target.Kind == NodeKind.User
            ? $"确定向用户“{target.Recipients[0].Nickname}”发送 {paths.Count} 个文件吗？"
            : $"确定向标签“{target.TagDisplay ?? ""}”下的 {target.Recipients.Count} 个用户发送 {paths.Count} 个文件吗？";

        var ok = await ShowConfirmAsync(owner, "确认投递", confirmText);
        if (!ok)
            return;

        await vm.SendFilesToRecipientsAsync(target.Recipients, paths, msg);
        vm.SetDropFiles(Array.Empty<string>());
    }

    private static async Task<string?> ShowMessageInputAsync(Window? owner, string targetHint)
    {
        var win = new Window
        {
            Title = "输入留言（可选）",
            Width = 520,
            Height = 260,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var tb = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Height = 120
        };

        var ok = new Button { Content = "确定", Width = 90 };
        var cancel = new Button { Content = "取消", Width = 90 };

        ok.Click += (_, _) => win.Close(tb.Text ?? "");
        cancel.Click += (_, _) => win.Close(null);

        win.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = targetHint, FontSize = 12, Foreground = Avalonia.Media.Brushes.Gray },
                new TextBlock { Text = "留言（可不填）", FontWeight = Avalonia.Media.FontWeight.SemiBold },
                tb,
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, ok }
                }
            }
        };

        return await win.ShowDialog<string?>(owner);
    }

    private static async Task<bool> ShowConfirmAsync(Window? owner, string title, string message)
    {
        var win = new Window
        {
            Title = title,
            Width = 520,
            Height = 200,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var ok = new Button { Content = "确定发送", Width = 110 };
        var cancel = new Button { Content = "取消", Width = 90 };
        ok.Click += (_, _) => win.Close(true);
        cancel.Click += (_, _) => win.Close(false);

        win.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, ok }
                }
            }
        };

        return await win.ShowDialog<bool>(owner);
    }

    private static async Task ShowInfoAsync(Window? owner, string title, string message)
    {
        var win = new Window
        {
            Title = title,
            Width = 420,
            Height = 180,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var ok = new Button { Content = "确定", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Width = 90 };
        ok.Click += (_, _) => win.Close(true);

        win.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Children = { ok }
                }
            }
        };

        await win.ShowDialog(owner);
    }
}

