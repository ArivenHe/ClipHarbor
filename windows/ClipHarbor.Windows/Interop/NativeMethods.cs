using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ClipHarbor.Windows.Interop;

internal static class NativeMethods
{
    internal delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    internal delegate void WinEventProc(nint hook, uint evt, nint hwnd, int objectId, int childId, uint threadId, uint time);
    [DllImport("comctl32.dll")] internal static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] internal static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] internal static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern nint GetClipboardOwner();
    [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(nint hwnd, StringBuilder name, int size);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] internal static extern nint LoadIcon(nint instance, nint name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] internal static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [DllImport("user32.dll")] internal static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] internal static extern bool PostMessage(nint hwnd, uint msg, nuint wParam, nint lParam);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] internal static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterWindowMessage(string message);
    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(in Guid folderId, uint flags, nint token, out nint path);

    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct MinMaxInfo { public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct NotifyIconData
    {
        public uint Size; public nint Window; public uint Id, Flags, CallbackMessage; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] internal struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }

    internal static bool IsOwnWindow(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var process);
        return process == Environment.ProcessId;
    }
    internal static string ScreenshotsDirectory()
    {
        var id = new Guid("b7bede81-df94-4682-a7d8-57a52620b86f");
        var result = SHGetKnownFolderPath(in id, 0x4000, 0, out var path); // KF_FLAG_DONT_VERIFY
        try { return result >= 0 && path != 0 ? Marshal.PtrToStringUni(path)! : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots"); }
        finally { if (path != 0) Marshal.FreeCoTaskMem(path); }
    }
    internal static string ProcessName(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var process);
        if (process == 0) return "";
        try { using var app = Process.GetProcessById((int)process); return app.ProcessName; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return ""; }
    }
    internal static bool IsPasteTarget(nint hwnd)
    {
        if (hwnd == 0 || !IsWindow(hwnd) || IsOwnWindow(hwnd)) return false;
        var name = new StringBuilder(256); GetClassName(hwnd, name, name.Capacity);
        return name.ToString() is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW");
    }
    internal static async Task PasteAsync(nint target)
    {
        if (!IsPasteTarget(target)) throw new InvalidOperationException("内容已复制，但没有可恢复的目标窗口。请先打开要粘贴的应用。");
        ShowWindow(target, 9); // SW_RESTORE
        SetForegroundWindow(target);
        // A hotkey may still be held. Never manufacture key-up events for it.
        for (var attempt = 0; attempt < 25; attempt++)
        {
            if (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.All(k => (GetAsyncKeyState(k) & 0x8000) == 0)) break;
            await Task.Delay(20);
        }
        await Task.Delay(100);
        if (GetForegroundWindow() != target || new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0))
            throw new InvalidOperationException("内容已复制。目标窗口未获得焦点或按键仍被按住，已取消自动粘贴。");
        static Input Key(ushort key, bool up) => new() { Type = 1, Data = new() { Keyboard = new() { Key = key, Flags = up ? 2u : 0u } } };
        var inputs = new[] { Key(0x11, false), Key(0x56, false), Key(0x56, true), Key(0x11, true) };
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new InvalidOperationException("内容已复制。Windows 阻止了自动粘贴；管理员权限窗口可能无法接收，请在目标应用手动粘贴。");
    }
}
