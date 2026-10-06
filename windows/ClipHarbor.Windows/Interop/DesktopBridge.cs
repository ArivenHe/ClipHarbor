using ClipHarbor.Core;
using System.Runtime.InteropServices;

namespace ClipHarbor.Windows.Interop;

internal sealed class DesktopBridge : IDisposable
{
    private const uint TrayMessage = 0x8000 + 42;
    private readonly nint _hwnd;
    private readonly NativeMethods.SubclassProc _subclass;
    private readonly NativeMethods.WinEventProc _foreground;
    private readonly uint _taskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");
    private readonly nint _hook;
    private NativeMethods.NotifyIconData _tray;
    public nint PasteTarget { get; private set; }
    public event Action<string>? Command;
    public bool Paused { get; set; }
    public string SyncStatus { get; private set; } = "";
    public void SetSyncStatus(string value)
    {
        SyncStatus = value;
        var tip = "拾贴 · " + value;
        _tray.Tip = tip.Length > 120 ? tip[..120] : tip;
        NativeMethods.Shell_NotifyIcon(1, ref _tray);
    }

    public DesktopBridge(nint hwnd)
    {
        _hwnd = hwnd;
        _subclass = WindowMessage;
        _foreground = (_, _, window, _, _, _, _) => RememberTarget(window);
        if (!NativeMethods.SetWindowSubclass(hwnd, _subclass, 1, 0)) throw new InvalidOperationException("无法初始化托盘窗口。");
        _hook = NativeMethods.SetWinEventHook(3, 3, 0, _foreground, 0, 0, 0); // EVENT_SYSTEM_FOREGROUND, out-of-context
        _tray = new()
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.NotifyIconData>(), Window = hwnd, Id = 1,
            Flags = 7 | 0x80, CallbackMessage = TrayMessage, Icon = NativeMethods.LoadIcon(0, (nint)32512),
            Tip = "拾贴 · ClipHarbor", Info = "", InfoTitle = ""
        };
        AddTray();
        RememberTarget(NativeMethods.GetForegroundWindow());
    }
    private void AddTray()
    {
        if (!NativeMethods.Shell_NotifyIcon(0, ref _tray)) throw new InvalidOperationException("无法创建系统托盘图标。");
        _tray.Version = 4;
        NativeMethods.Shell_NotifyIcon(4, ref _tray); // NIM_SETVERSION: enable keyboard notifications
    }
    public void RememberTarget(nint hwnd) { if (NativeMethods.IsPasteTarget(hwnd)) PasteTarget = hwnd; }
    public bool SetHotkey(AppSettings settings)
    {
        NativeMethods.UnregisterHotKey(_hwnd, 1);
        return NativeMethods.RegisterHotKey(_hwnd, 1, settings.HotkeyModifiers | 0x4000, settings.HotkeyKey);
    }
    private nint WindowMessage(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == 0x0024)
        {
            var limits = Marshal.PtrToStructure<NativeMethods.MinMaxInfo>(lParam);
            var scale = Math.Max(96, NativeMethods.GetDpiForWindow(hwnd)) / 96d;
            limits.MinTrackSize = new() { X = (int)(900 * scale), Y = (int)(600 * scale) };
            Marshal.StructureToPtr(limits, lParam, false);
            return 0;
        }
        if (message == 0x0312 && wParam == 1) { Command?.Invoke("quick"); return 0; }
        if (message == _taskbarCreated) { AddTray(); return 0; }
        if (message == TrayMessage)
        {
            var notification = (uint)lParam & 0xFFFF;
            if (notification is 0x0203 or 0x0400 or 0x0401) Command?.Invoke("history"); // double click, NIN_SELECT, NIN_KEYSELECT
            if (notification is 0x0205 or 0x007B) ShowMenu();
            return 0;
        }
        return NativeMethods.DefSubclassProc(hwnd, message, wParam, lParam);
    }
    private void ShowMenu()
    {
        var menu = NativeMethods.CreatePopupMenu();
        try
        {
            if (SyncStatus != "") NativeMethods.AppendMenu(menu, 2, 0, SyncStatus.Length > 70 ? SyncStatus[..70] : SyncStatus);
            NativeMethods.AppendMenu(menu, 0, 1, "打开历史");
            NativeMethods.AppendMenu(menu, 0, 2, Paused ? "恢复记录" : "暂停记录");
            NativeMethods.AppendMenu(menu, 0, 3, "设置");
            NativeMethods.AppendMenu(menu, 0x800, 0, "");
            NativeMethods.AppendMenu(menu, 0, 4, "退出拾贴");
            NativeMethods.GetCursorPos(out var point);
            NativeMethods.SetForegroundWindow(_hwnd);
            var command = NativeMethods.TrackPopupMenu(menu, 0x0100 | 0x0002, point.X, point.Y, 0, _hwnd, 0);
            NativeMethods.PostMessage(_hwnd, 0, 0, 0);
            if (command != 0) Command?.Invoke(command switch { 1 => "history", 2 => "pause", 3 => "settings", _ => "quit" });
        }
        finally { NativeMethods.DestroyMenu(menu); }
    }
    public void Dispose()
    {
        NativeMethods.UnregisterHotKey(_hwnd, 1);
        NativeMethods.Shell_NotifyIcon(2, ref _tray);
        if (_hook != 0) NativeMethods.UnhookWinEvent(_hook);
        NativeMethods.RemoveWindowSubclass(_hwnd, _subclass, 1);
    }
}
