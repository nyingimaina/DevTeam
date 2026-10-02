using System.Runtime.CompilerServices;

namespace DevTeam.Tests;

/// <summary>
/// Turns off the broker's single-instance license for every test in this assembly, before
/// any test can run. WebApplicationFactory-hosted tests execute Program.Main as well (the
/// factory intercepts app.Run, not the code above it) and xUnit runs them in parallel —
/// against the one real machine-global mutex they would all contend, with each other and
/// with any live broker on this machine. The module initializer (not a fixture, not a base
/// class) is the only mechanism that is guaranteed to run before the first of them.
/// </summary>
internal static class TestHostLicenseOptOut
{
    [ModuleInitializer]
    internal static void DisableBrokerLicenseForThisTestHost() =>
        Environment.SetEnvironmentVariable("DEVTEAM_LICENSE_DISABLED", "1");
}
