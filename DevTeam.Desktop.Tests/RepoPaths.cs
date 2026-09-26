namespace DevTeam.Desktop.Tests;

/// <summary>Locates repo files (installer script, build script) from the test output directory.</summary>
internal static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string Packaging => Path.Combine(Root, "packaging");

    public static string InstallerScript => Path.Combine(Packaging, "devteam.iss");

    public static string BuildInstallerScript => Path.Combine(Packaging, "build-installer.ps1");

    public static string E2EScript => Path.Combine(Packaging, "e2e-installed-app.ps1");

    public static string ReadInstallerScript() => File.ReadAllText(InstallerScript);

    public static string ReadBuildInstallerScript() => File.ReadAllText(BuildInstallerScript);

    public static string ReadE2EScript() => File.ReadAllText(E2EScript);

    public static string ReadShellFile(params string[] parts) =>
        File.ReadAllText(Path.Combine([Root, "DevTeam.Desktop", .. parts]));

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DevTeam.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root (DevTeam.slnx).");
    }
}
