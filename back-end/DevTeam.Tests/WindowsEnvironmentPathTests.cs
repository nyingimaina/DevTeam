using DevTeam.Shared;

namespace DevTeam.Tests;

/// <summary>
/// A DevTeam launched from the Start Menu inherits the PATH Explorer captured at logon, so a CLI
/// installed afterwards is invisible to it even though a fresh terminal finds it. Merging the
/// persisted machine and user PATH back in closes that gap.
/// </summary>
public sealed class WindowsEnvironmentPathTests
{
    [Fact]
    public void Compose_KeepsProcessEntriesFirst()
    {
        var result = WindowsEnvironmentPath.Compose(
            machinePath: @"C:\Windows",
            userPath: @"C:\Users\me\bin",
            processPath: @"C:\early");

        Assert.Equal(@"C:\early;C:\Windows;C:\Users\me\bin", result);
    }

    [Fact]
    public void Compose_AddsADirectoryMissingFromTheStaleProcessPath()
    {
        var result = WindowsEnvironmentPath.Compose(
            machinePath: null,
            userPath: @"C:\Users\me\AppData\Roaming\npm",
            processPath: @"C:\Windows");

        Assert.Contains(@"C:\Users\me\AppData\Roaming\npm", result);
    }

    [Fact]
    public void Compose_RemovesDuplicatesCaseInsensitively()
    {
        var result = WindowsEnvironmentPath.Compose(
            machinePath: @"C:\Windows",
            userPath: @"C:\WINDOWS",
            processPath: null);

        Assert.Equal(@"C:\Windows", result);
    }

    [Fact]
    public void Compose_IgnoresEmptyAndWhitespaceEntries()
    {
        var result = WindowsEnvironmentPath.Compose(
            machinePath: @"C:\Windows;;   ",
            userPath: null,
            processPath: " ");

        Assert.Equal(@"C:\Windows", result);
    }

    [Fact]
    public void Compose_ExpandsEnvironmentVariables()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var result = WindowsEnvironmentPath.Compose(null, @"%USERPROFILE%\npm", null);

        Assert.Contains(Path.Combine(userProfile, "npm"), result);
    }

    [Fact]
    public void Compose_ReturnsNullWhenThereIsNothingToCompose()
    {
        Assert.Null(WindowsEnvironmentPath.Compose(null, null, null));
    }

    [Fact]
    public void Compose_TrimsQuotedEntries()
    {
        var result = WindowsEnvironmentPath.Compose(@"  ""C:\Windows""  ", null, null);

        Assert.Equal(@"C:\Windows", result);
    }
}
