using Microsoft.Win32;

namespace AutoShutdown.Admin.Infrastructure;

public static class StartupManager
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AutoShutdownNovaSystemA";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, false);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, true)
            ?? throw new InvalidOperationException("Unable to open Windows startup registry key.");

        if (!enabled)
        {
            key.DeleteValue(ValueName, false);
            return;
        }

        var exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Unable to determine the System A executable path.");

        key.SetValue(ValueName, $"\"{exe}\"");
    }
}
