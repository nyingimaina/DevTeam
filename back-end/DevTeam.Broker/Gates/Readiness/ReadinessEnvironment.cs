using System.Net.Sockets;

namespace DevTeam.Broker.Gates.Readiness;

/// <summary>
/// Probes the local database so a check that needs one can be skipped honestly instead of
/// reporting a meaningless failure. Mirrors the old verify.ps1 Test-DatabaseReachable.
/// </summary>
public sealed class ReadinessEnvironment : IReadinessEnvironment
{
    private const string DatabaseHost = "localhost";
    private const int DatabasePort = 3306;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(750);

    public bool IsDatabaseReachable()
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(DatabaseHost, DatabasePort);
            return connect.Wait(ProbeTimeout) && client.Connected;
        }
        catch (Exception ex) when (ex is AggregateException or SocketException or ObjectDisposedException)
        {
            return false;
        }
    }
}
