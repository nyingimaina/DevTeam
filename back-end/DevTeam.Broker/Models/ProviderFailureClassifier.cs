namespace DevTeam.Broker.Models;

/// <summary>Why a model provider refused or failed a turn, in terms the app can act on.</summary>
public enum ProviderFailureKind
{
    None,
    RateLimited,
    Unavailable,
    ConnectFailed,
    ModelInvalid,
    Unknown,
}

/// <param name="Cooldown">How long this model should be skipped before it is tried again.</param>
public sealed record ProviderFailure(ProviderFailureKind Kind, string PlainReason, TimeSpan Cooldown)
{
    public static readonly ProviderFailure None = new(ProviderFailureKind.None, string.Empty, TimeSpan.Zero);

    public bool IsFailure => Kind != ProviderFailureKind.None;
}

/// <summary>
/// Reads the failure wording the agent emits (its stderr and its own log) and turns it into a
/// category, a sentence a user can understand, and how long to avoid that model.
///
/// This exists because opencode does not report provider failures over ACP at all: it retries
/// internally and the prompt simply never returns. Without reading the other channel we can only
/// ever say "the agent stopped responding" — true, and useless.
/// </summary>
public static class ProviderFailureClassifier
{
    // Rate limits clear on their own; a bad model id won't fix itself soon.
    public static readonly TimeSpan RateLimitCooldown = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan UnavailableCooldown = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ModelInvalidCooldown = TimeSpan.FromMinutes(30);

    public static ProviderFailure Classify(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return ProviderFailure.None;

        // A rate limit is the most common and the most transient.
        if (Has(line, "rate limit") || Has(line, "rate-limit") || Has(line, "high demand") || Has(line, "429"))
            return new(ProviderFailureKind.RateLimited, "The AI service is rate-limiting this model.", RateLimitCooldown);

        // A model the provider doesn't offer will keep failing until someone changes it.
        if ((Has(line, "not found") && Has(line, "model")) || Has(line, "unknown model") || Has(line, "invalid model")
            || Has(line, "model not")) 
            return new(ProviderFailureKind.ModelInvalid, "This model isn't available from the AI service.", ModelInvalidCooldown);

        if (Has(line, "unable to connect") || Has(line, "socket") || Has(line, "econnrefused") || Has(line, "typo in the url"))
            return new(ProviderFailureKind.ConnectFailed, "Couldn't reach the AI service.", UnavailableCooldown);

        if (Has(line, "unavailable") || Has(line, "502") || Has(line, "503") || Has(line, "504") || Has(line, "overloaded"))
            return new(ProviderFailureKind.Unavailable, "The AI service is temporarily unavailable.", UnavailableCooldown);

        // Any other stream error still means the model didn't answer — worth skipping, but we
        // don't know why, so the sentence stays honest about that.
        if (Has(line, "stream error") || Has(line, "ai_apicallerror") || Has(line, "ai_retryerror"))
            return new(ProviderFailureKind.Unknown, "The AI service failed the request.", UnavailableCooldown);

        return ProviderFailure.None;
    }

    private static bool Has(string text, string token)
        => text.Contains(token, StringComparison.OrdinalIgnoreCase);
}
