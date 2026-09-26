namespace DevTeam.Desktop.Services;

/// <summary>Checks whether the broker's health endpoint answers. Abstracted for tests.</summary>
public interface IHealthProbe
{
    Task<bool> IsHealthyAsync(Uri healthUrl, CancellationToken cancellationToken);
}

/// <summary>HTTP probe for the broker's <c>/healthz</c> endpoint.</summary>
public sealed class HttpHealthProbe : IHealthProbe, IDisposable
{
    private readonly HttpClient _client;

    public HttpHealthProbe() : this(new HttpClient { Timeout = TimeSpan.FromSeconds(3) }) { }

    public HttpHealthProbe(HttpClient client) => _client = client;

    public async Task<bool> IsHealthyAsync(Uri healthUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client.GetAsync(healthUrl, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public void Dispose() => _client.Dispose();
}
