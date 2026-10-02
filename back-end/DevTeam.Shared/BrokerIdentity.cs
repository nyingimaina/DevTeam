using System.Security.Cryptography;
using System.Text;

namespace DevTeam.Shared;

/// <summary>
/// The identity a real broker instance shares with the world: a stable fingerprint of its
/// data directory. Both the single-instance license (broker side) and the adopt-don't-spawn
/// decision (desktop side) derive from it, so both compute it here — one definition, never two.
/// </summary>
public static class BrokerIdentity
{
    /// <summary>
    /// A stable fingerprint of a broker data directory: the first 16 lowercase hex characters
    /// of SHA-256 over the normalized path. Normalization covers casing and separator style —
    /// the two ways the same directory legitimately arrives spelled differently on Windows.
    /// </summary>
    public static string DataDirFingerprint(string dataDirectory)
    {
        var normalized = (dataDirectory ?? string.Empty).Trim().Replace('/', '\\').ToLowerInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
    }

    /// <summary>
    /// The named mutex guarding the one live broker per data directory. Machine-global (not
    /// per-session) because two broker instances in different sessions are exactly as
    /// dangerous as two in one — they still share the SQLite file and the Serilog files.
    /// </summary>
    public static string BrokerMutexName(string dataDirectory) =>
        $"Global\\DevTeam.Broker.{DataDirFingerprint(dataDirectory)}";
}
