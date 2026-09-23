using DevTeam.Broker.Gates;

using DevTeam.Broker.Workflow;

namespace DevTeam.Tests.Gates;

public class SliceAllowlistTests
{
    private static readonly string[] Shared = ["Program.cs", "DevTeamDbContext.cs"];
    private static readonly string[] Templates = ["back-end/**/Features/Login", "front-end/app/login"];

    [Theory]
    [InlineData("back-end/Features/Login/LoginController.cs", true)]
    [InlineData("back-end/Foo/Features/Login/Deep/Thing.cs", true)]
    [InlineData("front-end/app/login/page.tsx", true)]
    [InlineData("front-end/app/login/LoginForm.tsx", true)]
    [InlineData("front-end/app/login2/page.tsx", false)]
    [InlineData("back-end/Features/Login2/Thing.cs", false)]
    public void CodePathTemplates_MatchConcretePaths(string path, bool expected)
        => Assert.Equal(expected, SliceAllowlist.IsAllowed(path, "login", Shared, Templates));

    [Theory]
    [InlineData("Program.cs")]
    [InlineData("DevTeamDbContext.cs")]
    [InlineData("devteam/features/login/manifest.yaml")]
    [InlineData("devteam/features/login/context.md")]
    [InlineData("devteam/release.yaml")]
    [InlineData("back-end\\Features\\Login\\LoginController.cs")]
    public void SharedAndArtifactPaths_AreAllowed(string path)
        => Assert.True(SliceAllowlist.IsAllowed(path, "login", Shared, Templates));

    [Theory]
    [InlineData("OtherFile.cs")]
    [InlineData("front-end/app/home/page.tsx")]
    [InlineData("dll/thing.dll")]
    public void AnythingElse_IsRejected(string path)
        => Assert.False(SliceAllowlist.IsAllowed(path, "login", Shared, Templates));

    [Fact]
    public void SharedEntries_SupportTheSameGlobSyntaxAsCodePathTemplates()
    {
        // Shared is a declared-intent list (see DeveloperScopeSnapshot) — a developer whose
        // legitimate touch spans a whole folder shouldn't have to enumerate every file in it.
        var shared = new[] { "Program.cs", "src/Utils/**" };

        Assert.True(SliceAllowlist.IsAllowed("src/Utils/Helpers/StringExtensions.cs", "login", shared, Templates));
        Assert.True(SliceAllowlist.IsAllowed("src/Utils/Format.cs", "login", shared, Templates));
        Assert.False(SliceAllowlist.IsAllowed("src/Other/File.cs", "login", shared, Templates));
    }
}