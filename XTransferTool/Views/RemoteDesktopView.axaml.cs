using System;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;
using XTransferTool.ViewModels;

namespace XTransferTool.Views;

public partial class RemoteDesktopView : UserControl
{
    public RemoteDesktopView()
    {
        InitializeComponent();
    }

    private void RemoteImage_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (DataContext is not RemoteDesktopViewModel vm)
            return;
        if (sender is not Avalonia.Controls.Control c)
            return;

        if (vm.RemoteWidth <= 0 || vm.RemoteHeight <= 0)
            return;

        var p = e.GetPosition(c);
        if (!TryMapToRemotePixels(c.Bounds, p, vm.RemoteWidth, vm.RemoteHeight, out var rx, out var ry))
            return;

        vm.SendMouseMove(rx, ry);
    }

    private void RemoteImage_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not RemoteDesktopViewModel vm)
            return;
        if (sender is not Avalonia.Controls.Control c)
            return;

        if (vm.RemoteWidth <= 0 || vm.RemoteHeight <= 0)
            return;

        var p = e.GetPosition(c);
        if (!TryMapToRemotePixels(c.Bounds, p, vm.RemoteWidth, vm.RemoteHeight, out var rx, out var ry))
            return;

        if (e.GetCurrentPoint(c).Properties.IsLeftButtonPressed)
            vm.SendMouseDown(rx, ry, button: 0);
    }

    private void RemoteImage_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is not RemoteDesktopViewModel vm)
            return;
        if (sender is not Avalonia.Controls.Control c)
            return;

        if (vm.RemoteWidth <= 0 || vm.RemoteHeight <= 0)
            return;

        var p = e.GetPosition(c);
        if (!TryMapToRemotePixels(c.Bounds, p, vm.RemoteWidth, vm.RemoteHeight, out var rx, out var ry))
            return;

        if (e.InitialPressMouseButton == MouseButton.Left)
            vm.SendMouseUp(rx, ry, button: 0);
    }

    private static bool TryMapToRemotePixels(Rect bounds, Point localPoint, int remoteW, int remoteH, out int x, out int y)
    {
        x = 0;
        y = 0;

        if (bounds.Width <= 1 || bounds.Height <= 1 || remoteW <= 0 || remoteH <= 0)
            return false;

        var scale = Math.Min(bounds.Width / remoteW, bounds.Height / remoteH);
        if (scale <= 0)
            return false;

        var contentW = remoteW * scale;
        var contentH = remoteH * scale;
        var offsetX = (bounds.Width - contentW) / 2;
        var offsetY = (bounds.Height - contentH) / 2;

        var cx = localPoint.X - offsetX;
        var cy = localPoint.Y - offsetY;
        if (cx < 0 || cy < 0 || cx >= contentW || cy >= contentH)
            return false;

        x = (int)Math.Clamp(cx / scale, 0, remoteW - 1);
        y = (int)Math.Clamp(cy / scale, 0, remoteH - 1);
        return true;
    }
}
