using Microsoft.Win32;

namespace ClipHarbor.Windows.Services;

internal static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool Enabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue("ClipHarbor") is string; }
    }
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue("ClipHarbor", $"\"{Environment.ProcessPath}\" --background");
        else key.DeleteValue("ClipHarbor", false);
    }
}
