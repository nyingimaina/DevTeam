using DevTeam.Broker.Context;
using DevTeam.Broker.Domain;
using DevTeam.Broker.Diagnostics;
using DevTeam.Broker.Metrics;
using DevTeam.Broker.Gates.Readiness;
using DevTeam.Broker.Git;
using DevTeam.Broker.Models;
using DevTeam.Broker.Notifications;
using DevTeam.Broker.SemaNami;
using DevTeam.Broker.Server;
using DevTeam.Broker.Workflow;
using DevTeam.Shared;
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

            var target = request.TargetBranch ?? "develop";
            if (ProtectedBranches.Contains(target))
            {
                // Protected branches are only mergeable by a branch that passed the strict
                // checks and hasn't moved since — this is what makes the gate unbypassable
                // rather than advisory.
                var gate = ctx.RequestServices.GetRequiredService<IShipReadinessGate>();
                if (!await gate.HasValidAttestationAsync(request.WorkspacePath, request.SourceBranch, ctx.RequestAborted))
                    return Results.Conflict(
                        $"'{ProtectedBranches.Display}' is protected: '{request.SourceBranch}' must pass the final checks " +
                        "(with no new commits afterwards) before it can be merged.");
            }

            var git = ctx.RequestServices.GetRequiredService<IGitService>();
            var result = await git.MergeAsync(request.WorkspacePath, request.SourceBranch, target, ctx.RequestAborted);
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

            // No stage names are pre-seeded here — a profile is reusable across workspaces
            // with different (possibly custom) pipelines, so there's no single fixed list to
            // seed from. A prompt row is created on demand when a stage's prompt is saved
            // (see the PUT handler below); ResolveActiveProfilePromptAsync already treats a
            // missing row the same as an empty prompt, so this is safe either way.
            var profile = new Profile { Name = request.Name, Description = request.Description, IsDefault = isFirst };

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
                if (existing is null)
                {
                    // Explicit DbSet.Add is required, not just profile.Prompts.Add: ProfilePrompt.Id
                    // is already a non-default Guid the moment it's constructed (client-side
                    // default), so EF's navigation-fixup alone treats it as Unchanged rather than
                    // Added, and SaveChanges then issues a no-op UPDATE instead of an INSERT.
                    existing = new ProfilePrompt { ProfileId = profile.Id, Profile = profile, StageName = promptUpdate.StageName };
                    profile.Prompts.Add(existing);
                    context.ProfilePrompts.Add(existing);
                }
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

        // ─── specialists (on-demand delegates any stage can consult — Part 3) ──

        static SpecialistRoleDto ToSpecialistDto(SpecialistRole s) => new(s.Id, s.Name, s.Description, s.PrimingPrompt, s.WritesCode);

        app.MapGet("/api/specialists", async (HttpContext ctx) =>
        {
            var db = ctx.RequestServices.GetRequiredService<IDbContextFactory<DevTeamDbContext>>();
            await using var context = await db.CreateDbContextAsync(ctx.RequestAborted);
            var specialists = await context.SpecialistRoles.OrderBy(s => s.Name).ToListAsync(ctx.RequestAborted);
            return Results.Ok(specialists.Select(ToSpecialistDto).ToArray());
        });

        app.MapPost("/api/specialists", async (SaveSpecialistRoleRequest request, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest("Name is required.");

            var db = ctx.RequestServices.GetRequiredService<IDbContextFactory<DevTeamDbContext>>();
            await using var context = await db.CreateDbContextAsync(ctx.RequestAborted);
            if (await context.SpecialistRoles.AnyAsync(s => s.Name == request.Name, ctx.RequestAborted))
                return Results.BadRequest($"A specialist named '{request.Name}' already exists.");

            var specialist = new SpecialistRole
            {
                Name = request.Name,
                Description = request.Description,
                PrimingPrompt = request.PrimingPrompt,
                WritesCode = request.WritesCode,
            };
            context.SpecialistRoles.Add(specialist);
            await context.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(ToSpecialistDto(specialist));
        });

        app.MapPut("/api/specialists/{id:guid}", async (Guid id, SaveSpecialistRoleRequest request, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest("Name is required.");

            var db = ctx.RequestServices.GetRequiredService<IDbContextFactory<DevTeamDbContext>>();
            await using var context = await db.CreateDbContextAsync(ctx.RequestAborted);
            var specialist = await context.SpecialistRoles.FirstOrDefaultAsync(s => s.Id == id, ctx.RequestAborted);
            if (specialist is null) return Results.NotFound();

            if (await context.SpecialistRoles.AnyAsync(s => s.Name == request.Name && s.Id != id, ctx.RequestAborted))
                return Results.BadRequest($"A specialist named '{request.Name}' already exists.");

            specialist.Name = request.Name;
            specialist.Description = request.Description;
            specialist.PrimingPrompt = request.PrimingPrompt;
            specialist.WritesCode = request.WritesCode;
            await context.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(ToSpecialistDto(specialist));
        });

        app.MapDelete("/api/specialists/{id:guid}", async (Guid id, HttpContext ctx) =>
        {
            var db = ctx.RequestServices.GetRequiredService<IDbContextFactory<DevTeamDbContext>>();
            await using var context = await db.CreateDbContextAsync(ctx.RequestAborted);
            var specialist = await context.SpecialistRoles.FirstOrDefaultAsync(s => s.Id == id, ctx.RequestAborted);
            if (specialist is null) return Results.NotFound();

            context.SpecialistRoles.Remove(specialist);
            await context.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(new { ok = true });
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

        // ─── pipeline authoring (Part 2C) ───────────────────────────────────

        app.MapGet("/api/workspace/pipeline", (string workspacePath, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(workspacePath))
                return Results.BadRequest("workspacePath is required.");

            var editor = ctx.RequestServices.GetRequiredService<PipelineEditorService>();
            return Results.Ok(editor.Load(workspacePath));
        });

        app.MapPut("/api/workspace/pipeline", (SavePipelineEditorRequest request, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.WorkspacePath))
                return Results.BadRequest("WorkspacePath is required.");
            if (request.Roles.Count == 0)
                return Results.BadRequest("A pipeline must have at least one role.");

            var editor = ctx.RequestServices.GetRequiredService<PipelineEditorService>();
            try
            {
                editor.Save(request.WorkspacePath, request.Roles);
            }
            catch (WorkflowConfigurationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
            return Results.Ok(editor.Load(request.WorkspacePath));
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

        // Like run-gates, but a failed check isn't a dead end: the agent is asked to fix it and the
        // checks re-run before the problem is handed back. Returns an outcome envelope.
        app.MapPost("/api/features/{featureId:guid}/run-gates-and-repair", async (Guid featureId, HttpContext ctx) =>
        {
            var engine = ctx.RequestServices.GetRequiredService<IWorkflowEngine>();
            try
            {
                var result = await new GateSelfHealer(engine).RunAsync(featureId, ctx.RequestAborted);
                return Results.Ok(new
                {
                    release = result.Release,
                    outcome = result.Outcome,
                    autoFixAttempts = result.AutoFixAttempts,
                    problems = result.Problems,
                });
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException ex) { return Results.BadRequest(ex.Message); }
        });

        // Recovery from a provider refusal: pick another model, prove it answers, and only then
        // clear the stage's failure. A model that can't be used is a normal 200 { ok: false } so
        // the UI can keep its pane open and say so; only bad input / unknown feature are errors.
        app.MapPost("/api/features/{featureId:guid}/switch-model", async (Guid featureId, SetModelRequest request, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.ModelId))
                return Results.BadRequest("ModelId is required.");

            var switcher = ctx.RequestServices.GetRequiredService<StageModelSwitcher>();
            try
            {
                return Results.Ok(await switcher.SwitchAndVerifyAsync(featureId, request.ModelId, ctx.RequestAborted));
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

        // Unlike push-back (only allowed to an earlier stage), this can re-open the CURRENT
        // stage — the escape hatch when a stage's live attempt is stuck (e.g. its agent session
        // went stale after a broker restart) but no gate has failed, so there is nothing to push
        // back from.
        app.MapPost("/api/features/{featureId:guid}/retry-stage", async (Guid featureId, RetryStageRequest request, HttpContext ctx) =>
        {
            var engine = ctx.RequestServices.GetRequiredService<IWorkflowEngine>();
            try
            {
                var release = await engine.RetryStageAsync(featureId, request.TargetStageName, ctx.RequestAborted);
                return Results.Ok(release);
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (InvalidOperationException ex) { return Results.BadRequest(ex.Message); }
        });

        // Escape hatch for a feature whose pipeline finished but whose completion never got
        // recorded — e.g. the merge+branch-delete succeeded but saving feature.Status = Complete
        // failed afterward. StartStageAsync/RunStageAsync both refuse once the flow position is
        // past the last stage, so this is the only way to resume finalizing in that state.
        app.MapPost("/api/features/{featureId:guid}/retry-finalize", async (Guid featureId, HttpContext ctx) =>
        {
            var engine = ctx.RequestServices.GetRequiredService<IWorkflowEngine>();
            try
            {
                var release = await engine.RetryFeatureFinalizationAsync(featureId, ctx.RequestAborted);
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

        app.MapPost("/api/features/{featureId:guid}/switch-to", async (Guid featureId, HttpContext ctx) =>
        {
            var engine = ctx.RequestServices.GetRequiredService<IWorkflowEngine>();
            try
            {
                var release = await engine.SwitchFeatureAsync(featureId, ctx.RequestAborted);
                return Results.Ok(release);
            }
            catch (KeyNotFoundException) { return Results.NotFound($"Feature {featureId} not found."); }
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

        // Persisted "Continue automatically" (CruiseControl, front-end) — a release-level flag,
        // not per-feature, so it survives a reload instead of resetting to a manual click.
        app.MapPut("/api/releases/{releaseId:guid}/autonomous", async (Guid releaseId, SetAutonomousEnabledRequest request, HttpContext ctx) =>
        {
            var engine = ctx.RequestServices.GetRequiredService<IWorkflowEngine>();
            try
            {
                var release = await engine.SetReleaseAutonomousEnabledAsync(releaseId, request.Enabled, ctx.RequestAborted);
                return Results.Ok(release);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound($"Release {releaseId} not found.");
            }
        });

        app.MapPost("/api/releases/{releaseId:guid}/finalize", async (Guid releaseId, HttpContext ctx) =>
        {
            var engine = ctx.RequestServices.GetRequiredService<IWorkflowEngine>();
            var gate = ctx.RequestServices.GetRequiredService<IShipReadinessGate>();
            try
            {
                // Hard ship gate: the strict checks must pass — pinned to this exact commit —
                // before the release branch may be merged into a protected branch.
                var report = await gate.CheckReleaseAsync(releaseId, ctx.RequestAborted);
                if (!report.Passed)
                    return Results.Conflict(report.BlockerSummary);

                var release = await engine.FinalizeReleaseAsync(releaseId, ctx.RequestAborted);
                return Results.Ok(release);
            }
            catch (KeyNotFoundException) { return Results.NotFound($"Release {releaseId} not found."); }
            catch (InvalidOperationException ex) { return Results.BadRequest(ex.Message); }
        });

        // Runs the strict checks for a release without shipping it — lets the user see the
        // result (and the Checks library populate) before they commit to finalizing.
        app.MapPost("/api/releases/{releaseId:guid}/readiness", async (Guid releaseId, HttpContext ctx) =>
        {
            var gate = ctx.RequestServices.GetRequiredService<IShipReadinessGate>();
            try
            {
                return Results.Ok(await gate.CheckReleaseAsync(releaseId, ctx.RequestAborted));
            }
            catch (KeyNotFoundException) { return Results.NotFound($"Release {releaseId} not found."); }
        });

        app.MapGet("/api/releases/{releaseId:guid}/readiness", async (Guid releaseId, HttpContext ctx) =>
        {
            var gate = ctx.RequestServices.GetRequiredService<IShipReadinessGate>();
            var latest = await gate.GetLatestAsync(releaseId, ctx.RequestAborted);
            return latest is null ? Results.NotFound("No final checks have run for this release yet.") : Results.Ok(latest);
        });

        // The Checks library: what DevTeam will check in this workspace, described in plain
        // language (title / why it matters / how to fix), plus the technical command for the
        // "show details" disclosure.
        app.MapGet("/api/checks", (string workspacePath, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(workspacePath))
                return Results.BadRequest("workspacePath is required.");

            var profile = ReadinessProfileLoader.Load(workspacePath);
            return Results.Ok(CheckCatalog.Describe(profile.Phases));
        });

        // Oldest-first history of a release's checks — the source for the trend line.
        app.MapGet("/api/releases/{releaseId:guid}/readiness/history", async (Guid releaseId, HttpContext ctx) =>
        {
            var gate = ctx.RequestServices.GetRequiredService<IShipReadinessGate>();
            return Results.Ok(await gate.GetHistoryAsync(releaseId, ctx.RequestAborted));
        });

        // ─── hotfix endpoints (Part 7F) ─────────────────────────────────────
        app.MapPost("/api/hotfixes", async (CreateHotfixRequest request, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.Key))
                return Results.BadRequest("Key is required.");
            if (string.IsNullOrWhiteSpace(request.WorkspacePath))
                return Results.BadRequest("WorkspacePath is required.");

            var engine = ctx.RequestServices.GetRequiredService<IWorkflowEngine>();
            var hotfix = await engine.StartHotfixAsync(request.Key, request.WorkspacePath, ctx.RequestAborted);
            return Results.Created($"/api/features/{hotfix.Id}", hotfix);
        });

        app.MapGet("/api/hotfixes", async (string? workspacePath, HttpContext ctx) =>
        {
            var engine = ctx.RequestServices.GetRequiredService<IWorkflowEngine>();
            var hotfixes = await engine.ListHotfixesAsync(workspacePath, ctx.RequestAborted);
            return Results.Ok(hotfixes);
        });

        app.MapPost("/api/hotfixes/{hotfixId:guid}/finalize", async (Guid hotfixId, HttpContext ctx) =>
        {
            var engine = ctx.RequestServices.GetRequiredService<IWorkflowEngine>();
            try
            {
                var release = await engine.FinalizeHotfixAsync(hotfixId, ctx.RequestAborted);
                return Results.Ok(release);
            }
            catch (KeyNotFoundException) { return Results.NotFound($"Hotfix {hotfixId} not found."); }
            catch (InvalidOperationException ex) { return Results.BadRequest(ex.Message); }
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

        app.MapPost("/api/fs/cleanup", async (CleanupWorkspaceRequest request, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.WorkspacePath) || !Path.IsPathRooted(request.WorkspacePath))
                return Results.BadRequest("An absolute WorkspacePath is required.");

            var cleanup = ctx.RequestServices.GetRequiredService<IWorkspaceProcessCleanupService>();
            return Results.Ok(await cleanup.CleanupWorkspace(request.WorkspacePath));
        });

        // Stopping a process the sweep collected as needing approval. Approval is per-list: only
        // pids the sweep itself surfaced can be stopped — the route is never a kill-anyone door.
        app.MapPost("/api/fs/cleanup/approve", async (ApproveStopRequest request, HttpContext ctx) =>
        {
            if (request.ProcessIds.Count == 0)
                return Results.BadRequest("At least one process id is required.");

            var cleanup = ctx.RequestServices.GetRequiredService<IWorkspaceProcessCleanupService>();
            return Results.Ok(await cleanup.StopApproved(request.ProcessIds));
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

        // ─── diagnostics (for a support hand-off) ──────────────────────────────
        // A novice turns on detailed logging, reproduces the problem, then collects one file
        // for a specialist. Everything here is deliberately plain and needs no technical skill.
        app.MapGet("/api/diagnostics/settings", (HttpContext ctx) =>
        {
            var settings = ctx.RequestServices.GetRequiredService<DiagnosticsSettings>();
            var identity = ctx.RequestServices.GetRequiredService<RuntimeIdentity>();
            return Results.Ok(new DiagnosticsSettingsDto(settings.VerboseLogging, identity.LogsDirectory));
        });

        app.MapPost("/api/diagnostics/settings", async (DiagnosticsSettingsRequest request, HttpContext ctx) =>
        {
            var settings = ctx.RequestServices.GetRequiredService<DiagnosticsSettings>();
            await settings.SetVerboseLoggingAsync(request.VerboseLogging, ctx.RequestAborted);
            var identity = ctx.RequestServices.GetRequiredService<RuntimeIdentity>();
            return Results.Ok(new DiagnosticsSettingsDto(settings.VerboseLogging, identity.LogsDirectory));
        });

        app.MapPost("/api/diagnostics/bundle", async (DiagnosticsBundleRequest? request, HttpContext ctx) =>
        {
            var service = ctx.RequestServices.GetRequiredService<DiagnosticsBundleService>();
            var bytes = await service.BuildAsync(request, ctx.RequestAborted);
            var name = $"devteam-diagnostics-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip";
            return Results.File(bytes, "application/zip", name);
        });

        app.MapPost("/api/diagnostics/reveal-logs", (HttpContext ctx) =>
        {
            var identity = ctx.RequestServices.GetRequiredService<RuntimeIdentity>();
            ctx.RequestServices.GetRequiredService<IFileSystemService>().RevealInExplorer(identity.LogsDirectory);
            return Results.Ok();
        });

        // ─── metrics (what DevTeam cost, so it can be made cheaper) ────────────
        app.MapGet("/api/metrics/summary", async (string? workspacePath, Guid? featureId, Guid? releaseId, int? days, HttpContext ctx) =>
        {
            var service = ctx.RequestServices.GetRequiredService<MetricsService>();
            var scope = new MetricsScope(workspacePath, featureId, releaseId, days ?? 30);
            return Results.Ok(await service.SummarizeAsync(scope, ctx.RequestAborted));
        });

        app.MapGet("/api/metrics/turns", async (string? workspacePath, Guid? featureId, Guid? releaseId, int? days, int? limit, HttpContext ctx) =>
        {
            var service = ctx.RequestServices.GetRequiredService<MetricsService>();
            var scope = new MetricsScope(workspacePath, featureId, releaseId, days ?? 30);
            return Results.Ok(await service.TurnsAsync(scope, limit ?? 200, ctx.RequestAborted));
        });

        // The LLM/human-facing report: ranked findings + aggregates. `format=markdown` for one read.
        app.MapGet("/api/metrics/diagnose", async (string? workspacePath, Guid? featureId, Guid? releaseId, int? days, string? format, HttpContext ctx) =>
        {
            var service = ctx.RequestServices.GetRequiredService<MetricsService>();
            var scope = new MetricsScope(workspacePath, featureId, releaseId, days ?? 30);
            var summary = await service.SummarizeAsync(scope, ctx.RequestAborted);
            return string.Equals(format, "markdown", StringComparison.OrdinalIgnoreCase)
                ? Results.Text(MetricsMarkdown.Render(summary), "text/markdown")
                : Results.Ok(summary);
        });

        // ─── requirement progress (how much of the BRS is done) ────────────────
        app.MapGet("/api/features/{featureId:guid}/progress", async (Guid featureId, HttpContext ctx) =>
        {
            var db = await ctx.RequestServices.GetRequiredService<IDbContextFactory<DevTeamDbContext>>()
                .CreateDbContextAsync(ctx.RequestAborted);
            var feature = await db.ReleaseFeatures
                .Include(f => f.Release)
                .FirstOrDefaultAsync(f => f.Id == featureId, ctx.RequestAborted);
            if (feature is null)
                return Results.NotFound();

            var progress = RequirementProgressCalculator.Compute(feature.Release.WorkspacePath, feature.Key);
            return Results.Ok(new RequirementProgressDto(
                progress.Requirements,
                new ProgressCountDto(progress.Code.Done, progress.Code.Total),
                new ProgressCountDto(progress.Tests.Done, progress.Tests.Total)));
        });

        // ─── negotiation (the numbered points stages exchange) ─────────────────
        app.MapGet("/api/features/{featureId:guid}/negotiation", async (Guid featureId, HttpContext ctx) =>
        {
            var db = await ctx.RequestServices.GetRequiredService<IDbContextFactory<DevTeamDbContext>>()
                .CreateDbContextAsync(ctx.RequestAborted);
            var points = await db.ReviewFindings
                .Where(f => f.StageRun.ReleaseFeatureId == featureId)
                .ToListAsync(ctx.RequestAborted);

            // Open points first (what still needs action), then by round.
            var ordered = points
                .OrderBy(f => f.Status == ReviewFindingStatus.Open ? 0 : 1)
                .ThenBy(f => f.Round)
                .ThenBy(f => f.CreatedAt)
                .Select(ToNegotiationPointDto)
                .ToArray();
            return Results.Ok(ordered);
        });

        app.MapPost("/api/features/{featureId:guid}/negotiation/{findingId:guid}/resolve", async (Guid featureId, Guid findingId, HttpContext ctx) =>
        {
            var db = await ctx.RequestServices.GetRequiredService<IDbContextFactory<DevTeamDbContext>>()
                .CreateDbContextAsync(ctx.RequestAborted);
            var point = await db.ReviewFindings
                .FirstOrDefaultAsync(f => f.Id == findingId && f.StageRun.ReleaseFeatureId == featureId, ctx.RequestAborted);
            if (point is null)
                return Results.NotFound();

            point.Status = ReviewFindingStatus.Resolved;
            point.ResolvedAt = DateTimeOffset.UtcNow;
            point.UpdatedAt = DateTimeOffset.UtcNow;
            point.ResolutionNote = "Closed by the user.";
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(ToNegotiationPointDto(point));
        });

        app.MapPost("/api/features/{featureId:guid}/negotiation/{findingId:guid}/escalate", async (Guid featureId, Guid findingId, HttpContext ctx) =>
        {
            var db = await ctx.RequestServices.GetRequiredService<IDbContextFactory<DevTeamDbContext>>()
                .CreateDbContextAsync(ctx.RequestAborted);
            var point = await db.ReviewFindings
                .FirstOrDefaultAsync(f => f.Id == findingId && f.StageRun.ReleaseFeatureId == featureId, ctx.RequestAborted);
            if (point is null)
                return Results.NotFound();

            point.Status = ReviewFindingStatus.Escalated;
            point.UpdatedAt = DateTimeOffset.UtcNow;
            point.ResolutionNote = "Escalated by the user.";
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(ToNegotiationPointDto(point));
        });

        // ─── code overview (plain status for the project screen) ───────────────
        app.MapGet("/api/workspaces/code-context", async (string workspacePath, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(workspacePath))
                return Results.BadRequest("workspacePath is required.");

            var service = ctx.RequestServices.GetRequiredService<IRepoContextService>();
            var status = await service.GetStatusAsync(workspacePath, ctx.RequestAborted);
            return Results.Ok(new CodeContextStatusDto(
                status.State.ToString(), status.ChangesBehind, status.BuiltAt, status.Warnings));
        });

        app.MapPost("/api/workspaces/code-context/refresh", (string workspacePath, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(workspacePath))
                return Results.BadRequest("workspacePath is required.");

            ctx.RequestServices.GetRequiredService<IRepoContextService>().EnqueueRefresh(workspacePath, "manual");
            return Results.Accepted();
        });

        // ─── model candidates (the failover list) ──────────────────────────────
        app.MapGet("/api/models/candidates", async (string workspacePath, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(workspacePath))
                return Results.BadRequest("workspacePath is required.");

            var service = ctx.RequestServices.GetRequiredService<IModelCandidateService>();
            var candidates = await service.ListSeededAsync(workspacePath, ctx.RequestAborted);
            return Results.Ok(candidates.Select(ToCandidateDto).ToArray());
        });

        app.MapPost("/api/models/candidates", async (AddModelCandidateRequest request, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.WorkspacePath) || string.IsNullOrWhiteSpace(request.ModelId))
                return Results.BadRequest("WorkspacePath and ModelId are required.");

            var service = ctx.RequestServices.GetRequiredService<IModelCandidateService>();
            try
            {
                var added = await service.AddAsync(request.WorkspacePath, request.ModelId, ctx.RequestAborted);
                return Results.Ok(ToCandidateDto(added));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        app.MapDelete("/api/models/candidates/{id:guid}", async (Guid id, HttpContext ctx) =>
        {
            var service = ctx.RequestServices.GetRequiredService<IModelCandidateService>();
            await service.RemoveAsync(id, ctx.RequestAborted);
            return Results.NoContent();
        });

        app.MapPost("/api/models/candidates/reorder", async (ReorderModelCandidatesRequest request, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.WorkspacePath))
                return Results.BadRequest("WorkspacePath is required.");

            var service = ctx.RequestServices.GetRequiredService<IModelCandidateService>();
            await service.ReorderAsync(request.WorkspacePath, request.OrderedIds ?? [], ctx.RequestAborted);
            return Results.Ok((await service.ListAsync(request.WorkspacePath, ctx.RequestAborted)).Select(ToCandidateDto).ToArray());
        });

        app.MapPost("/api/models/candidates/{id:guid}/enabled", async (Guid id, SetModelCandidateEnabledRequest request, HttpContext ctx) =>
        {
            var service = ctx.RequestServices.GetRequiredService<IModelCandidateService>();
            await service.SetEnabledAsync(id, request.Enabled, ctx.RequestAborted);
            return Results.Ok();
        });

        // Everything the agent says it can run, for the "add a model" picker.
        app.MapGet("/api/models/available", async (string workspacePath, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(workspacePath))
                return Results.BadRequest("workspacePath is required.");

            var catalog = ctx.RequestServices.GetRequiredService<ModelCatalogService>();
            return Results.Ok(await catalog.GetAvailableModelsAsync(workspacePath, ctx.RequestAborted));
        });

        // ─── desktop notifications ─────────────────────────────────────────────
        app.MapGet("/api/notifications/settings", (HttpContext ctx) =>
        {
            var settings = ctx.RequestServices.GetRequiredService<NotificationSettings>();
            return Results.Ok(new NotificationSettingsDto(
                settings.StageComplete, settings.NeedsAttention, settings.ApprovalNeeded, settings.Sound));
        });

        app.MapPost("/api/notifications/settings", async (NotificationSettingsRequest request, HttpContext ctx) =>
        {
            var settings = ctx.RequestServices.GetRequiredService<NotificationSettings>();
            await settings.SetAsync(
                request.StageComplete, request.NeedsAttention, request.ApprovalNeeded, request.Sound, ctx.RequestAborted);
            return Results.Ok(new NotificationSettingsDto(
                settings.StageComplete, settings.NeedsAttention, settings.ApprovalNeeded, settings.Sound));
        });

        // Lets someone confirm notifications actually reach them — the app is headless, so
        // "did that work?" is otherwise unanswerable without waiting for a real event.
        app.MapPost("/api/notifications/test", async (HttpContext ctx) =>
        {
            var notifier = ctx.RequestServices.GetRequiredService<IPlatformNotifier>();
            await notifier.NotifyAsync(
                new NotificationRequest(
                    "DevTeam",
                    "Notifications are working — you'll be told when a step finishes or needs you.",
                    NotificationUrgency.Info),
                ctx.RequestAborted);
            return Results.Ok();
        });

        // ─── live SemaNami channel ──────────────────────────────────────────────
        // Available reflects whether TELEGRAM_BOT_TOKEN/TELEGRAM_CHAT_ID are actually set (the
        // channel can be turned on in settings, but has nothing to connect to without them) —
        // env vars, so this is re-checked live rather than cached from startup.
        app.MapGet("/api/semanami/settings", (HttpContext ctx) =>
        {
            var settings = ctx.RequestServices.GetRequiredService<SemaNamiSettings>();
            return Results.Ok(new SemaNamiSettingsDto(settings.Enabled, IsSemaNamiAvailable()));
        });

        app.MapPost("/api/semanami/settings", async (SetSemaNamiEnabledRequest request, HttpContext ctx) =>
        {
            var settings = ctx.RequestServices.GetRequiredService<SemaNamiSettings>();
            await settings.SetEnabledAsync(request.Enabled, ctx.RequestAborted);
            return Results.Ok(new SemaNamiSettingsDto(settings.Enabled, IsSemaNamiAvailable()));
        });
    }

    private static bool IsSemaNamiAvailable()
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN"))
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID"));

    private static ModelCandidateDto ToCandidateDto(ModelCandidate candidate)
    {
        var rating = ModelCatalog.RatingFor(candidate.ModelId);
        return new ModelCandidateDto(
            candidate.Id,
            candidate.ModelId,
            candidate.Priority,
            candidate.Enabled,
            candidate.UserAdded,
            candidate.CooldownUntil,
            candidate.LastFailureKind,
            candidate.LastFailureReason,
            rating?.Cost,
            rating?.Smartness,
            rating?.Note);
    }

    private static NegotiationPointDto ToNegotiationPointDto(ReviewFinding point)
        => new(
            point.Id,
            point.Target,
            point.Summary,
            point.Expected,
            point.Round,
            point.OpenedBy,
            point.PushedBackTo,
            point.Status.ToString(),
            point.ResponseKind.ToString(),
            point.ResponseText,
            point.RequirementRef,
            point.CreatedAt);
}