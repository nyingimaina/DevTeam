namespace DevTeam.Broker.Models;

/// <summary>
/// A model's standing, on two 1–5 scales, so the UI can show *why* the default order is what it
/// is. These are curated estimates, not measurements — the UI labels them as such.
/// </summary>
public sealed record ModelRating(string ModelId, int Smartness, int Cost, string Note)
{
    /// <summary>
    /// Weighted so capability dominates and price breaks ties: a cheap model that can't finish
    /// multi-step work is the most expensive thing there is. Same scale for every model.
    /// </summary>
    public double Score => (Smartness * 3) - Cost;
}

/// <summary>
/// The curated starting point: which models to try, in what order, with the cost/smartness
/// estimates behind that order.
///
/// The order is deliberately provider-spread, not a pure score sort — a provider-level rate limit
/// hits every model from that provider, so consecutive entries must come from different providers
/// for failover to have anywhere to go.
/// </summary>
public static class ModelCatalog
{
    public const int SeedLimit = 10;

    /// <summary>The default first choice. Pinned rather than scored.</summary>
    public const string PinnedFirst = "opencode/big-pickle";

    private static readonly Dictionary<string, ModelRating> Ratings = new(StringComparer.Ordinal)
    {
        // opencode — free, so cheapest; heavily rate-limited in practice.
        ["opencode/big-pickle"] = new("opencode/big-pickle", 3, 1, "Free; the current default."),
        ["opencode/nemotron-3-ultra-free"] = new("opencode/nemotron-3-ultra-free", 4, 1, "Free, stronger than the default."),
        ["opencode/nemotron-3.5-lightning-free"] = new("opencode/nemotron-3.5-lightning-free", 3, 1, "Free and fast."),
        ["opencode/mimo-v2.5-free"] = new("opencode/mimo-v2.5-free", 3, 1, "Free."),
        ["opencode/muse-spark-1.3-contributor-free"] = new("opencode/muse-spark-1.3-contributor-free", 3, 1, "Free."),
        ["opencode/ling-3.0-flash-fin-free"] = new("opencode/ling-3.0-flash-fin-free", 2, 1, "Free, lightweight."),
        ["opencode/muse-spark-1.2-contributor-free"] = new("opencode/muse-spark-1.2-contributor-free", 2, 1, "Free, lightweight."),

        // opencode-go — paid but inexpensive tier; the workhorses.
        ["opencode-go/kimi-k3"] = new("opencode-go/kimi-k3", 5, 3, "Strong multi-step coding."),
        ["opencode-go/glm-5.3"] = new("opencode-go/glm-5.3", 5, 3, "Strong multi-step coding."),
        ["opencode-go/deepseek-v4-pro"] = new("opencode-go/deepseek-v4-pro", 5, 3, "Strong multi-step coding."),
        ["opencode-go/deepseek-v4.1-flash"] = new("opencode-go/deepseek-v4.1-flash", 4, 2, "Good value; fast."),
        ["opencode-go/kimi-k2.7-code"] = new("opencode-go/kimi-k2.7-code", 4, 2, "Code-focused, good value."),
        ["opencode-go/qwen3.6-plus"] = new("opencode-go/qwen3.6-plus", 4, 2, "Good value."),
        ["opencode-go/minimax-m3"] = new("opencode-go/minimax-m3", 4, 3, "Solid all-rounder."),
        ["opencode-go/qwen3.8-max"] = new("opencode-go/qwen3.8-max", 4, 3, "Solid all-rounder."),
        ["opencode-go/grok-4.7"] = new("opencode-go/grok-4.7", 4, 3, "Solid all-rounder."),
        ["opencode-go/gpt-5.6-luna"] = new("opencode-go/gpt-5.6-luna", 4, 3, "Solid all-rounder."),
        ["opencode-go/glm-5.3-flash"] = new("opencode-go/glm-5.3-flash", 3, 2, "Fast, lighter."),
        ["opencode-go/qwen3.8-flash"] = new("opencode-go/qwen3.8-flash", 3, 2, "Fast, lighter."),
        ["opencode-go/deepseek-v4-flash"] = new("opencode-go/deepseek-v4-flash", 3, 2, "Fast, lighter."),
        ["opencode-go/longcat-2.0"] = new("opencode-go/longcat-2.0", 3, 2, "Fast, lighter."),

        // google — a third provider, for real provider-level spread.
        ["google/gemini-3.5-flash"] = new("google/gemini-3.5-flash", 4, 3, "Solid, and a different provider."),
        ["google/gemini-3.7-flash"] = new("google/gemini-3.7-flash", 4, 3, "Solid, and a different provider."),
        ["google/gemini-3.1-pro-preview"] = new("google/gemini-3.1-pro-preview", 5, 5, "Most capable, most expensive."),
        ["google/gemini-3.1-flash-lite"] = new("google/gemini-3.1-flash-lite", 3, 2, "Cheap and light."),
    };

    // Pinned first, then strictly alternating providers so a provider-level failure always has a
    // genuinely different provider to fall back to.
    private static readonly string[] SeedOrder =
    [
        PinnedFirst,
        "opencode-go/kimi-k3",
        "opencode/nemotron-3-ultra-free",
        "opencode-go/glm-5.3",
        "google/gemini-3.5-flash",
        "opencode-go/deepseek-v4-pro",
        "opencode/nemotron-3.5-lightning-free",
        "opencode-go/deepseek-v4.1-flash",
        "google/gemini-3.7-flash",
        "opencode-go/kimi-k2.7-code",
    ];

    public static IReadOnlyList<string> SeedModelIds => SeedOrder;

    /// <summary>The curated standing, or null for a model we have no estimate for (shown as "unknown").</summary>
    public static ModelRating? RatingFor(string modelId)
        => Ratings.TryGetValue(modelId, out var rating) ? rating : null;
}
