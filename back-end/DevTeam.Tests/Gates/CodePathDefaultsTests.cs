using DevTeam.Broker.Gates;

namespace DevTeam.Tests.Gates;

public class CodePathDefaultsTests
{
    [Fact]
    public void Defaults_AreThePlatformConvention()
    {
        Assert.Equal("back-end/**/Features/<F>", CodePathDefaults.DefaultBack);
        Assert.Equal("front-end/app/<F>", CodePathDefaults.DefaultFront);
    }
}
