using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
namespace AutoShutdown.Shared;

[Flags]
public enum AdminPermission { None=0, Install=1, Control=2, Automation=4, ReadLogs=8, Manage=16, All=31 }
public sealed record AdminIdentity(string Id, string Username, string DisplayName, string Role,
    AdminPermission Permissions, List<string> DeviceIds, bool Enabled=true)
{
    public bool IsSuper => Role == "SuperAdmin" && Enabled;
    public bool Can(AdminPermission permission) => Enabled && (IsSuper || (Permissions & permission) == permission);
    public bool CanDevice(string id) => Enabled && (IsSuper || DeviceIds.Contains(id, StringComparer.Ordinal));
    public string RoleLabel => Role == "SuperAdmin" ? "Super Admin" : "Admin";
    public string Label => $"{DisplayName} ({Username})";
}
public sealed record LoginRequest(string Username, string Password);
public sealed record SetupRequest(string Username, string DisplayName, string Password);
public sealed record LoginSession(string Token, AdminIdentity Identity, DateTimeOffset ExpiresUtc, string RecoveryCode="");
public sealed record AccountChange(string Id, string Username, string DisplayName, string Role,
    AdminPermission Permissions, List<string> DeviceIds, bool Enabled, string Password="");
public sealed record RecoveryRequest(string Username, string RecoveryCode, string NewPassword);
public sealed record AuditEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    public string AdminId { get; init; } = "";
    public string Username { get; init; } = "SYSTEM";
    public string DisplayName { get; init; } = "System";
    public string DeviceId { get; init; } = "";
    public string ComputerName { get; init; } = "";
    public string Action { get; init; } = "";
    public string Software { get; init; } = "";
    public string Version { get; init; } = "";
    public string Previous { get; init; } = "";
    public string Current { get; init; } = "";
    public string Result { get; init; } = "";
    public string Details { get; init; } = "";
}
public static class PasswordHash
{
    public const int Iterations=600_000;
    public static void Validate(string password)
    {
        if (password is null || password.Length < 12 || password.Length > 128)
            throw new ArgumentException("Use a password of 12–128 characters.");
    }
    public static string Create(string password)
    {
        Validate(password);
        var salt=RandomNumberGenerator.GetBytes(16);
        var hash=Rfc2898DeriveBytes.Pbkdf2(password,salt,Iterations,HashAlgorithmName.SHA256,32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }
    public static bool Verify(string password, string encoded)
    {
        if (password is null || password.Length > 128) return false;
        try
        {
            var parts=encoded.Split('$');
            if(parts.Length!=4 || parts[0]!="pbkdf2-sha256" || !int.TryParse(parts[1],out var cost) || cost<Iterations || cost>2_000_000) return false;
            var salt=Convert.FromBase64String(parts[2]); var expected=Convert.FromBase64String(parts[3]);
            if(salt.Length!=16 || expected.Length!=32) return false;
            var actual=Rfc2898DeriveBytes.Pbkdf2(password,salt,cost,HashAlgorithmName.SHA256,32);
            return CryptographicOperations.FixedTimeEquals(actual,expected);
        }
        catch(FormatException) {return false;}
    }
    public static void ValidateUsername(string name)
    {
        if (name is null || !Regex.IsMatch(name,@"\A[a-zA-Z0-9][a-zA-Z0-9._-]{2,63}\z"))
            throw new ArgumentException("Username must contain 3–64 letters, digits, dots, underscores or hyphens.");
    }
    public static string Digest(string token)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
