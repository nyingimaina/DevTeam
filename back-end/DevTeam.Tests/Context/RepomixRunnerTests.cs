using DevTeam.Broker.Context;
using DevTeam.Broker.Gates;

namespace DevTeam.Tests.Context;

public class RepomixRunnerTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private sealed class ScriptedProcessRunner(Func<ProcessRunRequest, ProcessRunResult> handler) : IProcessRunner
    {
        public List<ProcessRunRequest> Calls { get; } = [];

        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken)
        {
            Calls.Add(request);
            return Task.FromResult(handler(request));
        }
    }

    private static ProcessRunResult Ok(string output = "", bool timedOut = false, int exitCode = 0)
        => new(exitCode, output, string.Empty, timedOut, TimeSpan.Zero);

    [Fact]
    public async Task Succeeds_WhenTheProcessWritesTheOutputFile()
    {
        var outputPath = Path.Combine(_workspace.Path, "out", "pack.xml");
        var runner = new ScriptedProcessRunner(_ =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(outputPath, "<files />");
            return Ok();
        });

        var result = await new RepomixRunner(runner, "repomix")
            .RunAsync(_workspace.Path, outputPath, compress: false, ["**/bin/**"], TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(RepomixOutcome.Success, result.Outcome);
        Assert.True(result.Bytes > 0);
    }

    [Fact]
    public async Task Missing_WhenTheToolIsNotOnPath()
    {
        var runner = new ScriptedProcessRunner(_ => new ProcessRunResult(1, string.Empty, "'repomix' is not recognized as an internal or external command", false, TimeSpan.Zero));

        var result = await new RepomixRunner(runner, "repomix")
            .RunAsync(_workspace.Path, Path.Combine(_workspace.Path, "pack.xml"), false, [], TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(RepomixOutcome.Missing, result.Outcome);
    }

    [Fact]
    public async Task Missing_WhenTheProcessCannotStart()
    {
        var runner = new ScriptedProcessRunner(_ => new ProcessRunResult(-1, string.Empty, "Process failed to start.", false, TimeSpan.Zero));

        var result = await new RepomixRunner(runner, "repomix")
            .RunAsync(_workspace.Path, Path.Combine(_workspace.Path, "pack.xml"), false, [], TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(RepomixOutcome.Missing, result.Outcome);
    }

    [Fact]
    public async Task TimedOut_IsClassifiedAsTimedOut()
    {
        var runner = new ScriptedProcessRunner(_ => new ProcessRunResult(-1, string.Empty, string.Empty, true, TimeSpan.FromSeconds(5)));

        var result = await new RepomixRunner(runner, "repomix")
            .RunAsync(_workspace.Path, Path.Combine(_workspace.Path, "pack.xml"), false, [], TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(RepomixOutcome.TimedOut, result.Outcome);
    }

    [Fact]
    public async Task Failed_WhenTheProcessExitsNonZero()
    {
        var runner = new ScriptedProcessRunner(_ => new ProcessRunResult(2, string.Empty, "boom", false, TimeSpan.Zero));

        var result = await new RepomixRunner(runner, "repomix")
            .RunAsync(_workspace.Path, Path.Combine(_workspace.Path, "pack.xml"), false, [], TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(RepomixOutcome.Failed, result.Outcome);
        Assert.Contains("boom", result.StderrPreview);
    }

    [Fact]
    public void BuildInvocation_QuotesPathsAndAddsFlags()
    {
        var runner = new RepomixRunner(new ScriptedProcessRunner(_ => Ok()), "repomix");
        var output = @"C:\work dir\pack.xml";

        var (_, arguments) = runner.BuildInvocation(@"C:\work dir", output, compress: true, ["**/bin/**", "devteam/context/**"]);

        Assert.Contains("--style xml", arguments);
        Assert.Contains("--compress", arguments);
        Assert.Contains($"--output \"{output}\"", arguments);
        Assert.Contains("--ignore", arguments);
    }
}
