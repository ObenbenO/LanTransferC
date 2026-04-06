using System;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;
using XTransferTool.ViewModels;

namespace XTransferTool.Views;

public partial class RemoteDesktopView : UserControl
{
    private bool _leftDown;
    private bool _rightDown;
    private bool _hasLastPos;
    private int _lastX;
    private int _lastY;

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
        if (!TryMapToRemotePixels(c.Bounds, p, vm.RemoteWidth, vm.RemoteHeight, clampOutside: false, out var rx, out var ry))
            return;

        _hasLastPos = true;
        _lastX = rx;
        _lastY = ry;
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
        if (!TryMapToRemotePixels(c.Bounds, p, vm.RemoteWidth, vm.RemoteHeight, clampOutside: true, out var rx, out var ry))
            return;

        _hasLastPos = true;
        _lastX = rx;
        _lastY = ry;

        var props = e.GetCurrentPoint(c).Properties;
        if (props.IsLeftButtonPressed)
        {
            _leftDown = true;
            vm.SendMouseDown(rx, ry, button: 0);
            e.Pointer.Capture(c);
        }

        if (props.IsRightButtonPressed)
        {
            _rightDown = true;
            vm.SendMouseDown(rx, ry, button: 1);
            e.Pointer.Capture(c);
        }
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
        if (!TryMapToRemotePixels(c.Bounds, p, vm.RemoteWidth, vm.RemoteHeight, clampOutside: true, out var rx, out var ry))
        {
            if (_hasLastPos)
            {
                rx = _lastX;
                ry = _lastY;
            }
            else
            {
                return;
            }
        }

        _hasLastPos = true;
        _lastX = rx;
        _lastY = ry;

        if (e.InitialPressMouseButton == MouseButton.Left || _leftDown)
        {
            _leftDown = false;
            vm.SendMouseUp(rx, ry, button: 0);
        }

        if (e.InitialPressMouseButton == MouseButton.Right || _rightDown)
        {
            _rightDown = false;
            vm.SendMouseUp(rx, ry, button: 1);
        }

        if (!_leftDown && !_rightDown)
            e.Pointer.Capture(null);
    }

    private void RemoteImage_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (DataContext is not RemoteDesktopViewModel vm)
            return;
        if (!_hasLastPos)
            return;

        if (_leftDown)
        {
            _leftDown = false;
            vm.SendMouseUp(_lastX, _lastY, button: 0);
        }

        if (_rightDown)
        {
            _rightDown = false;
            vm.SendMouseUp(_lastX, _lastY, button: 1);
        }
    }

    private void RemoteImage_PointerExited(object? sender, PointerEventArgs e)
    {
        if (DataContext is not RemoteDesktopViewModel vm)
            return;
        if (!_hasLastPos)
            return;

        if (_leftDown)
        {
            _leftDown = false;
            vm.SendMouseUp(_lastX, _lastY, button: 0);
        }

        if (_rightDown)
        {
            _rightDown = false;
            vm.SendMouseUp(_lastX, _lastY, button: 1);
        }
    }

    private static bool TryMapToRemotePixels(Rect bounds, Point localPoint, int remoteW, int remoteH, bool clampOutside, out int x, out int y)
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
        if (!clampOutside)
        {
            if (cx < 0 || cy < 0 || cx >= contentW || cy >= contentH)
                return false;
        }
        else
        {
            cx = Math.Clamp(cx, 0, Math.Max(0, contentW - 1));
            cy = Math.Clamp(cy, 0, Math.Max(0, contentH - 1));
        }

        x = (int)Math.Clamp(cx / scale, 0, remoteW - 1);
        y = (int)Math.Clamp(cy / scale, 0, remoteH - 1);
        return true;
    }
}
