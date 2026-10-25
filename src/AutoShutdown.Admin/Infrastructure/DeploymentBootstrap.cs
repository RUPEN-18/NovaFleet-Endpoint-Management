using AutoShutdown.Shared;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;

namespace AutoShutdown.Admin.Infrastructure;

public sealed record DeploymentSecrets(string AdminToken, string CertificatePassword);

public sealed class DeploymentBootstrap : IDisposable
{
    public string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoShutdown.Nova", "DeploymentAdmin");
    public string Url => "https://127.0.0.1:5056/";
    public DeploymentSecrets Secrets { get; }
    public X509Certificate2 Certificate { get; }
    public string CertificateHash => Certificate.GetCertHashString(HashAlgorithmName.SHA256);
    public string? ConfiguredLanAddress { get; }
    public string? LanAddress { get; }
    public string? LanUrl => LanAddress is null ? null : DeploymentNetwork.LanUrl(LanAddress);

    public DeploymentBootstrap()
    {
        Directory.CreateDirectory(Root);
        using var identity = WindowsIdentity.GetCurrent();
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { identity.User!, new SecurityIdentifier("S-1-5-18"), new SecurityIdentifier("S-1-5-32-544") })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(Root).SetAccessControl(acl);
        var settings = Path.Combine(Root, "secrets.json");
        var certificateFile = Path.Combine(Root, "server.pfx");
        if (File.Exists(settings))
            Secrets = JsonSerializer.Deserialize<DeploymentSecrets>(File.ReadAllText(settings))
                ?? throw new InvalidDataException("Deployment server settings are invalid.");
        else
        {
            if (File.Exists(certificateFile)) throw new InvalidDataException("Deployment server certificate exists without its settings.");
            Secrets = new(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
            File.WriteAllText(settings, JsonSerializer.Serialize(Secrets));
        }
        if (!File.Exists(certificateFile))
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=Nova Deployment Local Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new System.Security.Cryptography.OidCollection
                { new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1") }, true));
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
            File.WriteAllBytes(certificateFile, cert.Export(X509ContentType.Pfx, Secrets.CertificatePassword));
        }
        Certificate = X509CertificateLoader.LoadPkcs12FromFile(
            certificateFile,
            Secrets.CertificatePassword,
            X509KeyStorageFlags.UserKeySet);
        var networkFile = Path.Combine(Root, "deployment-network.json");
        if (File.Exists(networkFile))
        {
            var network = JsonSerializer.Deserialize<DeploymentLanSettings>(File.ReadAllText(networkFile));
            if (!string.IsNullOrWhiteSpace(network?.Address))
            {
                ConfiguredLanAddress = DeploymentNetwork.ParseLanAddress(network.Address).ToString();
                // Changing Wi-Fi or unplugging Ethernet must not break the working local connection.
                if (IsAssignedLanAddress(ConfiguredLanAddress)) LanAddress = ConfiguredLanAddress;
            }
        }
    }

    public static bool IsAssignedLanAddress(string address)
    {
        var expected = DeploymentNetwork.ParseLanAddress(address);
        return NetworkInterface.GetAllNetworkInterfaces().Any(adapter =>
            adapter.OperationalStatus == OperationalStatus.Up && adapter.GetIPProperties().UnicastAddresses
                .Any(entry => entry.Address.Equals(expected)));
    }

    public async Task SaveLanSettingsAsync(string? address)
    {
        address = string.IsNullOrWhiteSpace(address) ? null : address.Trim();
        if (address is not null && !IsAssignedLanAddress(address))
            throw new InvalidDataException("That IPv4 address is not assigned to an active adapter on this Admin PC.");
        var path = Path.Combine(Root, "deployment-network.json");
        var temporary = path + ".tmp";
        foreach (var candidate in new[] { path, temporary })
            if (File.Exists(candidate) && (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The network settings file must not be a link.");
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new DeploymentLanSettings(address)));
        File.Move(temporary, path, overwrite: true);
    }

    public static async Task SaveExportedConnectionAsync(string path, DeviceConnection connection)
    {
        DeploymentNetwork.ValidateConnection(connection, connection.ComputerName);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The pairing file must not be a link.");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var identity = WindowsIdentity.GetCurrent();
                var acl = new FileSecurity();
                acl.SetAccessRuleProtection(true, false);
                foreach (var sid in new[] { identity.User!, new SecurityIdentifier("S-1-5-18"), new SecurityIdentifier("S-1-5-32-544") })
                    acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
                new FileInfo(temporary).SetAccessControl(acl);
                await JsonSerializer.SerializeAsync(output, connection, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void SaveLocalServiceConnection(DeviceConnection connection)
    {
        DeploymentNetwork.ValidateConnection(connection, Environment.MachineName);
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("Run Admin as administrator for the one-time pairing.");
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AutoShutdown.Nova.DeploymentService");
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Install the deployment service before pairing this PC.");
        var path = Path.Combine(root, "connection.json");
        var temporary = path + ".tmp";
        foreach (var candidate in new[] { path, temporary })
            if (File.Exists(candidate) && (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The service connection file must not be a link.");
        File.WriteAllText(temporary, JsonSerializer.Serialize(connection, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
        foreach (var sid in new[] { "S-1-5-18", "S-1-5-32-544" })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(temporary).SetAccessControl(acl);
        File.Move(temporary, path, overwrite: true);
    }

    public void Dispose() => Certificate.Dispose();
}
