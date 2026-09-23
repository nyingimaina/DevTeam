using DevTeam.Broker.Domain;

namespace DevTeam.Broker.Workflow;

/// <summary>
/// Stops a stage retrying the same failing checks forever. The inner loop is already bounded, but
/// "Run Stage Again" / the frontend's auto-retry countdown reset it — so a check the agent can
/// never satisfy (a false positive, or a genuine dead-end) would bounce indefinitely. After
/// <see cref="MaxConsecutiveFailures"/> identical failures we stop auto-retrying and ask a person.
/// A different set of failing checks resets the count, so a one-off flake isn't punished.
/// </summary>
public static class GateFailureLoopGuard
{
    public const int MaxConsecutiveFailures = 3;

    public static void RecordFailure(ReleaseStageRun run, IReadOnlyList<string> failingChecks)
    {
        var signature = Signature(failingChecks);
        if (string.Equals(run.LastFailureSignature, signature, StringComparison.Ordinal))
        {
            run.ConsecutiveFailures++;
        }
        else
        {
            run.LastFailureSignature = signature;
            run.ConsecutiveFailures = 1;
        }

        run.AutoRetrySuppressed = run.ConsecutiveFailures >= MaxConsecutiveFailures;
    }

    public static void Clear(ReleaseStageRun run)
    {
        run.ConsecutiveFailures = 0;
        run.LastFailureSignature = null;
        run.AutoRetrySuppressed = false;
    }

    /// <summary>Order-independent so the same failing checks always hash to the same signature.</summary>
    public static string Signature(IReadOnlyList<string> failingChecks)
        => failingChecks.Count == 0
            ? "challenge"
            : string.Join(',', failingChecks.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal));
}
