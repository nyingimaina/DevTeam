using System.Reflection;
using DevTeam.Broker.Context;
using DevTeam.Broker.Diagnostics;
using DevTeam.Broker.Domain;
using DevTeam.Broker.Gates;
using DevTeam.Broker.Gates.Readiness;
using DevTeam.Broker.Git;
using DevTeam.Broker.Licensing;
using DevTeam.Broker.Metrics;
using DevTeam.Broker.Models;
using DevTeam.Broker.Notifications;
using DevTeam.Broker.Rpc;
using DevTeam.Broker.SemaNami;
using DevTeam.Broker.Server;
using DevTeam.Broker.Spoke;
using DevTeam.Broker.Workflow;
using DevTeam.Shared;
using Microsoft.EntityFrameworkCore;
using SemaNami.Core;
// Aliased, not a plain `using` — SemaNami.Core.Conversations.IProcessRunner (unused here; it's
// for SemaNami's own OS-service-registration CLI features) collides with the pervasive
// DevTeam.Broker.Gates.IProcessRunner.
using SNC = SemaNami.Core.Conversations;
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

        // Single-instance license, decided before anything with a side effect: two live
        // brokers on one data directory is the worst observed failure mode (2026-10-01:
        // nine broker lifetimes in sixteen seconds fought over the shared SQLite database
        // and Serilog files; one died mid-transaction and lost a 201 release insert).
        if (!BrokerLicenseGate.TryEnter(identity))
            return;

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
        builder.Services.AddSingleton<AttentionService>();
        builder.Services.AddSingleton<IUserNotifier, UserNotifier>();
        // The app has no UI of its own, so the desktop is how it reaches someone who walked away.
        // One adapter per platform; Linux/macOS are stubs for now. Registered by concrete type
        // (not IPlatformNotifier directly) so the CompositePlatformNotifier factory below can
        // resolve it alongside SemaNamiPlatformNotifier without either replacing the other.
        if (OperatingSystem.IsWindows())
            builder.Services.AddSingleton<WindowsToastNotifier>();
        else
            builder.Services.AddSingleton<LoggingNotifier>();

        // SemaNami: the live Telegram channel. Optional — reuses SemaNami's own
        // TELEGRAM_BOT_TOKEN/TELEGRAM_CHAT_ID env var convention; if either is missing, the
        // channel simply isn't registered (the composite below still gets the OS notifier on
        // its own). Whether it's actually USED, even when registered, is separately gated by
        // SemaNamiSettings.Enabled (an in-app toggle) — see SemaNamiListenerService.
        builder.Services.AddSingleton<SemaNamiSettings>();
        var telegramBotToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
        var telegramChatId = Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID");
        var semaNamiAvailable = !string.IsNullOrWhiteSpace(telegramBotToken) && !string.IsNullOrWhiteSpace(telegramChatId);
        if (semaNamiAvailable)
        {
            var semaNamiDbPath = Path.Combine(identity.DataDirectory, "semanami.db");
            builder.Services.AddSingleton<SNC.IConversationStore>(_ => new SNC.SqliteConversationStore(semaNamiDbPath));
            builder.Services.AddSingleton<ITelegramMessageSender>(_ => new TelegramBotMessageSender(telegramBotToken!));
            builder.Services.AddSingleton<SNC.IUpdatesSource>(
                _ => new SNC.TelegramUpdatesSource(telegramBotToken!, long.Parse(telegramChatId!)));
            builder.Services.AddSingleton<SNC.IRealtimeNotifier, SNC.InProcessRealtimeNotifier>();
            builder.Services.AddSingleton<SNC.ConversationListener>();
            builder.Services.AddSingleton(sp => new SNC.ConversationSender(
                sp.GetRequiredService<SNC.IConversationStore>(), sp.GetRequiredService<ITelegramMessageSender>(), telegramChatId!));
            builder.Services.AddSingleton<SemaNamiChannelState>();
            builder.Services.AddSingleton<SemaNamiPlatformNotifier>();
            builder.Services.AddSingleton<ISemaNamiReplyRouter, SemaNamiReplyRouter>();
            builder.Services.AddHostedService<SemaNamiListenerService>();
        }

        builder.Services.AddSingleton<IPlatformNotifier>(sp =>
        {
            var notifiers = new List<IPlatformNotifier>();
            notifiers.Add(OperatingSystem.IsWindows()
                ? sp.GetRequiredService<WindowsToastNotifier>()
                : sp.GetRequiredService<LoggingNotifier>());
            if (semaNamiAvailable)
                notifiers.Add(sp.GetRequiredService<SemaNamiPlatformNotifier>());
            return new CompositePlatformNotifier(notifiers, sp.GetRequiredService<ILogger<CompositePlatformNotifier>>());
        });

        builder.Services.AddSingleton(identity);
        builder.Services.AddSingleton<IAppInfo, AppInfo>();
        builder.Services.AddSingleton(BrokerBootInfo.ForThisProcess(identity.DataDirectory));
        // The ACP process and the agent spoke are both created on first use, never on resolution.
        // They sit on the dependency chain of the workflow engine, so an eager factory here made
        // every read-only endpoint (GET /api/releases, /api/hotfixes, ...) launch the agent and
        // answer 500 whenever the launch failed.
        builder.Services.AddSingleton<IAcpProcess>(sp => new DeferredAcpProcess(() =>
        {
            var exe = identity.OpenCodePath
                ?? throw new InvalidOperationException(
                    "opencode executable not found. Install the opencode command-line tool " +
                    $"(npm install -g opencode-ai, or winget install opencode), or set OPENCODE_PATH. " +
                    $"Searched: {string.Join(", ", identity.OpenCodeSearchedDirectories)}");
            var logger = sp.GetRequiredService<ILogger<Program>>();
            // Logged at Information because this is the difference between "the CLI is missing" and
            // "the CLI was found but Windows refused to launch it" — the two look identical from the
            // outside otherwise, and the second one used to surface only as an opaque 500.
            var launch = OpenCodePathResolver.ResolveLaunchTarget(exe);
            logger.LogInformation(
                "launching opencode: discovered={Discovered} launchTarget={LaunchTarget} ({Outcome}) cwd={Cwd}",
                exe,
                launch.Path,
                launch.Resolved ? "symlink resolved" : launch.UnresolvedReason,
                Environment.CurrentDirectory);
            // Which compaction config the child will actually run with. The agent inherits this
            // process's environment, so an OPENCODE_CONFIG_CONTENT set here is one the policy
            // deliberately steps aside for — logging it is the only way to tell "prune is on" from
            // "prune was requested but something else won".
            logger.LogInformation(
                "opencode compaction: prune=true reserved={Reserved} source={Source}",
                CompactionPolicy.ReservedTokens,
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(CompactionPolicy.EnvironmentVariable))
                    ? "devteam policy"
                    : "inherited " + CompactionPolicy.EnvironmentVariable);
            // Stderr is drained by the process wrapper regardless; forwarding it here means the
            // agent's own complaints ("stream error: rate limit exceeded") land in our log too.
            return AcpProcessLaunch.Create(
                exe,
                ["acp"],
                line => logger.LogDebug("opencode: {Line}", line));
        }));
        builder.Services.AddSingleton<IPermissionPolicy, WorkspaceScopedPermissionPolicy>();
        builder.Services.AddSingleton<IAgentSpoke>(sp => new LazyAgentSpoke(() =>
            new OpencodeAcpSpoke(sp.GetRequiredService<IAcpProcess>())));
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
        // Runs first of the hosted services: every run must be classified (GatesRunning healed,
        // lost turns escalated) before any background worker or incoming request can touch them.
        builder.Services.AddSingleton<WorkflowCrashRecoverer>();
        builder.Services.AddHostedService<WorkflowCrashRecoveryService>();
        // The heartbeat runs alongside so those classifications can tell a genuine broker death
        // from "another process merely resolved Program against the shared database".
        builder.Services.AddHostedService<BrokerHeartbeatService>();
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

        // Record how opencode was located (or that it was not). Without this a detection failure
        // is only ever visible to the user, never in the log a support bundle collects.
        if (identity.OpenCodePath is null)
        {
            app.Logger.LogWarning(
                "opencode CLI not found. Searched: {Directories}. Desktop app installed: {DesktopApp}",
                identity.OpenCodeSearchedDirectories,
                identity.OpenCodeDesktopAppInstalled);
        }
        else
        {
            app.Logger.LogInformation(
                "opencode CLI resolved to {Path} (requires shell: {RequiresShell})",
                identity.OpenCodePath,
                identity.OpenCodeRequiresShell);
        }

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
        app.Services.GetRequiredService<SemaNamiSettings>().LoadAsync(CancellationToken.None)
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