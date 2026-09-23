using System.Reflection;
using DevTeam.Broker.Context;
using DevTeam.Broker.Diagnostics;
using DevTeam.Broker.Domain;
using DevTeam.Broker.Gates;
using DevTeam.Broker.Gates.Readiness;
using DevTeam.Broker.Git;
using DevTeam.Broker.Metrics;
using DevTeam.Broker.Models;
using DevTeam.Broker.Notifications;
using DevTeam.Broker.Rpc;
using DevTeam.Broker.Server;
using DevTeam.Broker.Spoke;
using DevTeam.Broker.Workflow;
using DevTeam.Shared;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Core;
using Serilog.Events;
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

        // The level is switchable at runtime so a novice can turn on full detail, reproduce a
        // problem, hand the logs to a specialist, and turn it off again — without a rebuild or a
        // config edit. Normal operation stays at Information; "verbose" is Verbose (includes EF
        // SQL and framework diagnostics), which is what actually reconstructs a failure.
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);

        // Persisted, structured logging — the built-in console logger scrolls away and
        // nothing survives a restart. Also fills a real gap: a request that throws only
        // ever showed a bare status code to the client, with the actual exception visible
        // solely in a live console session (see UseSerilogRequestLogging below, which logs
        // the exception itself before rethrowing).
        builder.Host.UseSerilog((context, services, config) => config
            .ReadFrom.Services(services)
            .MinimumLevel.ControlledBy(levelSwitch)
            // Request logging is noisy and adds nothing a support bundle needs.
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(Path.Combine(identity.LogsDirectory, "devteam-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                // Bounded: verbose mode is chatty (every SQL statement) and must never be able
                // to fill the disk during an investigation.
                fileSizeLimitBytes: 25 * 1024 * 1024,
                rollOnFileSizeLimit: true));

        builder.Services.AddSingleton(levelSwitch);
        builder.Services.AddSingleton<DiagnosticsSettings>();
        builder.Services.AddSingleton<DiagnosticsBundleService>();
        builder.Services.AddSingleton<IModelCandidateService, ModelCandidateService>();
        // Reads the agent's own log, the only place a provider refusal is reported.
        builder.Services.AddSingleton<IProviderFailureWatcher>(
            _ => new OpenCodeLogWatcher(OpenCodeLogWatcher.DefaultLogPath));
        builder.Services.AddSingleton<NotificationSettings>();
        builder.Services.AddSingleton<IUserNotifier, UserNotifier>();
        // The app has no UI of its own, so the desktop is how it reaches someone who walked away.
        // One adapter per platform; Linux/macOS are stubs for now.
        if (OperatingSystem.IsWindows())
            builder.Services.AddSingleton<IPlatformNotifier, WindowsToastNotifier>();
        else
            builder.Services.AddSingleton<IPlatformNotifier, LoggingNotifier>();

        builder.Services.AddSingleton(identity);
        builder.Services.AddSingleton<IAppInfo, AppInfo>();
        builder.Services.AddSingleton<IAcpProcess>(sp =>
        {
            var exe = identity.OpenCodePath
                ?? throw new InvalidOperationException(
                    "opencode executable not found. Install opencode or set the path.");
            var logger = sp.GetRequiredService<ILogger<Program>>();
            // Stderr is drained by the process wrapper regardless; forwarding it here means the
            // agent's own complaints ("stream error: rate limit exceeded") land in our log too.
            return new OpencodeAcpProcess(exe, ["acp"], line => logger.LogDebug("opencode: {Line}", line));
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
        builder.Services.AddSingleton<IReadinessEnvironment, ReadinessEnvironment>();
        builder.Services.AddSingleton<IReadinessChecker, ReadinessChecker>();
        builder.Services.AddSingleton<IShipReadinessGate, ShipReadinessGate>();

        // Code overview (Repomix). Configuration first, then the fakeable Repomix/git seams, then
        // the coalescing queue + singleton service + its background worker.
        var repoContextOptions = new RepoContextOptions();
        builder.Configuration.GetSection("RepoContext").Bind(repoContextOptions);
        builder.Services.AddSingleton(repoContextOptions);
        builder.Services.AddSingleton<RepoContextQueue>();
        builder.Services.AddSingleton<IRepomixRunner>(sp => new RepomixRunner(
            sp.GetRequiredService<IProcessRunner>(), repoContextOptions.RepomixCommand,
            sp.GetService<ILogger<RepomixRunner>>()));
        builder.Services.AddSingleton<IGitProbe>(sp => new GitProbe(
            sp.GetRequiredService<IProcessRunner>(), sp.GetService<ILogger<GitProbe>>()));
        builder.Services.AddSingleton<IRepoContextService>(sp => new RepoContextService(
            repoContextOptions,
            sp.GetRequiredService<RepoContextQueue>(),
            sp.GetRequiredService<IRepomixRunner>(),
            sp.GetRequiredService<IGitProbe>(),
            sp.GetService<ILogger<RepoContextService>>()));
        builder.Services.AddHostedService<RepoContextWorker>();
        builder.Services.AddHostedService<MetricsRetentionService>();

        builder.Services.AddSingleton<IGate[]>(sp =>
        {
            var runner = sp.GetRequiredService<IProcessRunner>();
            var readiness = sp.GetRequiredService<IReadinessChecker>();
            return BuiltinGateRegistry.Create(runner, readiness, sp.GetRequiredService<IRepoContextService>());
        });
        builder.Services.AddSingleton<IGateRunner, GateRunner>();
        builder.Services.AddSingleton<WorkflowDefinitionLoader>();
        builder.Services.AddSingleton<PipelineEditorService>();
        builder.Services.AddScoped<IWorkflowEngine, WorkflowEngine>();
        builder.Services.AddSingleton<IModelSwitchBackend, BrokerModelSwitchBackend>();
        builder.Services.AddScoped<StageModelSwitcher>(sp => new StageModelSwitcher(
            sp.GetRequiredService<IDbContextFactory<DevTeamDbContext>>(),
            sp.GetRequiredService<IModelSwitchBackend>(),
            sp.GetRequiredService<ILogger<StageModelSwitcher>>()));
        builder.Services.AddSingleton<IGitService, GitService>();

        // Turn metrics: a per-turn ledger, an on-demand rollup/diagnosis, and a retention pass.
        builder.Services.AddSingleton<MetricsService>();
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

        // Restore the operator's logging preference before serving anything.
        app.Services.GetRequiredService<DiagnosticsSettings>().LoadAsync(CancellationToken.None)
            .GetAwaiter().GetResult();
        app.Services.GetRequiredService<NotificationSettings>().LoadAsync(CancellationToken.None)
            .GetAwaiter().GetResult();

        app.UseSerilogRequestLogging();
        app.UseRequestDiagnostics();
        app.UseAgentErrorHandling();
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