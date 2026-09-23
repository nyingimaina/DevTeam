namespace DevTeam.Broker.Domain;

/// <summary>
/// A generic named application setting, for operator preferences that belong to the machine
/// rather than to any one workspace or release (e.g. "verbose logging"). Absent means "use the
/// default", so a missing row is never an error.
/// </summary>
public sealed class AppSetting
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
