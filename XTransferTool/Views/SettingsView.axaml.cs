using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using XTransferTool.ViewModels;

namespace XTransferTool.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not SettingsViewModel vm)
            return;

        vm.PickFolderAsync = async () =>
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider is null)
                return null;

            var folders = await top.StorageProvider.OpenFolderPickerAsync(new()
            {
                Title = "选择默认接收目录",
                AllowMultiple = false
            });

            return folders.Count > 0 ? folders[0].Path.LocalPath : null;
        };
    }
}

