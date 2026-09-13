using System.Reflection;
using DevTeam.Broker.Domain;
using DevTeam.Broker.Gates;
using DevTeam.Broker.Rpc;
using DevTeam.Broker.Server;
using DevTeam.Broker.Spoke;
using DevTeam.Broker.Workflow;
using DevTeam.Shared;
using Microsoft.EntityFrameworkCore;
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

        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(identity.Port));

        builder.Services.AddSingleton(identity);
        builder.Services.AddSingleton<IAppInfo, AppInfo>();
        builder.Services.AddSingleton<IAcpProcess>(sp =>
        {
            var exe = identity.OpenCodePath
                ?? throw new InvalidOperationException(
                    "opencode executable not found. Install opencode or set the path.");
            return new OpencodeAcpProcess(exe, ["acp"]);
        });
        builder.Services.AddSingleton<IAgentSpoke, OpencodeAcpSpoke>();
        builder.Services.AddSingleton<BrokerCoordinator>();
        builder.Services.AddSingleton<IFileSystemService, FileSystemService>();
        builder.Services.AddSingleton<IProcessRunner, SystemProcessRunner>();
        builder.Services.AddSingleton<IGate[]>(sp =>
        {
            var runner = sp.GetRequiredService<IProcessRunner>();
            return BuiltinGateRegistry.Create(runner);
        });
        builder.Services.AddSingleton<IGateRunner, GateRunner>();
        builder.Services.AddSingleton<WorkflowDefinitionLoader>();
        builder.Services.AddScoped<IWorkflowEngine, WorkflowEngine>();
        builder.Services.AddDbContextFactory<DevTeamDbContext>(options =>
            options.UseSqlite($"Data Source={identity.DatabasePath}"));
        builder.Services.AddSignalR();
        builder.Services.AddSingleton<IEventBroadcaster, SignalRHubBroadcaster>();
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
        });

        var app = builder.Build();

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
                context.Response.ContentType = "text/html";
                await context.Response.SendFileAsync(Path.Combine(app.Environment.WebRootPath, "index.html"));
                return;
            }

            await next();
        });
    }
}