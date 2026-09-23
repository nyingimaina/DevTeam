using System.Text;

using DevTeam.Broker.Workflow;

namespace DevTeam.Broker.Gates;

public sealed class CoverageMatrixGate : IGate
{
    public string Name => BuiltinRegistry.CoverageMatrix;

    public Task<GateResult> RunAsync(GateRequest request, CancellationToken cancellationToken)
    {
        var requirements = RequirementDtos.ParseJson(GateInputs.GetOptional(request.Inputs, "requirementsJson"));
        var referenceCorpus = BuildReferenceCorpus(request);

        if (requirements.Count == 0)
            return Task.FromResult(GateResult.Fail("No requirements to cover", "Coverage matrix needs the requirement list."));

        var uncovered = new List<string>();
        var matrix = new StringBuilder();
        matrix.Append("| REQ | Coverage |").AppendLine();
        matrix.Append("| --- | --- |").AppendLine();
        foreach (var requirement in requirements)
        {
            // Same matcher the progress meter uses, so the two can never disagree.
            var covered = RequirementMatcher.MatchesRequirement(referenceCorpus, requirement);
            matrix.Append($"| {requirement.Id} | {(covered ? "covered" : "UNCOVERED")} |").AppendLine();
            if (!covered)
                uncovered.Add(requirement.Id);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (uncovered.Count == 0)
            return Task.FromResult(GateResult.Pass(
                $"All {requirements.Count} requirements are referenced by tests",
                matrix.ToString().TrimEnd()));

        var evidence = new StringBuilder();
        // State the fix, not just the symptom: the whole convention is that a test names the
        // requirement it proves, and without this line the developer is left guessing.
        evidence.Append("Every test must name the requirement it proves — put the requirement id in the test name ")
            .Append("(e.g. REQ_").Append(RequirementMatcher.FirstNumber(requirements[0].Id)).Append("_AddCommand_ValidInputs). ")
            .AppendLine("Then re-run the checks.");
        foreach (var req in uncovered)
            evidence.Append("fail: no test references ").Append(req).AppendLine();
        foreach (var req in requirements)
            if (!uncovered.Contains(req.Id))
                evidence.Append("ok:   covered ").Append(req.Id).AppendLine();
        return Task.FromResult(GateResult.Fail(
            $"{uncovered.Count} requirement(s) have no test reference",
            evidence.ToString().TrimEnd()));
    }

    private static string BuildReferenceCorpus(GateRequest request)
    {
        var corpus = new StringBuilder();
        var testOutput = GateInputs.Get(request.Inputs, "testOutput", string.Empty);
        if (!string.IsNullOrWhiteSpace(testOutput))
            corpus.Append(testOutput).AppendLine();
        corpus.Append(TestFiles.Text(GateInputs.GetOptional(request.Inputs, "testFilesJson")));
        return corpus.ToString();
    }
}
