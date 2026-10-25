using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace AutoShutdown.Shared;

public sealed record DeploymentLanSettings(string? Address);
public sealed record DeploymentLanInfo(string? ConfiguredAddress, string? ActiveUrl, string CertificateSha256);

public static class DeploymentNetwork
{
    public const int AdminPort = 5056;
    public const int DevicePort = 5057;

    public static bool IsPrivateLanAddress(IPAddress? address)
    {
        if (address?.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31
            || bytes[0] == 192 && bytes[1] == 168;
    }

    public static IPAddress ParseLanAddress(string text)
    {
        if (!IPAddress.TryParse(text, out var address) || !IsPrivateLanAddress(address)
            || !string.Equals(address.ToString(), text, StringComparison.Ordinal))
            throw new InvalidDataException("Enter this Admin PC's private IPv4 address, for example 192.168.1.20.");
        return address;
    }

    public static string LanUrl(string address) => $"https://{ParseLanAddress(address)}:{DevicePort}/";

    public static bool CanUseAdminEndpoint(IPAddress? local, int port, IPAddress? remote, int adminPort = AdminPort) =>
        local is not null && remote is not null && port == adminPort
        && IPAddress.IsLoopback(local) && IPAddress.IsLoopback(remote);

    public static void ValidateComputerName(string? computer)
    {
        if (computer is null || !Regex.IsMatch(computer, @"\A[A-Za-z0-9][A-Za-z0-9_-]{0,62}\z"))
            throw new InvalidDataException("Enter the exact client computer name returned by hostname on that PC.");
    }

    public static void ValidateConnection(DeviceConnection profile, string expectedComputer)
    {
        ValidateComputerName(profile.ComputerName);
        if (!string.Equals(profile.ComputerName, expectedComputer, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("This pairing file belongs to a different computer.");
        if (!Guid.TryParseExact(profile.DeviceId, "N", out _)
            || !SevenZipPolicy.IsHash(profile.DeviceToken) || !SevenZipPolicy.IsHash(profile.ServerCertificateSha256))
            throw new InvalidDataException("The pairing file contains invalid device credentials or certificate pin.");
        if (!Uri.TryCreate(profile.ServerUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0
            || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || !IPAddress.TryParse(uri.Host, out var address)
            || !(address.Equals(IPAddress.Loopback) && uri.Port == AdminPort
                || IsPrivateLanAddress(address) && uri.Port == DevicePort))
            throw new InvalidDataException("Use the pinned local HTTPS endpoint or a private IPv4 LAN endpoint on port 5057.");
    }
}
