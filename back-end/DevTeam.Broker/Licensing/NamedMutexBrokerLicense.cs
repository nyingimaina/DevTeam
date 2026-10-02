using System.Threading;
using DevTeam.Shared;

namespace DevTeam.Broker.Licensing;

/// <summary>The outcome of trying to become the one live broker for a data directory.</summary>
/// <param name="Acquired">True when this process now holds the license.</param>
/// <param name="PreviousHolderDied">True when the license was taken over from a crashed holder.</param>
/// <param name="Handle">Holds the license until disposed; null when refused.</param>
public sealed record BrokerLicenseAcquisition(
    bool Acquired, bool PreviousHolderDied, IBrokerLicenseHandle? Handle)
{
    public static BrokerLicenseAcquisition Refused { get; } = new(false, false, null);
}

/// <summary>
/// Releasing the license. Real brokers hold it for the process lifetime and never dispose it;
/// the handle exists so tests and future embedders can return a license cleanly.
/// </summary>
public interface IBrokerLicenseHandle : IDisposable;

/// <summary>
/// The single-instance license: a machine-global named mutex per broker data directory.
/// Two live brokers on one database is the worst observed failure mode (2026-10-01 21:43:
/// nine broker lifetimes in sixteen seconds, interleaved Serilog writers, a rolled-back
/// release insert, 404/503 request flapping). A second broker must exit before it touches
/// the database or a log file, so this is decided in Main before anything with a side effect.
/// </summary>
public static class NamedMutexBrokerLicense
{
    public static BrokerLicenseAcquisition TryAcquire(string dataDirectory)
    {
        var mutex = new Mutex(initiallyOwned: false, BrokerIdentity.BrokerMutexName(dataDirectory));
        try
        {
            var previousHolderDied = false;
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                // The previous holder died without releasing. Ownership is granted along
                // with the exception, and a crashed broker's license is exactly what a
                // legitimate restart needs to take over.
                acquired = true;
                previousHolderDied = true;
            }

            if (!acquired)
            {
                mutex.Dispose();
                return BrokerLicenseAcquisition.Refused;
            }

            return new BrokerLicenseAcquisition(true, previousHolderDied, new MutexLicenseHandle(mutex));
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Disposing releases the mutex. The catch on ReleaseMutex covers disposal from a thread
    /// other than the acquiring one (mutex ownership is thread-affine); the license is then
    /// already gone and there is nothing left to release.
    /// </summary>
    private sealed class MutexLicenseHandle(Mutex mutex) : IBrokerLicenseHandle
    {
        public void Dispose()
        {
            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not the owner anymore; nothing to release.
            }

            mutex.Dispose();
        }
    }
}
