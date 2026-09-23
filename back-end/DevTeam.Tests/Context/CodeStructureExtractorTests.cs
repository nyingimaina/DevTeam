using DevTeam.Broker.Context;

namespace DevTeam.Tests.Context;

public class CodeStructureExtractorTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void Build_ListsEntitiesRoutesAndTypes()
    {
        _workspace.Write("back-end/src/Core/Db.cs", """
            public class AppDbContext
            {
                public DbSet<ReleaseFeature> ReleaseFeatures { get; set; }
                public DbSet<FlowPosition> FlowPositions { get; set; }
            }
            """);
        _workspace.Write("back-end/src/Core/ReleaseFeature.cs", """
            public class ReleaseFeature
            {
                public FlowPosition Position { get; set; }
                public ICollection<FlowPosition> History { get; set; }
            }
            """);
        _workspace.Write("back-end/src/Api.cs", """
            app.MapPost("/api/features/{featureId}/run-gates", () => {});
            """);
        _workspace.Write("front-end/app/types.ts", "export interface FeatureDto { id: string }\n");

        var structure = CodeStructureExtractor.Build(_workspace.Path);

        Assert.Contains("ReleaseFeature", structure);
        Assert.Contains("FlowPosition", structure);
        Assert.Contains("POST /api/features/{featureId}/run-gates", structure);
        Assert.Contains("FeatureDto", structure);
    }

    [Fact]
    public void Build_SaysSoForAnUnsupportedStack()
    {
        _workspace.Write("README.md", "# just docs");
        _workspace.Write("main.py", "print('hello')");

        var structure = CodeStructureExtractor.Build(_workspace.Path);

        Assert.Contains("No automatic structure available", structure);
    }
}
