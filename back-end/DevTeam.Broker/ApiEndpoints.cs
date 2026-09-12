using DevTeam.Broker.Server;

namespace DevTeam.Broker;

public static class ApiEndpoints
{
    public static void MapApi(this WebApplication app)
    {
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
                var result = await coordinator.PromptAsync(sessionId, request.Text, ctx.RequestAborted);
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
    }
}