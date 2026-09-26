using Microsoft.Win32;

namespace DevTeam.Desktop.Services;

/// <summary>
/// Registry reader backed by the real Windows registry. The 64-bit view is requested explicitly so
/// the result does not depend on the bitness the runtime happens to be built for.
/// </summary>
public sealed class WindowsRegistryReader : IRegistryReader
{
    private const RegistryView View = RegistryView.Registry64;

    public string? ReadMachineString(string subKey, string valueName) => Read(Registry.LocalMachine, subKey, valueName);

    public string? ReadUserString(string subKey, string valueName) => Read(Registry.CurrentUser, subKey, valueName);

    private static string? Read(RegistryKey root, string subKey, string valueName)
    {
        try
        {
            using var key = root.OpenSubKey(subKey, writable: false);
            return key?.GetValue(valueName)?.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
