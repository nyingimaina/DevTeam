using DevTeam.Broker.Server;
using Xunit;

namespace DevTeam.Tests;

public class PathContainmentTests
{
    [Fact]
    public void IsWithinWorkspace_ExactMatch_ReturnsTrue()
    {
        Assert.True(PathContainment.IsWithinWorkspace(@"C:\work\proj", @"C:\work\proj"));
    }

    [Fact]
    public void IsWithinWorkspace_ExactMatchWithTrailingSeparator_ReturnsTrue()
    {
        Assert.True(PathContainment.IsWithinWorkspace(@"C:\work\proj", @"C:\work\proj\"));
    }

    [Fact]
    public void IsWithinWorkspace_NestedSubpath_ReturnsTrue()
    {
        Assert.True(PathContainment.IsWithinWorkspace(@"C:\work\proj", @"C:\work\proj\src\App.tsx"));
    }

    [Fact]
    public void IsWithinWorkspace_SiblingDirectoryWithSamePrefix_ReturnsFalse()
    {
        // Guards against a naive StartsWith("C:\work\proj") matching "C:\work\proj2".
        Assert.False(PathContainment.IsWithinWorkspace(@"C:\work\proj", @"C:\work\proj2\secrets.txt"));
    }

    [Fact]
    public void IsWithinWorkspace_ShallowerSiblingDirectory_ReturnsFalse()
    {
        Assert.False(PathContainment.IsWithinWorkspace(@"C:\work\proj", @"C:\work\other\file.txt"));
    }

    [Fact]
    public void IsWithinWorkspace_CaseInsensitive_ReturnsTrue()
    {
        Assert.True(PathContainment.IsWithinWorkspace(@"C:\Work\Proj", @"c:\work\proj\src\app.tsx"));
    }

    [Fact]
    public void IsWithinWorkspace_PathTraversalOutsideWorkspace_ReturnsFalse()
    {
        Assert.False(PathContainment.IsWithinWorkspace(@"C:\work\proj", @"C:\work\proj\..\..\secrets.txt"));
    }

    [Fact]
    public void IsWithinWorkspace_NullOrEmptyCandidate_ReturnsFalse()
    {
        Assert.False(PathContainment.IsWithinWorkspace(@"C:\work\proj", null));
        Assert.False(PathContainment.IsWithinWorkspace(@"C:\work\proj", ""));
    }
}
