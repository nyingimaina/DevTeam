using System.Text;
using DevTeam.Broker.Gates;

namespace DevTeam.Broker.Notifications;

/// <summary>
/// Windows adapter: raises a real Action-Center toast with sound.
///
/// It drives the native WinRT toast API through a small PowerShell helper rather than referencing
/// the WinRT projections directly, so the broker stays a plain cross-platform project (no
/// Windows-only target framework, no extra package). The trade-off is ~1s of process start per
/// notification, which is irrelevant for something a person reads afterwards; swapping this for an
/// in-process adapter later is a one-class change thanks to <see cref="IPlatformNotifier"/>.
/// </summary>
public sealed class WindowsToastNotifier : IPlatformNotifier
{
    private const string AppId = "DevTeam";
    private static readonly Lazy<string> ScriptPath = new(WriteScript);

    private readonly IProcessRunner _runner;
    private readonly ILogger<WindowsToastNotifier> _logger;

    public WindowsToastNotifier(IProcessRunner runner, ILogger<WindowsToastNotifier> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public async Task NotifyAsync(NotificationRequest request, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return;

        // Base64 so a title or message can never break out of the command line.
        var title = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.Title));
        var message = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.Message));
        var sound = request.Urgency == NotificationUrgency.Attention
            ? "ms-winsoundevent:Notification.Looping.Alarm"
            : "ms-winsoundevent:Notification.Default";

        var arguments =
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{ScriptPath.Value}\" -Title64 {title} -Message64 {message} -Sound {sound}";

        var result = await _runner.RunAsync(
            new ProcessRunRequest("powershell.exe", arguments, Path.GetTempPath(), TimeoutMs: 15000), ct);

        if (result.ExitCode != 0)
            _logger.LogDebug("Toast notification failed: {Error}", result.StandardError);
    }

    private static string WriteScript()
    {
        var path = Path.Combine(Path.GetTempPath(), "devteam-toast.ps1");
        File.WriteAllText(path, Script);
        return path;
    }

    private const string Script = """
        param([string]$Title64, [string]$Message64, [string]$Sound)

        $ErrorActionPreference = 'Stop'
        [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType=WindowsRuntime] | Out-Null
        [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom, ContentType=WindowsRuntime] | Out-Null

        $title = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Title64))
        $message = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Message64))

        $template = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent(
            [Windows.UI.Notifications.ToastTemplateType]::ToastText02)
        $nodes = $template.GetElementsByTagName('text')
        $nodes.Item(0).AppendChild($template.CreateTextNode($title)) | Out-Null
        $nodes.Item(1).AppendChild($template.CreateTextNode($message)) | Out-Null

        $audio = $template.CreateElement('audio')
        $audio.SetAttribute('src', $Sound)
        $audio.SetAttribute('silent', 'false')
        $template.DocumentElement.AppendChild($audio) | Out-Null

        [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('DevTeam').Show(
            [Windows.UI.Notifications.ToastNotification]::new($template))
        """;
}
