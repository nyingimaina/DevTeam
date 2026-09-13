using DevTeam.Broker.Git;
using DevTeam.Broker.Server;
using DevTeam.Broker.Workflow;

namespace DevTeam.Broker;

public static class ApiEndpoints
{
    public static void MapApi(this WebApplication app)
    {
        // ─── git endpoints ───────────────────────────────────────────────
        app.MapPost("/api/git/init", async (GitInitRequest request, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.WorkspacePath))
                return Results.BadRequest("WorkspacePath is required.");

            var git = ctx.RequestServices.GetRequiredService<IGitService>();
            var result = await git.InitAsync(request.WorkspacePath, ctx.RequestAborted);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result.Message);
        });

        app.MapGet("/api/git/status", async (string workspacePath, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(workspacePath))
                return Results.BadRequest("workspacePath is required.");

            var git = ctx.RequestServices.GetRequiredService<IGitService>();
            var result = await git.StatusAsync(workspacePath, ctx.RequestAborted);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result.Message);
        });

        app.MapPost("/api/git/branch", async (GitBranchRequest request, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.WorkspacePath))
                return Results.BadRequest("WorkspacePath is required.");
            if (string.IsNullOrWhiteSpace(request.BranchName))
                return Results.BadRequest("BranchName is required.");

            var git = ctx.RequestServices.GetRequiredService<IGitService>();
            var result = await git.EnsureBranchAsync(request.WorkspacePath, request.BranchName, ctx.RequestAborted);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result.Message);
        });

        app.MapPost("/api/git/commit", async (GitCommitRequest request, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.WorkspacePath))
                return Results.BadRequest("WorkspacePath is required.");
            if (string.IsNullOrWhiteSpace(request.Message))
                return Results.BadRequest("Message is required.");

            var git = ctx.RequestServices.GetRequiredService<IGitService>();
            var result = await git.CommitAsync(request.WorkspacePath, request.Message, ctx.RequestAborted);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result.Message);
        });

        app.MapPost("/api/git/merge", async (GitMergeRequest request, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.WorkspacePath))
                return Results.BadRequest("WorkspacePath is required.");
            if (string.IsNullOrWhiteSpace(request.SourceBranch))
                return Results.BadRequest("SourceBranch is required.");

            var git = ctx.RequestServices.GetRequiredService<IGitService>();
            var result = await git.MergeAsync(request.WorkspacePath, request.SourceBranch, request.TargetBranch ?? "develop", ctx.RequestAborted);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result.Message);
        });

        app.MapGet("/api/git/log", async (string workspacePath, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(workspacePath))
                return Results.BadRequest("workspacePath is required.");

            var git = ctx.RequestServices.GetRequiredService<IGitService>();
            var result = await git.LogAsync(workspacePath, ctx.RequestAborted);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result.Message);
        });

        // ─── stage endpoints ─────────────────────────────────────────────
        app.MapPost("/api/releases/{releaseId:guid}/start-stage", async (Guid releaseId, HttpContext ctx) =>
        {
            var engine = ctx.RequestServices.GetRequiredService<IWorkflowEngine>();
            try
            {
                var stageRun = await engine.StartStageAsync(releaseId, ctx.RequestAborted);
                return Results.Ok(stageRun);
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException ex) { return Results.BadRequest(ex.Message); }
        });

        app.MapPost("/api/releases/{releaseId:guid}/send-message", async (Guid releaseId, SendMessageRequest request, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.Text))
                return Results.BadRequest("Text is required.");

            var engine = ctx.RequestServices.GetRequiredService<IWorkflowEngine>();
            try
            {
                var result = await engine.SendMessageAsync(releaseId, request.Text, ctx.RequestAborted);
                return Results.Ok(result);
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException ex) { return Results.BadRequest(ex.Message); }
        });

        app.MapPost("/api/releases/{releaseId:guid}/run-gates", async (Guid releaseId, HttpContext ctx) =>
        {
            var engine = ctx.RequestServices.GetRequiredService<IWorkflowEngine>();
            try
            {
                var release = await engine.RunGatesAsync(releaseId, ctx.RequestAborted);
                return Results.Ok(release);
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException ex) { return Results.BadRequest(ex.Message); }
        });

        // ─── release endpoints ────────────────────────────────────────────
        app.MapPost("/api/releases", async (CreateReleaseRequest request, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.FeatureKey))
                return Results.BadRequest("FeatureKey is required.");
            if (string.IsNullOrWhiteSpace(request.WorkspacePath))
                return Results.BadRequest("WorkspacePath is required.");

            var engine = ctx.RequestServices.GetRequiredService<IWorkflowEngine>();
            var release = await engine.StartReleaseAsync(request.FeatureKey, request.WorkspacePath, ctx.RequestAborted);
            return Results.Created($"/api/releases/{release.Id}", release);
        });

        app.MapGet("/api/releases", async (HttpContext ctx) =>
        {
            var engine = ctx.RequestServices.GetRequiredService<IWorkflowEngine>();
            var releases = await engine.ListReleasesAsync(ctx.RequestAborted);
            return Results.Ok(releases);
        });

        app.MapGet("/api/releases/{releaseId:guid}", async (Guid releaseId, HttpContext ctx) =>
        {
            var engine = ctx.RequestServices.GetRequiredService<IWorkflowEngine>();
            try
            {
                var release = await engine.GetReleaseAsync(releaseId, ctx.RequestAborted);
                return Results.Ok(release);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound($"Release {releaseId} not found.");
            }
        });

        app.MapPost("/api/releases/{releaseId:guid}/advance", async (Guid releaseId, HttpContext ctx) =>
        {
            var engine = ctx.RequestServices.GetRequiredService<IWorkflowEngine>();
            try
            {
                var release = await engine.AdvanceAsync(releaseId, ctx.RequestAborted);
                return Results.Ok(release);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound($"Release {releaseId} not found.");
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        app.MapPost("/api/releases/{releaseId:guid}/signoff", async (Guid releaseId, SignoffRequest request, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.StageName))
                return Results.BadRequest("StageName is required.");
            if (string.IsNullOrWhiteSpace(request.Role))
                return Results.BadRequest("Role is required.");

            var engine = ctx.RequestServices.GetRequiredService<IWorkflowEngine>();
            try
            {
                var release = await engine.SignoffAsync(releaseId, request.StageName, request.Role, request.Comment, ctx.RequestAborted);
                return Results.Ok(release);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound($"Release {releaseId} not found.");
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        app.MapGet("/healthz", (HttpContext ctx) =>
        {
            var appInfo = ctx.RequestServices.GetRequiredService<IAppInfo>();
            return Results.Ok(new HealthResponse("ok", appInfo.Version));
        });

        app.MapGet("/api/info", async (HttpContext ctx) =>
        {
            var coordinator = ctx.RequestServices.GetRequiredService<BrokerCoordinator>();
            return Results.Ok(await coordinator.GetAgentInfoAsync(ctx.RequestAborted));
        });

        app.MapGet("/api/sessions", async (HttpContext ctx) =>
        {
            var coordinator = ctx.RequestServices.GetRequiredService<BrokerCoordinator>();
            return Results.Ok(await coordinator.ListSessionsAsync(ctx.RequestAborted));
        });

        app.MapPost("/api/sessions", async (NewSessionRequest request, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.WorkspacePath))
                return Results.BadRequest("WorkspacePath is required.");

            var coordinator = ctx.RequestServices.GetRequiredService<BrokerCoordinator>();
            var session = await coordinator.NewSessionAsync(
                request.WorkspacePath, request.ModelId, ctx.RequestAborted);
            return Results.Created($"/api/sessions/{session.SessionId}", session);
        });

        app.MapGet("/api/sessions/{sessionId:guid}", async (Guid sessionId, HttpContext ctx) =>
        {
            var coordinator = ctx.RequestServices.GetRequiredService<BrokerCoordinator>();
            var detail = await coordinator.GetSessionDetailAsync(sessionId, ctx.RequestAborted);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        app.MapDelete("/api/sessions/{sessionId:guid}", async (Guid sessionId, HttpContext ctx) =>
        {
            var coordinator = ctx.RequestServices.GetRequiredService<BrokerCoordinator>();
            var deleted = await coordinator.DeleteSessionAsync(sessionId, ctx.RequestAborted);
            return deleted ? Results.NoContent() : Results.NotFound();
        });

        app.MapPost("/api/sessions/{sessionId:guid}/prompt", async (Guid sessionId, PromptRequest request, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.Text))
                return Results.BadRequest("Text is required.");

            var coordinator = ctx.RequestServices.GetRequiredService<BrokerCoordinator>();
            try
            {
                var result = await coordinator.PromptWithSessionRecoveryAsync(sessionId, request.Text, ctx.RequestAborted);
                return Results.Ok(result);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound($"Session {sessionId} not found.");
            }
        });

        app.MapPost("/api/sessions/{sessionId:guid}/model", async (Guid sessionId, SetModelRequest request, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.ModelId))
                return Results.BadRequest("ModelId is required.");

            var coordinator = ctx.RequestServices.GetRequiredService<BrokerCoordinator>();
            try
            {
                var modelId = await coordinator.SetModelAsync(sessionId, request.ModelId, ctx.RequestAborted);
                return Results.Ok(new { modelId });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound($"Session {sessionId} not found.");
            }
        });

        app.MapPost("/api/sessions/{sessionId:guid}/mode", async (Guid sessionId, SetModeRequest request, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.ModeId))
                return Results.BadRequest("ModeId is required.");

            var coordinator = ctx.RequestServices.GetRequiredService<BrokerCoordinator>();
            try
            {
                var modeId = await coordinator.SetModeAsync(sessionId, request.ModeId, ctx.RequestAborted);
                return Results.Ok(new { modeId });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound($"Session {sessionId} not found.");
            }
        });

        // ─── filesystem browser (PathBrowser) ─────────────────────────────────
        app.MapGet("/api/fs/roots", (HttpContext ctx) =>
        {
            var fs = ctx.RequestServices.GetRequiredService<IFileSystemService>();
            return Results.Ok(fs.GetRoots());
        });

        app.MapGet("/api/fs/list", (string? path, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest("Path is required.");

            var fs = ctx.RequestServices.GetRequiredService<IFileSystemService>();
            try
            {
                return Results.Ok(fs.ListDirectory(path));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        app.MapGet("/api/fs/stat", (string? path, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest("Path is required.");

            var fs = ctx.RequestServices.GetRequiredService<IFileSystemService>();
            try
            {
                return Results.Ok(fs.GetStat(path));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        app.MapPost("/api/fs/mkdir", (CreateDirectoryRequest request, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.Path))
                return Results.BadRequest("Path is required.");

            var fs = ctx.RequestServices.GetRequiredService<IFileSystemService>();
            try
            {
                return Results.Ok(fs.CreateDirectory(request.Path));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(ex.Message);
            }
        });
    }
}