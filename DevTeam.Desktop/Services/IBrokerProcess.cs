namespace DevTeam.Desktop.Services;

/// <summary>
/// A spawned child process (the broker) that the shell owns and is responsible for terminating.
/// Abstracted so the shell's lifecycle logic is testable without launching a real process.
/// </summary>
public interface IBrokerProcess : IDisposable
{
    int Id { get; }

    bool HasExited { get; }

    void Kill();

    bool WaitForExit(int milliseconds);
}

/// <summary>Starts a child process. Abstracted for tests.</summary>
public interface IBrokerLauncher
{
    IBrokerProcess Start(string executablePath, IReadOnlyList<string> arguments, string workingDirectory);
}
