using System.Reflection;
using DevTeam.Broker.Domain;
using DevTeam.Broker.Gates;
using DevTeam.Broker.Git;
using DevTeam.Broker.Rpc;
using DevTeam.Broker.Server;
using DevTeam.Broker.Spoke;
using DevTeam.Broker.Workflow;
using DevTeam.Shared;
using Microsoft.EntityFrameworkCore;
using Serilog;
using System.Text.Json.Serialization;

namespace DevTeam.Broker;

public partial class Program
{
    public static void Main(string[] args)
    {
        var identity = RuntimeIdentity.Resolve(args, portEnv: null, dataDirEnv: null,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        Directory.CreateDirectory(identity.DataDirectory);
        Directory.CreateDirectory(identity.LogsDirectory);

        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(identity.Port));

        // Persisted, structured logging — the built-in console logger scrolls away and
        // nothing survives a restart. Also fills a real gap: a request that throws only
        // ever showed a bare status code to the client, with the actual exception visible
        // solely in a live console session (see UseSerilogRequestLogging below, which logs
        // the exception itself before rethrowing).
        builder.Host.UseSerilog((context, services, config) => config
            .ReadFrom.Services(services)
            .WriteTo.Console()
            .WriteTo.File(Path.Combine(identity.LogsDirectory, "devteam-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14));

        builder.Services.AddSingleton(identity);
        builder.Services.AddSingleton<IAppInfo, AppInfo>();
        builder.Services.AddSingleton<IAcpProcess>(sp =>
        {
            var exe = identity.OpenCodePath
                ?? throw new InvalidOperationException(
                    "opencode executable not found. Install opencode or set the path.");
            return new OpencodeAcpProcess(exe, ["acp"]);
        });
        builder.Services.AddSingleton<IPermissionPolicy, WorkspaceScopedPermissionPolicy>();
        builder.Services.AddSingleton<IAgentSpoke, OpencodeAcpSpoke>();
        builder.Services.AddSingleton<ActiveTurnTracker>();
        builder.Services.AddSingleton<BrokerCoordinator>();
        builder.Services.AddSingleton<IWorkflowCoordinator>(sp => sp.GetRequiredService<BrokerCoordinator>());
        builder.Services.AddSingleton<ModelCatalogService>();
        builder.Services.AddSingleton<IProcessLauncher, SystemProcessLauncher>();
        builder.Services.AddSingleton<IFileSystemService, FileSystemService>();
#pragma warning disable CA1416 // App is Windows-only in practice; WmiProcessInspector is annotated accordingly.
        builder.Services.AddSingleton<IOsProcessInspector, WmiProcessInspector>();
#pragma warning restore CA1416
        builder.Services.AddSingleton<IWorkspaceProcessCleanupService, WorkspaceProcessCleanupService>();
#pragma warning disable CA1416 // App is Windows-only in practice; DpapiGitCredentialStore is annotated accordingly.
        builder.Services.AddSingleton<IGitCredentialStore, DpapiGitCredentialStore>();
#pragma warning restore CA1416
        builder.Services.AddSingleton<IProcessRunner, SystemProcessRunner>();
        builder.Services.AddSingleton<IGate[]>(sp =>
        {
            var runner = sp.GetRequiredService<IProcessRunner>();
            return BuiltinGateRegistry.Create(runner);
        });
        builder.Services.AddSingleton<IGateRunner, GateRunner>();
        builder.Services.AddSingleton<WorkflowDefinitionLoader>();
        builder.Services.AddScoped<IWorkflowEngine, WorkflowEngine>();
        builder.Services.AddSingleton<IGitService, GitService>();
        builder.Services.AddDbContextFactory<DevTeamDbContext>(options =>
            options.UseSqlite($"Data Source={identity.DatabasePath}"));
        builder.Services.AddSignalR();
        builder.Services.AddSingleton<IEventBroadcaster, SignalRHubBroadcaster>();
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

        var app = builder.Build();

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DevTeamDbContext>();
            db.Database.EnsureCreated();
            DevTeamDbContextSchemaSync.EnsureAllTablesCreated(db);
        }

        app.UseSerilogRequestLogging();
        app.UseRequestDiagnostics();
        app.MapHub<BrokerHub>("/hub");
        app.MapApi();

        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseSpaFallback();

        app.Run();
    }
}

public interface IAppInfo
{
    string Version { get; }
}

public sealed class AppInfo : IAppInfo
{
    public string Version { get; } =
        typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? "0.0.0";
}

public static class SpaFallbackExtensions
{
    /// <summary>Serves index.html for non-API, non-hub routes so the SPA can deep-link.</summary>
    public static void UseSpaFallback(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Method == HttpMethods.Get &&
                context.Request.Path.StartsWithSegments("/api") is false &&
                context.Request.Path.StartsWithSegments("/healthz") is false &&
                !Path.HasExtension(context.Request.Path) &&
                context.Request.Path != "/hub")
            {
                if (TryResolveIndexPath(app.Environment.WebRootPath) is { } indexPath)
                {
                    context.Response.ContentType = "text/html";
                    await context.Response.SendFileAsync(indexPath);
                    return;
                }
            }

            await next();
        });
    }

    /// <summary>
    /// Resolves <c>wwwroot/index.html</c> only when the web UI is actually
    /// deployed next to the broker binary. Returns null (falling through to the
    /// pipeline) when there is no web root or the index file is missing, instead
    /// of throwing and surfacing a 500.
    /// </summary>
    internal static string? TryResolveIndexPath(string? webRootPath)
    {
        if (string.IsNullOrEmpty(webRootPath))
            return null;

        var indexPath = Path.Combine(webRootPath, "index.html");
        return File.Exists(indexPath) ? indexPath : null;
    }
}