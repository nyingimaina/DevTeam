using DevTeam.Broker.Domain;
using DevTeam.Broker.Git;
using DevTeam.Broker.Server;
using DevTeam.Broker.Workflow;
using Microsoft.EntityFrameworkCore;

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

        // ─── git remote management ──────────────────────────────────────
        app.MapGet("/api/git/remote", async (string workspacePath, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(workspacePath))
                return Results.BadRequest("workspacePath is required.");

            var git = ctx.RequestServices.GetRequiredService<IGitService>();
            var db = ctx.RequestServices.GetRequiredService<IDbContextFactory<DevTeamDbContext>>();
            var result = await git.GetRemoteAsync(workspacePath, ctx.RequestAborted);
            if (!result.Success) return Results.BadRequest(result.Message);

            await using var context = await db.CreateDbContextAsync(ctx.RequestAborted);
            var settings = await context.WorkspaceGitSettings.FindAsync([workspacePath], ctx.RequestAborted);
            return Results.Ok(new GitRemoteResponse(result.RemoteUrl, settings?.CredentialName));
        });

        app.MapPost("/api/git/remote", async (SetGitRemoteRequest request, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.WorkspacePath))
                return Results.BadRequest("WorkspacePath is required.");
            if (string.IsNullOrWhiteSpace(request.Url))
                return Results.BadRequest("Url is required.");

            var git = ctx.RequestServices.GetRequiredService<IGitService>();
            var result = await git.SetRemoteAsync(request.WorkspacePath, request.Url, ctx.RequestAborted);
            if (!result.Success) return Results.BadRequest(result.Message);

            var db = ctx.RequestServices.GetRequiredService<IDbContextFactory<DevTeamDbContext>>();
            await using var context = await db.CreateDbContextAsync(ctx.RequestAborted);
            var settings = await context.WorkspaceGitSettings.FindAsync([request.WorkspacePath], ctx.RequestAborted);
            if (settings is null)
            {
                settings = new WorkspaceGitSettings { WorkspacePath = request.WorkspacePath };
                context.WorkspaceGitSettings.Add(settings);
            }
            settings.CredentialName = request.CredentialName;
            await context.SaveChangesAsync(ctx.RequestAborted);

            return Results.Ok(new GitRemoteResponse(result.RemoteUrl, settings.CredentialName));
        });

        app.MapPost("/api/git/credential", (SetGitCredentialRequest request, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest("Name is required.");
            if (string.IsNullOrWhiteSpace(request.Token))
                return Results.BadRequest("Token is required.");

            var store = ctx.RequestServices.GetRequiredService<IGitCredentialStore>();
            store.SetToken(request.Name, request.Token);
            return Results.Ok(new { ok = true });
        });

        app.MapGet("/api/git/credentials", (HttpContext ctx) =>
        {
            var store = ctx.RequestServices.GetRequiredService<IGitCredentialStore>();
            return Results.Ok(new GitCredentialsResponse(store.ListNames()));
        });

        // ─── profiles (named, reusable per-stage priming prompts) ──────────

        static ProfileDto ToProfileDto(Profile p) => new(
            p.Id, p.Name, p.Description, p.IsDefault,
            [.. p.Prompts.Select(pr => new ProfilePromptDto(pr.StageName, pr.PromptText, pr.OverridesBuiltInPrompt))]);

        app.MapGet("/api/profiles", async (HttpContext ctx) =>
        {
            var db = ctx.RequestServices.GetRequiredService<IDbContextFactory<DevTeamDbContext>>();
            await using var context = await db.CreateDbContextAsync(ctx.RequestAborted);
            var profiles = await context.Profiles.Include(p => p.Prompts)
                .OrderBy(p => p.Name)
                .ToListAsync(ctx.RequestAborted);
            return Results.Ok(profiles.Select(ToProfileDto).ToArray());
        });

        app.MapPost("/api/profiles", async (CreateProfileRequest request, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest("Name is required.");

            var db = ctx.RequestServices.GetRequiredService<IDbContextFactory<DevTeamDbContext>>();
            await using var context = await db.CreateDbContextAsync(ctx.RequestAborted);
            var isFirst = !await context.Profiles.AnyAsync(ctx.RequestAborted);

            var profile = new Profile { Name = request.Name, Description = request.Description, IsDefault = isFirst };
            foreach (var stageName in ProfileStageNames.All)
                profile.Prompts.Add(new ProfilePrompt { StageName = stageName, PromptText = "" });

            context.Profiles.Add(profile);
            await context.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(ToProfileDto(profile));
        });

        app.MapPut("/api/profiles/{id:guid}", async (Guid id, UpdateProfileRequest request, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest("Name is required.");

            var db = ctx.RequestServices.GetRequiredService<IDbContextFactory<DevTeamDbContext>>();
            await using var context = await db.CreateDbContextAsync(ctx.RequestAborted);
            var profile = await context.Profiles.Include(p => p.Prompts).FirstOrDefaultAsync(p => p.Id == id, ctx.RequestAborted);
            if (profile is null) return Results.NotFound();

            profile.Name = request.Name;
            profile.Description = request.Description;
            foreach (var promptUpdate in request.Prompts)
            {
                var existing = profile.Prompts.FirstOrDefault(p => p.StageName == promptUpdate.StageName);
                if (existing is null) continue;
                existing.PromptText = promptUpdate.PromptText;
                existing.OverridesBuiltInPrompt = promptUpdate.OverridesBuiltInPrompt;
            }
            await context.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(ToProfileDto(profile));
        });

        app.MapDelete("/api/profiles/{id:guid}", async (Guid id, HttpContext ctx) =>
        {
            var db = ctx.RequestServices.GetRequiredService<IDbContextFactory<DevTeamDbContext>>();
            await using var context = await db.CreateDbContextAsync(ctx.RequestAborted);
            var profile = await context.Profiles.Include(p => p.Prompts).FirstOrDefaultAsync(p => p.Id == id, ctx.RequestAborted);
            if (profile is null) return Results.NotFound();

            var wasDefault = profile.IsDefault;
            context.Profiles.Remove(profile);
            await context.SaveChangesAsync(ctx.RequestAborted);

            if (wasDefault)
            {
                // SQLite's EF provider can't translate ORDER BY on DateTimeOffset — sort client-side.
                var remaining = await context.Profiles.ToListAsync(ctx.RequestAborted);
                var promoted = remaining.OrderBy(p => p.CreatedAt).FirstOrDefault();
                if (promoted is not null)
                {
                    promoted.IsDefault = true;
                    await context.SaveChangesAsync(ctx.RequestAborted);
                }
            }
            return Results.Ok(new { ok = true });
        });

        app.MapPost("/api/profiles/{id:guid}/default", async (Guid id, HttpContext ctx) =>
        {
            var db = ctx.RequestServices.GetRequiredService<IDbContextFactory<DevTeamDbContext>>();
            await using var context = await db.CreateDbContextAsync(ctx.RequestAborted);
            var target = await context.Profiles.Include(p => p.Prompts).FirstOrDefaultAsync(p => p.Id == id, ctx.RequestAborted);
            if (target is null) return Results.NotFound();

            var currentDefaults = await context.Profiles.Where(p => p.IsDefault && p.Id != id).ToListAsync(ctx.RequestAborted);
            foreach (var p in currentDefaults) p.IsDefault = false;
            target.IsDefault = true;
            await context.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(ToProfileDto(target));
        });

        app.MapGet("/api/workspace/profile", async (string workspacePath, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(workspacePath))
                return Results.BadRequest("workspacePath is required.");

            var db = ctx.RequestServices.GetRequiredService<IDbContextFactory<DevTeamDbContext>>();
            await using var context = await db.CreateDbContextAsync(ctx.RequestAborted);
            var settings = await context.WorkspaceProfileSettings.FindAsync([workspacePath], ctx.RequestAborted);
            if (settings is not null) return Results.Ok(new WorkspaceProfileDto(settings.ProfileId));

            var defaultProfile = await context.Profiles.FirstOrDefaultAsync(p => p.IsDefault, ctx.RequestAborted);
            if (defaultProfile is null) return Results.Ok(new WorkspaceProfileDto(null));

            context.WorkspaceProfileSettings.Add(new WorkspaceProfileSettings { WorkspacePath = workspacePath, ProfileId = defaultProfile.Id });
            await context.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(new WorkspaceProfileDto(defaultProfile.Id));
        });

        app.MapPost("/api/workspace/profile", async (SetWorkspaceProfileRequest request, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.WorkspacePath))
                return Results.BadRequest("WorkspacePath is required.");

            var db = ctx.RequestServices.GetRequiredService<IDbContextFactory<DevTeamDbContext>>();
            await using var context = await db.CreateDbContextAsync(ctx.RequestAborted);
            if (!await context.Profiles.AnyAsync(p => p.Id == request.ProfileId, ctx.RequestAborted))
                return Results.BadRequest("Unknown profile.");

            var settings = await context.WorkspaceProfileSettings.FindAsync([request.WorkspacePath], ctx.RequestAborted);
            if (settings is null)
            {
                settings = new WorkspaceProfileSettings { WorkspacePath = request.WorkspacePath };
                context.WorkspaceProfileSettings.Add(settings);
            }
            settings.ProfileId = request.ProfileId;
            await context.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(new WorkspaceProfileDto(settings.ProfileId));
        });

        // ─── feature-scoped pipeline endpoints (GitFlow) ───────────────────
        app.MapPost("/api/features/{featureId:guid}/start-stage", async (Guid featureId, HttpContext ctx) =>
        {
            var engine = ctx.RequestServices.GetRequiredService<IWorkflowEngine>();
            try
            {
                var stageRun = await engine.StartStageAsync(featureId, ctx.RequestAborted);
                return Results.Ok(stageRun);
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException ex) { return Results.BadRequest(ex.Message); }
            catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
            {
                return Results.Conflict("The agent turn was cancelled.");
            }
        });

        app.MapPost("/api/features/{featureId:guid}/send-message", async (Guid featureId, SendMessageRequest request, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.Text))
                return Results.BadRequest("Text is required.");

            var engine = ctx.RequestServices.GetRequiredService<IWorkflowEngine>();
            try
            {
                var result = await engine.SendMessageEnforcingSingleQuestionAsync(featureId, request.Text, ctx.RequestAborted);
                return Results.Ok(result);
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException ex) { return Results.BadRequest(ex.Message); }
            catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
            {
                return Results.Conflict("The agent turn was cancelled.");
            }
        });

        app.MapPost("/api/features/{featureId:guid}/run-gates", async (Guid featureId, HttpContext ctx) =>
        {
            var engine = ctx.RequestServices.GetRequiredService<IWorkflowEngine>();
            try
            {
                var release = await engine.RunGatesAsync(featureId, ctx.RequestAborted);
                return Results.Ok(release);
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException ex) { return Results.BadRequest(ex.Message); }
        });

        app.MapPost("/api/features/{featureId:guid}/run-stage", async (Guid featureId, HttpContext ctx) =>
        {
            var engine = ctx.RequestServices.GetRequiredService<IWorkflowEngine>();
            try
            {
                var release = await engine.RunStageAsync(featureId, ctx.RequestAborted);
                return Results.Ok(release);
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException ex) { return Results.BadRequest(ex.Message); }
        });

        app.MapPost("/api/features/{featureId:guid}/push-back", async (Guid featureId, PushBackRequest request, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.TargetStageName))
                return Results.BadRequest("TargetStageName is required.");

            var engine = ctx.RequestServices.GetRequiredService<IWorkflowEngine>();
            try
            {
                var release = await engine.PushBackAsync(
                    featureId, request.TargetStageName, request.Instructions, ctx.RequestAborted);
                return Results.Ok(release);
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException ex) { return Results.BadRequest(ex.Message); }
        });

        app.MapGet("/api/features/{featureId:guid}/pipeline", async (Guid featureId, HttpContext ctx) =>
        {
            var engine = ctx.RequestServices.GetRequiredService<IWorkflowEngine>();
            try
            {
                var pipeline = await engine.GetPipelineAsync(featureId, ctx.RequestAborted);
                return Results.Ok(pipeline);
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });

        app.MapGet("/api/features/{featureId:guid}/stages/{stageRunId:guid}/messages", async (Guid featureId, Guid stageRunId, HttpContext ctx) =>
        {
            var engine = ctx.RequestServices.GetRequiredService<IWorkflowEngine>();
            try
            {
                var messages = await engine.GetStageMessagesAsync(featureId, stageRunId, ctx.RequestAborted);
                return Results.Ok(messages);
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });

        app.MapGet("/api/features/{featureId:guid}/stages/{stageRunId:guid}/artifacts", async (Guid featureId, Guid stageRunId, HttpContext ctx) =>
        {
            var engine = ctx.RequestServices.GetRequiredService<IWorkflowEngine>();
            try
            {
                var artifacts = await engine.GetStageArtifactsAsync(featureId, stageRunId, ctx.RequestAborted);
                return Results.Ok(artifacts);
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException ex) { return Results.BadRequest(ex.Message); }
        });

        app.MapGet("/api/features/{featureId:guid}/workspace-changes", async (Guid featureId, HttpContext ctx) =>
        {
            var engine = ctx.RequestServices.GetRequiredService<IWorkflowEngine>();
            try
            {
                var changes = await engine.GetWorkspaceChangesAsync(featureId, ctx.RequestAborted);
                return Results.Ok(changes);
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });

        app.MapPost("/api/features/{featureId:guid}/advance", async (Guid featureId, HttpContext ctx) =>
        {
            var engine = ctx.RequestServices.GetRequiredService<IWorkflowEngine>();
            try
            {
                var release = await engine.AdvanceAsync(featureId, ctx.RequestAborted);
                return Results.Ok(release);
            }
            catch (KeyNotFoundException) { return Results.NotFound($"Feature {featureId} not found."); }
            catch (InvalidOperationException ex) { return Results.BadRequest(ex.Message); }
        });

        app.MapPost("/api/features/{featureId:guid}/signoff", async (Guid featureId, SignoffRequest request, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.StageName))
                return Results.BadRequest("StageName is required.");
            if (string.IsNullOrWhiteSpace(request.Role))
                return Results.BadRequest("Role is required.");

            var engine = ctx.RequestServices.GetRequiredService<IWorkflowEngine>();
            try
            {
                var release = await engine.SignoffAsync(featureId, request.StageName, request.Role, request.Comment, ctx.RequestAborted);
                return Results.Ok(release);
            }
            catch (KeyNotFoundException) { return Results.NotFound($"Feature {featureId} not found."); }
            catch (InvalidOperationException ex) { return Results.BadRequest(ex.Message); }
        });

        app.MapGet("/api/releases/{releaseId:guid}/models", async (Guid releaseId, HttpContext ctx) =>
        {
            var engine = ctx.RequestServices.GetRequiredService<IWorkflowEngine>();
            try
            {
                var models = await engine.GetAvailableModelsAsync(releaseId, ctx.RequestAborted);
                return Results.Ok(models);
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });

        app.MapPost("/api/releases/{releaseId:guid}/features", async (Guid releaseId, CreateFeatureRequest request, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.FeatureKey))
                return Results.BadRequest("FeatureKey is required.");

            var engine = ctx.RequestServices.GetRequiredService<IWorkflowEngine>();
            try
            {
                var feature = await engine.CreateFeatureAsync(releaseId, request.FeatureKey, ctx.RequestAborted);
                return Results.Created($"/api/features/{feature.Id}", feature);
            }
            catch (KeyNotFoundException) { return Results.NotFound($"Release {releaseId} not found."); }
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

        app.MapGet("/api/releases", async (string? workspacePath, HttpContext ctx) =>
        {
            var engine = ctx.RequestServices.GetRequiredService<IWorkflowEngine>();
            var releases = await engine.ListReleasesAsync(workspacePath, ctx.RequestAborted);
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

        // GET (read the current model/mode) plus /model and /mode themselves are the only
        // session routes left — ReleaseWizard's stage-header ModelPicker uses all three for
        // the session opened by start-stage.
        app.MapGet("/api/sessions/{sessionId:guid}", async (Guid sessionId, HttpContext ctx) =>
        {
            var coordinator = ctx.RequestServices.GetRequiredService<BrokerCoordinator>();
            var detail = await coordinator.GetSessionDetailAsync(sessionId, ctx.RequestAborted);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
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

        app.MapPost("/api/fs/reveal", (RevealInExplorerRequest request, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.Path))
                return Results.BadRequest("Path is required.");

            var fs = ctx.RequestServices.GetRequiredService<IFileSystemService>();
            try
            {
                fs.RevealInExplorer(request.Path);
                return Results.NoContent();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        app.MapPost("/api/fs/cleanup", (CleanupWorkspaceRequest request, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.WorkspacePath) || !Path.IsPathRooted(request.WorkspacePath))
                return Results.BadRequest("An absolute WorkspacePath is required.");

            var cleanup = ctx.RequestServices.GetRequiredService<IWorkspaceProcessCleanupService>();
            return Results.Ok(cleanup.CleanupWorkspace(request.WorkspacePath));
        });

        // ─── active turn (the one agent turn the broker can run at a time) ────
        app.MapGet("/api/turns/current", (HttpContext ctx) =>
        {
            var coordinator = ctx.RequestServices.GetRequiredService<BrokerCoordinator>();
            var turn = coordinator.GetCurrentTurn();
            return turn is null ? Results.NoContent() : Results.Ok(turn);
        });

        app.MapPost("/api/turns/current/cancel", async (HttpContext ctx) =>
        {
            var coordinator = ctx.RequestServices.GetRequiredService<BrokerCoordinator>();
            var cancelled = await coordinator.CancelCurrentTurnAsync(ctx.RequestAborted);
            return cancelled ? Results.Ok() : Results.NotFound();
        });
    }
}