using DevTeam.Shared;

namespace DevTeam.Broker.Server;

/// <summary>
/// The running broker's boot identity, surfaced on /healthz so a caller can tell WHICH
/// broker answered: the process id, a boot id unique per broker lifetime, and the data
/// directory fingerprint. The desktop shell needs this to adopt a live broker instead of
/// spawning a second one (2026-10-01: adopt-vs-spawn was guesswork, and the guesses fed a
/// nine-broker storm).
/// </summary>
public sealed record BrokerBootInfo(int ProcessId, Guid BootId, string DataDirFingerprint)
{
    public static BrokerBootInfo ForThisProcess(string dataDirectory) =>
        new(Environment.ProcessId, Guid.NewGuid(), BrokerIdentity.DataDirFingerprint(dataDirectory));
}
