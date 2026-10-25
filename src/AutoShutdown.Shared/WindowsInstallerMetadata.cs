using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AutoShutdown.Shared;

// Reads MSI databases without running installation code. Also used by the service for
// per-machine MSI product-state checks and authenticated Brave preset recognition.
public static class WindowsInstallerMetadata
{
    public static MsiProductMetadata ReadPackage(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Installer inspection requires Windows.");
        Check(MsiOpenDatabase(path, IntPtr.Zero, out var database), "open MSI metadata"); // MSIDBOPEN_READONLY
        try
        {
            return new(ReadProperty(database, "ProductName"), ReadProperty(database, "ProductVersion"),
                ReadProperty(database, "ProductCode"), ReadProperty(database, "Manufacturer"));
        }
        finally { MsiCloseHandle(database); }
    }
    private static string ReadProperty(uint database, string property)
    {
        Check(MsiDatabaseOpenView(database, $"SELECT `Value` FROM `Property` WHERE `Property`='{property}'", out var view), "read MSI properties");
        try
        {
            Check(MsiViewExecute(view, 0), "query MSI properties");
            var result = MsiViewFetch(view, out var record);
            if (result == 259) return ""; // ERROR_NO_MORE_ITEMS
            Check(result, "read MSI property");
            try
            {
                var text = new StringBuilder(4097); uint length = 4096;
                Check(MsiRecordGetString(record, 1, text, ref length), "read MSI property value");
                return text.ToString();
            }
            finally { MsiCloseHandle(record); }
        }
        finally { MsiViewClose(view); MsiCloseHandle(view); }
    }
    public static string MachineProductProperty(string productCode, string property)
    {
        if (!OperatingSystem.IsWindows() || !Guid.TryParseExact(productCode, "B", out _)) return "";
        var text = new StringBuilder(4097); uint length = 4096;
        // MSIINSTALLCONTEXT_MACHINE = 4; never consult the LocalSystem user's install context.
        return MsiGetProductInfoEx(productCode, null, 4, property, text, ref length) == 0 ? text.ToString() : "";
    }
    public static bool IsSignedBy(string path, string publisher)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var file = new WinTrustFile { Size = (uint)Marshal.SizeOf<WinTrustFile>(), Path = Marshal.StringToCoTaskMemUni(path) };
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFile>());
        Marshal.StructureToPtr(file, filePointer, false);
        var data = new WinTrustData { Size = (uint)Marshal.SizeOf<WinTrustData>(), UiChoice = 2, UnionChoice = 1,
            File = filePointer, StateAction = 1, ProviderFlags = 0x1000 }; // WTD_CACHE_ONLY_URL_RETRIEVAL; no UI
        var policy = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        try
        {
            if (WinVerifyTrust(new IntPtr(-1), ref policy, ref data) != 0) return false;
#pragma warning disable SYSLIB0057
            using var signed = X509Certificate.CreateFromSignedFile(path);
            using var certificate = new X509Certificate2(signed);
#pragma warning restore SYSLIB0057
            return ApplicationPackagePolicy.SamePublisher(certificate.GetNameInfo(X509NameType.SimpleName, false), publisher);
        }
        catch (System.Security.Cryptography.CryptographicException) { return false; }
        finally
        {
            data.StateAction = 2;
            WinVerifyTrust(new IntPtr(-1), ref policy, ref data);
            Marshal.FreeHGlobal(filePointer);
            Marshal.FreeCoTaskMem(file.Path);
        }
    }
    private static void Check(uint result, string action)
    {
        if (result != 0) throw new InvalidDataException($"Could not {action} (Windows Installer error {result}). Select a complete MSI installer.");
    }
    [StructLayout(LayoutKind.Sequential)] private struct WinTrustFile
    {
        public uint Size; public IntPtr Path, File, KnownSubject;
    }
    [StructLayout(LayoutKind.Sequential)] private struct WinTrustData
    {
        public uint Size; public IntPtr Policy, Sip;
        public uint UiChoice, Revocation, UnionChoice; public IntPtr File;
        public uint StateAction; public IntPtr State, Url;
        public uint ProviderFlags, UiContext; public IntPtr SignatureSettings;
    }
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);
    [DllImport("msi.dll", EntryPoint = "MsiOpenDatabaseW", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint MsiOpenDatabase(string path, IntPtr mode, out uint database);
    [DllImport("msi.dll", EntryPoint = "MsiDatabaseOpenViewW", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint MsiDatabaseOpenView(uint database, string query, out uint view);
    [DllImport("msi.dll", ExactSpelling = true)] private static extern uint MsiViewExecute(uint view, uint record);
    [DllImport("msi.dll", ExactSpelling = true)] private static extern uint MsiViewFetch(uint view, out uint record);
    [DllImport("msi.dll", EntryPoint = "MsiRecordGetStringW", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint MsiRecordGetString(uint record, uint field, StringBuilder text, ref uint length);
    [DllImport("msi.dll", ExactSpelling = true)] private static extern uint MsiViewClose(uint view);
    [DllImport("msi.dll", ExactSpelling = true)] private static extern uint MsiCloseHandle(uint handle);
    [DllImport("msi.dll", EntryPoint = "MsiGetProductInfoExW", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint MsiGetProductInfoEx(string code, string? sid, uint context, string property, StringBuilder text, ref uint length);
}
