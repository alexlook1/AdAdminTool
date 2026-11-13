using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AdAdminTool.Services;

public class ConnectivityService
{
    private readonly int _pingTimeoutMs;
    private readonly int _tcpTimeoutMs;

    public ConnectivityService(int pingTimeoutMs = 1500, int tcpTimeoutMs = 1500)
    {
        _pingTimeoutMs = pingTimeoutMs;
        _tcpTimeoutMs = tcpTimeoutMs;
    }

    public async Task<bool> IsOnlineAsync(string host, CancellationToken ct = default)
        => (await CheckAsync(host, ct)).Online;

    public async Task<(bool Online, string Details)> CheckAsync(
        string host, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host))
            return (false, "пустое имя хоста");

        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, _pingTimeoutMs).WaitAsync(ct);
            if (reply.Status == IPStatus.Success)
                return (true, $"ping ok ({reply.RoundtripTime} ms, {reply.Address})");
        }
        catch { }

        try
        {
            using var client = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_tcpTimeoutMs);
            await client.ConnectAsync(host, 445, timeoutCts.Token);
            if (client.Connected)
                return (true, "tcp 445 ok");
        }
        catch { }

        return (false, "ping и tcp 445 недоступны (DNS? фаервол?)");
    }
}