using System.Text;

using DevTeam.Broker.Workflow;

namespace DevTeam.Broker.Gates;

public sealed class CoverageMatrixGate : IGate
{
    public string Name => BuiltinRegistry.CoverageMatrix;

    public Task<GateResult> RunAsync(GateRequest request, CancellationToken cancellationToken)
    {
        var requirements = RequirementDtos.ParseJson(GateInputs.GetOptional(request.Inputs, "requirementsJson"));
        var testOutput = GateInputs.Get(request.Inputs, "testOutput", string.Empty);
        var featureKey = GateInputs.Get(request.Inputs, "featureKey", request.FeatureKey ?? string.Empty);

        if (requirements.Count == 0)
            return Task.FromResult(GateResult.Fail("No requirements to cover", "Coverage matrix needs the requirement list."));

        var uncovered = new List<string>();
        var matrix = new StringBuilder();
        matrix.Append("| REQ | Coverage |").AppendLine();
        matrix.Append("| --- | --- |").AppendLine();
        foreach (var requirement in requirements)
        {
            var covered = TestCovers(testOutput, requirement.Id) ||
                TestCovers(testOutput, Sanitize(requirement.Title));
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
        foreach (var req in uncovered)
            evidence.Append("fail: no test references ").Append(req).AppendLine();
        foreach (var req in requirements)
            if (!uncovered.Contains(req.Id))
                evidence.Append("ok:   covered ").Append(req.Id).AppendLine();
        return Task.FromResult(GateResult.Fail(
            $"{uncovered.Count} requirement(s) have no test reference",
            evidence.ToString().TrimEnd()));
    }

    private static bool TestCovers(string testOutput, string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;
        return testOutput.Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    private static string Sanitize(string title)
        => title.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
}