namespace DevTeam.Desktop.Services;

/// <summary>Reports whether the embedded view can actually display the web UI.</summary>
public interface IWebView2Probe
{
    Task<bool> ProbeAsync(TimeSpan timeout, Uri targetUrl);
}
