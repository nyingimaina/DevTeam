using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DevTeam.Broker;
using DevTeam.Broker.Diagnostics;
using DevTeam.Broker.Server;

namespace DevTeam.Tests;

/// <summary>
/// The support hand-off surface: a quotable request reference, a verbose-logging switch, and one
/// file a novice can send to a specialist.
/// </summary>
public class ApiDiagnosticsTests : IClassFixture<ApiIntegrationTests.AppFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly ApiIntegrationTests.AppFactory _factory;

    public ApiDiagnosticsTests(ApiIntegrationTests.AppFactory factory) => _factory = factory;

    [Fact]
    public async Task EveryApiResponse_CarriesAQuotableRequestReference()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/info");

        var reference = Assert.Single(response.Headers.GetValues("X-Request-Id"));
        Assert.False(string.IsNullOrWhiteSpace(reference));
    }

    [Fact]
    public async Task AClientSuppliedReference_IsHonoured()
    {
        var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/info");
        request.Headers.Add("X-Request-Id", "abc123def456");

        var response = await client.SendAsync(request);

        Assert.Equal("abc123def456", Assert.Single(response.Headers.GetValues("X-Request-Id")));
    }

    [Fact]
    public async Task VerboseLogging_CanBeTurnedOnAndOff()
    {
        var client = _factory.CreateClient();

        var initial = await client.GetFromJsonAsync<DiagnosticsSettingsDto>("/api/diagnostics/settings", JsonOptions);
        Assert.False(initial!.VerboseLogging);

        var turnedOn = await client.PostAsJsonAsync("/api/diagnostics/settings", new { verboseLogging = true }, JsonOptions);
        turnedOn.EnsureSuccessStatusCode();
        Assert.True((await turnedOn.Content.ReadFromJsonAsync<DiagnosticsSettingsDto>(JsonOptions))!.VerboseLogging);

        // The logs folder is told to the user so they can also look for themselves.
        Assert.False(string.IsNullOrWhiteSpace(initial.LogsDirectory));

        var turnedOff = await client.PostAsJsonAsync("/api/diagnostics/settings", new { verboseLogging = false }, JsonOptions);
        Assert.False((await turnedOff.Content.ReadFromJsonAsync<DiagnosticsSettingsDto>(JsonOptions))!.VerboseLogging);
    }

    [Fact]
    public async Task TheBundle_CarriesWhatASpecialistNeeds()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/diagnostics/bundle", new
        {
            frontendErrors = new[]
            {
                new { at = "2026-01-01T00:00:00Z", message = "Failed to load release", url = "/api/releases/x", status = 500, requestId = "abc123" },
            },
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);

        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("app-info.json", names);
        Assert.Contains("settings.json", names);
        Assert.Contains("state.json", names);
        Assert.Contains("models.json", names);
        Assert.Contains("frontend-errors.json", names);

        var appInfo = await ReadEntryAsync(zip, "app-info.json");
        Assert.Contains("version", appInfo);
        // The agent's own log is where a provider refusal is recorded, so the bundle points at it.
        Assert.Contains("opencodeLogPath", appInfo);

        // "Is my model actually being used?" — asked and answered in the file.
        var models = await ReadEntryAsync(zip, "models.json");
        Assert.Contains("recentSessions", models);

        // The UI's own errors travel with the server's, so both halves of an incident line up.
        var frontendErrors = await ReadEntryAsync(zip, "frontend-errors.json");
        Assert.Contains("Failed to load release", frontendErrors);
        Assert.Contains("abc123", frontendErrors);
    }

    [Fact]
    public async Task ATestNotification_ReachesThePlatformAdapter()
    {
        // "Did that work?" has to be answerable without waiting for a real pipeline event.
        _factory.Notifier.Requests.Clear();
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/notifications/test", null);

        response.EnsureSuccessStatusCode();
        var request = Assert.Single(_factory.Notifier.Requests);
        Assert.Contains("working", request.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NotificationSettings_RoundTrip()
    {
        var client = _factory.CreateClient();

        var updated = await client.PostAsJsonAsync("/api/notifications/settings",
            new { sound = false }, JsonOptions);
        updated.EnsureSuccessStatusCode();
        Assert.False((await updated.Content.ReadFromJsonAsync<NotificationSettingsDto>(JsonOptions))!.Sound);

        var read = await client.GetFromJsonAsync<NotificationSettingsDto>("/api/notifications/settings", JsonOptions);
        Assert.False(read!.Sound);
        // Untouched fields keep their defaults.
        Assert.True(read.NeedsAttention);

        (await client.PostAsJsonAsync("/api/notifications/settings", new { sound = true }, JsonOptions)).EnsureSuccessStatusCode();
    }

    private static async Task<string> ReadEntryAsync(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry!.Open());
        return await reader.ReadToEndAsync();
    }
}
