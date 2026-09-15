using System.Runtime.Versioning;
using DevTeam.Broker.Git;

namespace DevTeam.Tests;

[SupportedOSPlatform("windows")]
public class GitCredentialStoreTests : IDisposable
{
    private readonly string _filePath;

    public GitCredentialStoreTests()
    {
        _filePath = Path.Combine(Path.GetTempPath(), "devteam-cred-" + Guid.NewGuid().ToString("N") + ".dat");
    }

    public void Dispose()
    {
        if (File.Exists(_filePath))
            File.Delete(_filePath);
    }

    [Fact]
    public void HasToken_BeforeSetToken_IsFalse()
    {
        var store = new DpapiGitCredentialStore(_filePath);
        Assert.False(store.HasToken("github"));
    }

    [Fact]
    public void SetToken_ThenTryGetToken_RoundTrips()
    {
        var store = new DpapiGitCredentialStore(_filePath);
        store.SetToken("github", "ghp_supersecrettoken123");

        Assert.True(store.HasToken("github"));
        Assert.Equal("ghp_supersecrettoken123", store.TryGetToken("github"));
    }

    [Fact]
    public void SetToken_StoresEncryptedBytesNotPlaintext()
    {
        var store = new DpapiGitCredentialStore(_filePath);
        store.SetToken("github", "ghp_supersecrettoken123");

        var fileBytes = File.ReadAllBytes(_filePath);
        var fileText = System.Text.Encoding.UTF8.GetString(fileBytes);
        Assert.DoesNotContain("ghp_supersecrettoken123", fileText);
    }

    [Fact]
    public void SetToken_CalledTwiceWithSameName_OverwritesPreviousToken()
    {
        var store = new DpapiGitCredentialStore(_filePath);
        store.SetToken("github", "first-token");
        store.SetToken("github", "second-token");

        Assert.Equal("second-token", store.TryGetToken("github"));
    }

    [Fact]
    public void TryGetToken_WhenNameNotStored_ReturnsNull()
    {
        var store = new DpapiGitCredentialStore(_filePath);
        Assert.Null(store.TryGetToken("github"));
    }

    [Fact]
    public void SetToken_MultipleNames_AreIndependentAndListable()
    {
        var store = new DpapiGitCredentialStore(_filePath);
        store.SetToken("github-personal", "token-a");
        store.SetToken("gitlab-work", "token-b");

        Assert.Equal("token-a", store.TryGetToken("github-personal"));
        Assert.Equal("token-b", store.TryGetToken("gitlab-work"));
        Assert.Equal(["github-personal", "gitlab-work"], store.ListNames().OrderBy(n => n));
    }

    [Fact]
    public void ListNames_NeverExposesTokenValues()
    {
        var store = new DpapiGitCredentialStore(_filePath);
        store.SetToken("github", "ghp_supersecrettoken123");

        Assert.DoesNotContain(store.ListNames(), n => n.Contains("supersecret"));
    }

    [Fact]
    public void SetToken_PersistsAcrossStoreInstances()
    {
        var store1 = new DpapiGitCredentialStore(_filePath);
        store1.SetToken("github", "persisted-token");

        var store2 = new DpapiGitCredentialStore(_filePath);
        Assert.Equal("persisted-token", store2.TryGetToken("github"));
        Assert.Contains("github", store2.ListNames());
    }
}
