namespace DevTeam.Desktop.Services;

/// <summary>Reads string values from the Windows registry. Abstracted so detection is testable.</summary>
public interface IRegistryReader
{
    string? ReadMachineString(string subKey, string valueName);

    string? ReadUserString(string subKey, string valueName);
}

/// <summary>
/// Reads the WebView2 Evergreen runtime version from the registry. Used for reporting, not for
/// deciding whether the shell can show its UI in-app — that decision is made by trying the view.
/// </summary>
public interface IWebView2RuntimeProbe
{
    bool IsInstalled();

    string? GetVersion();
}

/// <summary>
/// Reports the WebView2 Evergreen runtime version from the registry. The installer asks the same
/// question, so the two agree on where the runtime records itself.
/// </summary>
public sealed class WebView2RuntimeProbe : IWebView2RuntimeProbe
{
    public const string ClientKey =
        @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    /// <summary>
    /// The Evergreen runtime is installed by the 32-bit EdgeUpdate agent, so on x64 it registers
    /// under the WOW6432Node reflector rather than the native 64-bit path.
    /// </summary>
    public const string Machine32BitClientKey =
        @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    public const string VersionValueName = "pv";

    private readonly IRegistryReader _registry;

    public WebView2RuntimeProbe(IRegistryReader registry) => _registry = registry;

    public string? GetVersion()
    {
        foreach (var key in new[] { ClientKey, Machine32BitClientKey })
        {
            var machine = _registry.ReadMachineString(key, VersionValueName);
            if (!string.IsNullOrWhiteSpace(machine))
                return machine;
        }

        foreach (var key in new[] { ClientKey, Machine32BitClientKey })
        {
            var user = _registry.ReadUserString(key, VersionValueName);
            if (!string.IsNullOrWhiteSpace(user))
                return user;
        }

        return null;
    }

    public bool IsInstalled() => GetVersion() is not null;
}
