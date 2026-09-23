using DevTeam.Broker.Context;

namespace DevTeam.Tests.Context;

/// <summary>
/// Fake Repomix. By default it "succeeds" by writing a small XML pack to the requested output
/// path; tests can swap the handler to simulate a missing tool, a timeout, a crash, or a pack
/// that is too large.
/// </summary>
internal sealed class FakeRepomixRunner : IRepomixRunner
{
    public List<(string Workspace, string Output, bool Compress)> Calls { get; } = [];

    public Func<string, string, bool, RepomixResult> Handler { get; set; } = DefaultHandler;

    public static RepomixResult DefaultHandler(string workspace, string output, bool compress)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var content = compress
            ? "<files>\n<file path=\"back-end/src/Core/Adder.cs\">class Adder {}</file>\n<file path=\"back-end/src/Features/adding/AddHandler.cs\">class AddHandler {}</file>\n</files>"
            : "<files>\n<file path=\"back-end/src/Core/Adder.cs\">namespace Core; class Adder { int Add(int a,int b)=>a+b; }</file>\n</files>";
        File.WriteAllText(output, content);
        return new RepomixResult(RepomixOutcome.Success, output, content.Length, "ok", string.Empty);
    }

    public Task<RepomixResult> RunAsync(
        string workspacePath, string outputPath, bool compress,
        IReadOnlyList<string> ignore, TimeSpan timeout, CancellationToken ct)
    {
        Calls.Add((workspacePath, outputPath, compress));
        return Task.FromResult(Handler(workspacePath, outputPath, compress));
    }
}

/// <summary>
/// Fake git facts. <see cref="DescribeResults"/> is a FIFO so a test can model "HEAD changed while
/// Repomix ran" by queueing two different results for one refresh.
/// </summary>
internal sealed class FakeGitProbe : IGitProbe
{
    public GitProbeResult Describe { get; set; } = new(true, "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0", "main", false);

    public Queue<GitProbeResult> DescribeResults { get; } = new();

    public int? Count { get; set; } = 1;

    public Task<GitProbeResult> DescribeAsync(string workspacePath, CancellationToken ct)
        => Task.FromResult(DescribeResults.Count > 0 ? DescribeResults.Dequeue() : Describe);

    public Task<int?> CountCommitsAsync(string workspacePath, string fromCommit, CancellationToken ct)
        => Task.FromResult(Count);
}

/// <summary>A throwaway directory that cleans itself up, for the on-disk refresh tests.</summary>
internal sealed class TempWorkspace : IDisposable
{
    public TempWorkspace(bool withGit = true)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "devteam-context-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        if (withGit)
            Directory.CreateDirectory(System.IO.Path.Combine(Path, ".git", "info"));
    }

    public string Path { get; }

    public string Write(string relative, string content)
    {
        var full = System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public bool Exists(string relative)
        => File.Exists(System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
    }
}
