using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AutoShutdown.Shared;

public sealed record InstallerInspection(ApplicationImportOptions Options, bool Automatic, string Summary);
public sealed record MsiProductMetadata(string Name, string Version, string ProductCode, string Publisher);

// Built-in options are chosen by inspected bytes/signature, never by an extension alone.
public static class InstallerCatalog
{
    public const string BravePublisher = "Brave Software Inc";
    public static bool IsBraveFileName(string name) => Regex.IsMatch(name,
        @"\ABraveBrowser(?:Standalone)?Setup(?:-BRV[0-9]+)?(?: \([0-9]+\))?\.exe\z", RegexOptions.IgnoreCase);
    public static bool BraveVersionsMatch(string registryVersion, string? executableVersion)
    {
        if (!Version.TryParse(registryVersion, out var registry) || !Version.TryParse(executableVersion, out var executable)) return false;
        if (registry == executable) return true;
        // Brave may expose its three-part release version in the file and prepend Chromium's
        // major version in Windows installed-app metadata. Compare the complete Brave release.
        return registry.Revision >= 0 && executable.Revision < 0 && registry.Minor == executable.Major
            && registry.Build == executable.Minor && registry.Revision == executable.Build
            || executable.Revision >= 0 && registry.Revision < 0 && executable.Minor == registry.Major
            && executable.Build == registry.Minor && executable.Revision == registry.Build;
    }
    public static InstallerInspection Inspect(string path, string? originalFileName = null)
    {
        var fileName = originalFileName ?? Path.GetFileName(path);
        ApplicationPackagePolicy.ValidateFileName(fileName);
        var file = new FileInfo(path);
        if (!file.Exists || file.Length <= 0 || file.Length > ApplicationPackagePolicy.MaximumBytes)
            throw new InvalidDataException("Select a non-empty installer no larger than 2 GiB.");
        if (fileName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            return FromMsi(WindowsInstallerMetadata.ReadPackage(path));
        var metadata = FileVersionInfo.GetVersionInfo(path);
        if (IsBraveFileName(fileName) && WindowsInstallerMetadata.IsSignedBy(path, BravePublisher))
            return Brave();
        // Installer ProductVersion often describes the bootstrapper, not the installed application.
        // Never turn that value into a required browser/application version.
        var name = CleanName(metadata.ProductName);
        if (name.Length == 0) name = Path.GetFileNameWithoutExtension(fileName);
        var options = new ApplicationImportOptions(name, "", name, "", VerificationMode: ApplicationPackagePolicy.Registry);
        return new(options, false, "No saved unattended settings for this EXE. Add its vendor's silent arguments in Advanced options; name and version overrides are optional.");
    }
    public static InstallerInspection Brave() => new(new("Brave Browser", "/silent /install", "Brave", "",
        VerificationMode: ApplicationPackagePolicy.BraveMachine, InstalledPublisher: BravePublisher), true,
        "Brave detected. Silent installation is ready; Nova will verify the installed browser on the Client. Online setup files need internet on the Client.");
    public static InstallerInspection FromMsi(MsiProductMetadata msi)
    {
        var options = new ApplicationImportOptions(msi.Name, "ALLUSERS=1", msi.Name, msi.Version,
            VerificationMode: ApplicationPackagePolicy.MsiProduct, InstalledProductCode: msi.ProductCode,
            InstalledPublisher: msi.Publisher);
        ApplicationPackagePolicy.Validate("package.msi", options);
        return new(options, true, $"MSI detected: {msi.Name} {msi.Version}. Quiet machine installation and product verification are ready.");
    }
    public static ApplicationImportOptions Resolve(InstallerInspection inspected, ApplicationImportOptions requested)
    {
        if (requested.VerificationMode != ApplicationPackagePolicy.Auto) return requested;
        if (inspected.Automatic)
        {
            // Recognised EXEs keep the authenticated preset. MSI supports optional extra properties only.
            var arguments = inspected.Options.VerificationMode == ApplicationPackagePolicy.MsiProduct
                && requested.SilentArguments.Length != 0 ? requested.SilentArguments : inspected.Options.SilentArguments;
            return inspected.Options with { SilentArguments = arguments, ExpectedSha256 = requested.ExpectedSha256,
                Name = requested.Name.Length == 0 ? inspected.Options.Name : requested.Name,
                InstalledDisplayVersion = inspected.Options.VerificationMode == ApplicationPackagePolicy.BraveMachine
                    ? requested.InstalledDisplayVersion : inspected.Options.InstalledDisplayVersion };
        }
        var name = requested.Name.Length == 0 ? inspected.Options.Name : requested.Name;
        var display = requested.InstalledDisplayName.Length == 0 ? inspected.Options.InstalledDisplayName : requested.InstalledDisplayName;
        return requested with { Name = name, InstalledDisplayName = display,
            VerificationMode = requested.InstalledDisplayVersion.Length == 0 ? ApplicationPackagePolicy.Registry : ApplicationPackagePolicy.Exact,
            InstalledProductCode = "", InstalledPublisher = "" };
    }
    private static string CleanName(string? name) => name is not null && name.Trim().Length <= 200
        && !name.Any(char.IsControl) ? name.Trim() : "";
}
