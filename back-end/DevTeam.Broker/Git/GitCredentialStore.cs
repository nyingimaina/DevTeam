using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevTeam.Shared;

namespace DevTeam.Broker.Git;

/// <summary>
/// Stores named PATs (personal access tokens), shareable across workspaces —
/// e.g. one "github-personal" token used by several projects' remotes. Never
/// exposed back over the API except by name — only TryGetToken's plaintext
/// value is used, and only to build a per-invocation git auth header.
/// </summary>
public interface IGitCredentialStore
{
    bool HasToken(string name);

    void SetToken(string name, string token);

    string? TryGetToken(string name);

    IReadOnlyList<string> ListNames();
}

[SupportedOSPlatform("windows")]
public sealed class DpapiGitCredentialStore : IGitCredentialStore
{
    private readonly string _filePath;

    public DpapiGitCredentialStore(RuntimeIdentity identity)
        : this(Path.Combine(identity.DataDirectory, "git-credentials.dat"))
    {
    }

    public DpapiGitCredentialStore(string filePath)
    {
        _filePath = filePath;
    }

    public bool HasToken(string name) => LoadAll().ContainsKey(name);

    public void SetToken(string name, string token)
    {
        var all = LoadAll();
        all[name] = token;
        SaveAll(all);
    }

    public string? TryGetToken(string name) => LoadAll().TryGetValue(name, out var token) ? token : null;

    public IReadOnlyList<string> ListNames() => LoadAll().Keys.ToArray();

    private Dictionary<string, string> LoadAll()
    {
        if (!File.Exists(_filePath))
            return new Dictionary<string, string>();

        var protectedBytes = File.ReadAllBytes(_filePath);
        var plainBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
        var json = Encoding.UTF8.GetString(plainBytes);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
    }

    private void SaveAll(Dictionary<string, string> all)
    {
        var json = JsonSerializer.Serialize(all);
        var plainBytes = Encoding.UTF8.GetBytes(json);
        var protectedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllBytes(_filePath, protectedBytes);
    }
}
