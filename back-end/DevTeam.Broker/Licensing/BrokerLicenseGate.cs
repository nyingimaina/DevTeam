using DevTeam.Shared;

namespace DevTeam.Broker.Licensing;

/// <summary>
/// The Main wiring of the single-instance license: one call, first thing after identity
/// resolution, and Main returns immediately when refused. Kept as its own class so Program
/// stays a composition root and the refusal semantics stay testable in isolation.
/// </summary>
public static class BrokerLicenseGate
{
    // Rooted for the process lifetime on purpose. A local would only be alive while the
    // JIT considers it in use; losing the reference would close the mutex handle and let
    // a second broker start while this one still runs. The OS releases it when the
    // process dies, which is exactly the ownership semantics the license needs.
    private static IBrokerLicenseHandle? _held;

    /// <summary>
    /// True when this process may run as THE broker for its data directory. False means a
    /// live broker already holds the license: a plain sentence has been written to stderr,
    /// the exit code is <see cref="BrokerExitCodes.AlreadyRunning"/>, and Main must return now.
    /// </summary>
    public static bool TryEnter(RuntimeIdentity identity)
    {
        // WebApplicationFactory-hosted tests execute Main as well (the factory intercepts
        // app.Run, not the code above it) and run in parallel against the one real
        // machine-global mutex; without this opt-out the test suite would contend with
        // itself and with any live broker. The variable is an explicit, documented
        // switch (see TestHostLicenseOptOut in DevTeam.Tests) — the license protects an
        // operator from accidental second brokers, not from their own deliberate opt-out.
        if (Environment.GetEnvironmentVariable("DEVTEAM_LICENSE_DISABLED") == "1")
            return true;

        var license = NamedMutexBrokerLicense.TryAcquire(identity.DataDirectory);
        if (!license.Acquired)
        {
            // Stderr only, deliberately: the refusing instance must not open the shared
            // Serilog file — interleaved writers are part of the storm this gate prevents.
            // The exit code tells the desktop shell "refused on purpose", not "crashed".
            Console.Error.WriteLine(
                "Another DevTeam broker is already running for this data directory " +
                $"({identity.DataDirectory}). This instance will not start.");
            Environment.ExitCode = BrokerExitCodes.AlreadyRunning;
            return false;
        }

        _held = license.Handle;
        return true;
    }
}
