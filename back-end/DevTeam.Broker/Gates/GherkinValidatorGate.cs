using System.Text;

using DevTeam.Broker.Workflow;

namespace DevTeam.Broker.Gates;

public sealed class GherkinValidatorGate : IGate
{
    private static readonly string[] RequiredKeywords = ["given", "when", "then"];

    public string Name => BuiltinRegistry.GherkinValidator;

    public Task<GateResult> RunAsync(GateRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.FeatureKey))
            return Task.FromResult(GateResult.Fail("gherkin_validator requires a featureKey", "featureKey input missing"));

        var featureKey = request.FeatureKey;
        var requirements = RequirementDtos.ParseJson(GateInputs.GetOptional(request.Inputs, "requirementsJson"));

        if (requirements.Count == 0)
            return Task.FromResult(GateResult.Fail(
                "No requirements to validate",
                "A requirements list is required so every REQ can prove Given/When/Then acceptance criteria."));

        var issues = new List<string>();
        foreach (var requirement in requirements)
        {
            var missingKeywords = RequiredKeywords
                .Where(keyword => !ContainsKeyword(requirement.AcceptanceCriteria, keyword))
                .Select(keyword => char.ToUpper(keyword[0]) + keyword[1..])
                .ToArray();

            if (missingKeywords.Length > 0)
                issues.Add($"{requirement.Id}: acceptance criteria missing {string.Join(", ", missingKeywords)}");
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (issues.Count > 0)
        {
            var evidence = new StringBuilder();
            foreach (var issue in issues)
                evidence.Append("fail: ").Append(issue).AppendLine();
            foreach (var requirement in requirements)
                if (!issues.Any(i => i.StartsWith(requirement.Id + ":", StringComparison.Ordinal)))
                    evidence.Append("ok:   ").Append(requirement.Id).AppendLine();
            return Task.FromResult(GateResult.Fail(
                $"{issues.Count} requirement(s) lack complete Gherkin acceptance criteria",
                evidence.ToString().TrimEnd()));
        }

        var report = new StringBuilder();
        foreach (var requirement in requirements)
            report.Append("ok:   ").Append(requirement.Id).AppendLine();
        return Task.FromResult(GateResult.Pass(
            $"All {requirements.Count} requirements have Given/When/Then acceptance criteria",
            report.ToString().TrimEnd()));
    }

    private static bool ContainsKeyword(string? text, string keyword)
        => text is not null && text.Contains(keyword, StringComparison.OrdinalIgnoreCase);
}