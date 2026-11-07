using System.Net.NetworkInformation;

namespace PCStatsWidget.Services;

public sealed class PingService : IDisposable
{
    private readonly Ping _ping = new();
    private readonly string[] _targets = ["1.1.1.1", "8.8.8.8", "208.67.222.222"];
    private readonly string[] _targetNames = ["Cloudflare (1.1.1.1)", "Google (8.8.8.8)", "OpenDNS"];
    private int _currentTargetIndex;

    public long LastPingMs { get; private set; } = -1;
    public string CurrentTargetName => _targetNames[_currentTargetIndex];
    public string CurrentTargetAddress => _targets[_currentTargetIndex];

    public void CycleTarget()
    {
        _currentTargetIndex = (_currentTargetIndex + 1) % _targets.Length;
    }

    public async Task<long> MeasurePingAsync()
    {
        try
        {
            string host = _targets[_currentTargetIndex];
            PingReply reply = await _ping.SendPingAsync(host, 1200);
            if (reply.Status == IPStatus.Success)
            {
                LastPingMs = reply.RoundtripTime;
                return LastPingMs;
            }
        }
        catch
        {
            // Ağ hatası olursa -1
        }

        LastPingMs = -1;
        return -1;
    }

    public void Dispose()
    {
        _ping.Dispose();
    }
}
