namespace DevTeam.Shared;

/// <summary>
/// Process exit codes the desktop shell can read off a broker child. They distinguish
/// "refused on purpose" from "crashed", which is the difference between adopting the live
/// broker and reporting a startup failure (2026-10-01: nine spawned brokers died on the
/// port bind and the shell had no way to tell that from a genuine failure to start).
/// </summary>
public static class BrokerExitCodes
{
    /// <summary>Another broker already holds the license for this data directory.</summary>
    public const int AlreadyRunning = 73;
}
