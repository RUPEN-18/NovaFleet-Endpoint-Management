using System.Text.RegularExpressions;

namespace AutoShutdown.Shared;

public sealed record ApplicationImportOptions(string Name = "", string SilentArguments = "", string InstalledDisplayName = "",
    string InstalledDisplayVersion = "", string ExpectedSha256 = "", string VerificationMode = "exact",
    string InstalledProductCode = "", string InstalledPublisher = "");

public static class ApplicationPackagePolicy
{
    public const long MaximumBytes = 2L * 1024 * 1024 * 1024;
    public const string Exact = "exact", Registry = "registry", MsiProduct = "msi-product", BraveMachine = "brave-machine", Auto = "auto";
    public static bool IsGeneral(ApprovedPackage package) => package.Profile == "application";
    public static bool NeedsUpdatedService(ApprovedPackage package) => IsGeneral(package) && package.VerificationMode != Exact;
    public static bool IsApproved(ApprovedPackage package)
    {
        if (!IsGeneral(package)) return package.Profile == "7zip" && SevenZipPolicy.IsApproved(package);
        if (!Regex.IsMatch(package.Id, @"\Aapp-[0-9a-f]{32}\z") || !SevenZipPolicy.IsHash(package.Sha256)
            || package.Length <= 0 || package.Length > MaximumBytes || package.VerificationMode == Auto) return false;
        try { Validate(package.FileName, Options(package)); return true; }
        catch (ArgumentException) { return false; }
    }
    public static ApplicationImportOptions Options(ApprovedPackage p) => new(p.Name, p.SilentArguments,
        p.InstalledDisplayName, p.InstalledDisplayVersion, "", p.VerificationMode, p.InstalledProductCode, p.InstalledPublisher);
    public static void ValidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 180
            || !Regex.IsMatch(fileName, @"\A[A-Za-z0-9][A-Za-z0-9._ ()-]*\.(exe|msi)\z", RegexOptions.IgnoreCase))
            throw new ArgumentException("Select an EXE or MSI installer with a simple file name.");
    }
    public static void Validate(string fileName, ApplicationImportOptions options)
    {
        ValidateFileName(fileName);
        foreach (var value in new[] { options.Name, options.InstalledDisplayName, options.InstalledDisplayVersion, options.InstalledPublisher })
            if (value is null || value.Length > 200 || value.Any(char.IsControl)) throw new ArgumentException("Application details are invalid.");
        if (options.SilentArguments is null || options.SilentArguments.Length > 4000 || options.SilentArguments.Any(char.IsControl))
            throw new ArgumentException("Installer arguments are invalid.");
        if (options.ExpectedSha256 is null || options.ExpectedSha256.Length != 0 && !SevenZipPolicy.IsHash(options.ExpectedSha256))
            throw new ArgumentException("Expected SHA-256 must be blank or contain exactly 64 hexadecimal characters.");
        if (options.VerificationMode == Auto) return; // Server resolves this from the uploaded bytes before approval.
        if (options.VerificationMode is not (Exact or Registry or MsiProduct or BraveMachine))
            throw new ArgumentException("Unknown installation verification method.");
        if (string.IsNullOrWhiteSpace(options.Name) || string.IsNullOrWhiteSpace(options.InstalledDisplayName))
            throw new ArgumentException("Nova could not identify the application. Set its name under Advanced options.");
        if ((options.VerificationMode is Exact or MsiProduct) && string.IsNullOrWhiteSpace(options.InstalledDisplayVersion))
            throw new ArgumentException("This installation profile requires its expected installed version.");
        var msi = fileName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
        if (!msi && string.IsNullOrWhiteSpace(options.SilentArguments))
            throw new ArgumentException("Nova has no saved unattended settings for this EXE. Enter the vendor's silent arguments under Advanced options, or select an MSI installer.");
        if (msi && !Regex.IsMatch(options.SilentArguments, @"\A\s*(?:[A-Z][A-Z0-9_]*=(?:""[^""\r\n]*""|[^\s""/]+)\s*)*\z"))
            throw new ArgumentException("MSI options must be properties, for example ALLUSERS=1. Quiet mode and no restart are supplied automatically.");
        if (msi && Regex.IsMatch(options.SilentArguments, @"\b(?:REBOOT|REBOOTPROMPT)\s*=", RegexOptions.IgnoreCase))
            throw new ArgumentException("MSI restart properties are controlled by Nova.");
        if (options.VerificationMode == MsiProduct && (!msi || !Guid.TryParseExact(options.InstalledProductCode, "B", out _)))
            throw new ArgumentException("An MSI product identity is required.");
        if (options.VerificationMode == BraveMachine && (msi
            || options.InstalledDisplayName != "Brave" || options.InstalledPublisher != InstallerCatalog.BravePublisher
            || options.SilentArguments != "/silent /install"))
            throw new ArgumentException("Invalid automatic Brave installation settings.");
    }
    public static bool SuccessfulExit(ApprovedPackage package, int? exitCode) =>
        exitCode == 0 || IsGeneral(package) && exitCode == 3010;
    public static string ExpectedVersion(ApprovedPackage package) => IsGeneral(package)
        ? package.InstalledDisplayVersion : SevenZipPolicy.Version;
    public static bool IsVersion(string? version) => version is not null && version.Length <= 200
        && !version.Any(char.IsControl) && !string.IsNullOrWhiteSpace(version);
    public static bool VerifiedResult(ApprovedPackage p, string version, InstallationEvidence? evidence)
    {
        if (!NeedsUpdatedService(p)) return version == ExpectedVersion(p);
        if (!IsVersion(version) || evidence is null || evidence.Mode != p.VerificationMode) return false;
        if (p.VerificationMode == MsiProduct)
            return string.Equals(evidence.ProductCode, p.InstalledProductCode, StringComparison.OrdinalIgnoreCase)
                && version == p.InstalledDisplayVersion;
        if (!string.Equals(evidence.DisplayName, p.InstalledDisplayName, StringComparison.OrdinalIgnoreCase)) return false;
        if (p.InstalledPublisher.Length != 0 && !SamePublisher(evidence.Publisher, p.InstalledPublisher)) return false;
        return p.VerificationMode != BraveMachine || evidence.ExecutableVerified && Version.TryParse(version, out _)
            && (p.InstalledDisplayVersion.Length == 0 || InstallerCatalog.BraveVersionsMatch(version, p.InstalledDisplayVersion));
    }
    public static bool SamePublisher(string? a, string? b) => a is not null && b is not null
        && string.Equals(a.Replace(",", "").TrimEnd('.', ' '), b.Replace(",", "").TrimEnd('.', ' '), StringComparison.OrdinalIgnoreCase);
}
