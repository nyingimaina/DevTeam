using System.Threading;
using DevTeam.Broker.Licensing;
using DevTeam.Shared;

namespace DevTeam.Tests.Licensing;

// 2026-10-01, 21:43: a desktop restart spawned nine broker lifetimes in sixteen seconds.
// Every one of them opened the same SQLite database and the same Serilog file; one died
// mid-transaction and took a 201 release insert with it, and requests flapped between
// dying brokers (404/503). The license these tests pin is the gate that makes that
// impossible: a second broker on the same data directory exits before it touches anything.
public sealed class BrokerLicenseTests
{
    private static string TempDataDir() =>
        Path.Combine(Path.GetTempPath(), "devteam-license-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Fingerprint_IsStableAcrossCasingAndSeparatorStyle()
    {
        var withBackslashes = BrokerIdentity.DataDirFingerprint(@"C:\Users\tester\.devteam");
        var lowercased = BrokerIdentity.DataDirFingerprint(@"c:\users\tester\.devteam");
        var forwardSlashes = BrokerIdentity.DataDirFingerprint(@"c:/users/tester/.devteam");

        Assert.Equal(withBackslashes, lowercased);
        Assert.Equal(withBackslashes, forwardSlashes);
    }

    [Fact]
    public void Fingerprint_DiffersForDifferentDirectories()
    {
        var first = BrokerIdentity.DataDirFingerprint(@"C:\Users\tester\.devteam");
        var second = BrokerIdentity.DataDirFingerprint(@"C:\Users\other\.devteam");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void MutexName_IsMachineGlobalAndNamespacedPerDataDirectory()
    {
        var name = BrokerIdentity.BrokerMutexName(@"C:\Users\tester\.devteam");

        Assert.StartsWith(@"Global\DevTeam.Broker.", name);
        var suffix = name[@"Global\DevTeam.Broker.".Length..];
        Assert.Equal(16, suffix.Length);
        Assert.True(suffix.All(char.IsAsciiHexDigitLower), $"suffix should be lowercase hex: {suffix}");
    }

    [Fact]
    public void TryAcquire_WhenFree_Acquires()
    {
        var result = NamedMutexBrokerLicense.TryAcquire(TempDataDir());

        Assert.True(result.Acquired);
        Assert.False(result.PreviousHolderDied);
        Assert.NotNull(result.Handle);
        result.Handle!.Dispose();
    }

    [Fact]
    public void TryAcquire_WhileAnotherInstanceHolds_IsRefused()
    {
        var dataDir = TempDataDir();
        using var first = NamedMutexBrokerLicense.TryAcquire(dataDir).Handle;

        // A second broker is a second process on its own thread — and mutex WaitOne is
        // re-entrant for the holder's own thread — so the refusal is probed from a thread
        // that is not the holder's, exactly like the real second instance would be.
        var second = RunOnOwnThread(() => NamedMutexBrokerLicense.TryAcquire(dataDir));

        Assert.False(second.Acquired);
        Assert.Null(second.Handle);
    }

    /// <summary>Runs the probe on a dedicated thread so mutex thread-affinity is modeled honestly.</summary>
    private static T RunOnOwnThread<T>(Func<T> probe)
    {
        var result = default(T)!;
        var thread = new Thread(() => result = probe());
        thread.Start();
        thread.Join();
        return result;
    }

    [Fact]
    public void TryAcquire_AfterTheHolderReleases_AcquiresAgain()
    {
        var dataDir = TempDataDir();
        NamedMutexBrokerLicense.TryAcquire(dataDir).Handle!.Dispose();

        var reacquired = NamedMutexBrokerLicense.TryAcquire(dataDir);

        Assert.True(reacquired.Acquired);
        reacquired.Handle!.Dispose();
    }

    [Fact]
    public void TryAcquire_DifferentDataDirectories_AreIndependentlyLicensed()
    {
        using var first = NamedMutexBrokerLicense.TryAcquire(TempDataDir()).Handle;

        var second = NamedMutexBrokerLicense.TryAcquire(TempDataDir());

        Assert.True(second.Acquired);
        second.Handle!.Dispose();
    }

    [Fact]
    public void TryAcquire_AbandonedByACrashedHolder_TakesOverAndReportsIt()
    {
        var dataDir = TempDataDir();
        var mutexName = BrokerIdentity.BrokerMutexName(dataDir);

        // The crashed-holder simulation: a real thread takes ownership of the mutex and
        // dies without releasing, which is exactly what a killed broker leaves behind
        // while some other handle (a probe, a shell) keeps the mutex object alive.
        Mutex? abandoned = null;
        var holder = new Thread(() => abandoned = new Mutex(true, mutexName));
        holder.Start();
        holder.Join();

        var takeover = NamedMutexBrokerLicense.TryAcquire(dataDir);

        Assert.True(takeover.Acquired);
        Assert.True(takeover.PreviousHolderDied);
        takeover.Handle!.Dispose();
        abandoned?.Dispose();
    }

    [Fact]
    public void Program_RefusesASecondBroker_BeforeTouchingTheSharedDatabaseOrLogFiles()
    {
        var program = ReadBrokerFile("Program.cs");

        // Order is the whole point: the license must be decided before any side effect —
        // the interleaved Serilog writers and the shared SQLite file are what a refused
        // broker must never reach.
        var resolveIdentity = IndexOfRequired(program, "RuntimeIdentity.Resolve");
        var licenseGate = IndexOfRequired(program, "BrokerLicenseGate.TryEnter");
        var createDirectories = IndexOfRequired(program, "Directory.CreateDirectory");
        var buildHost = IndexOfRequired(program, "WebApplication.CreateBuilder");

        Assert.True(resolveIdentity < licenseGate, "the license needs the resolved data directory");
        Assert.True(licenseGate < createDirectories, "a refused broker must not touch the file system");
        Assert.True(licenseGate < buildHost, "a refused broker must not build the host or bind the port");
    }

    [Fact]
    public void LicenseGate_ExitsWithASpecificCode_AndHonorsTheDocumentedOptOut()
    {
        var gate = ReadBrokerFile("Licensing", "BrokerLicenseGate.cs");

        // The distinct exit code is what lets the desktop tell "refused on purpose" from
        // "crashed", which is the difference between adopting the live broker and
        // reporting a startup failure.
        Assert.Contains("BrokerExitCodes.AlreadyRunning", gate);

        // WebApplicationFactory-hosted tests execute Main as well (the factory intercepts
        // app.Run, not the code above it) and run in parallel; without the opt-out they
        // would all contend on the one real machine-global mutex.
        Assert.Contains("DEVTEAM_LICENSE_DISABLED", gate);
    }

    private static int IndexOfRequired(string text, string needle)
    {
        var index = text.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(index >= 0, $"expected to find '{needle}'");
        return index;
    }

    private static string ReadBrokerFile(params string[] parts)
    {
        var repoRoot = FindRepoRoot();
        return File.ReadAllText(Path.Combine([repoRoot, "back-end", "DevTeam.Broker", .. parts]));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DevTeam.slnx")))
                return dir.FullName;
            dir = dir.Parent!;
        }

        throw new InvalidOperationException("Could not locate the repository root (DevTeam.slnx).");
    }
}
