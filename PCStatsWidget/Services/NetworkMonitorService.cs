using System.Diagnostics;
using System.Net.NetworkInformation;

namespace PCStatsWidget.Services;

public sealed class NetworkSnapshot
{
    public double DownloadBytesPerSec { get; set; }
    public double UploadBytesPerSec { get; set; }
    public string DownloadText { get; set; } = "0 KB/s";
    public string UploadText { get; set; } = "0 KB/s";
    public string CompactDownloadText { get; set; } = "0K";
    public string CompactUploadText { get; set; } = "0K";
}

public sealed class NetworkMonitorService
{
    private long _lastBytesReceived;
    private long _lastBytesSent;
    private long _lastTimestamp;
    private bool _initialized;

    public NetworkMonitorService()
    {
        TryGetTotalBytes(out _lastBytesReceived, out _lastBytesSent);
        _lastTimestamp = Stopwatch.GetTimestamp();
        _initialized = true;
    }

    public NetworkSnapshot ReadSnapshot()
    {
        var snapshot = new NetworkSnapshot();
        long now = Stopwatch.GetTimestamp();

        if (!TryGetTotalBytes(out long currentRx, out long currentTx))
        {
            return snapshot;
        }

        if (!_initialized)
        {
            _lastBytesReceived = currentRx;
            _lastBytesSent = currentTx;
            _lastTimestamp = now;
            _initialized = true;
            return snapshot;
        }

        double elapsedSeconds = (double)(now - _lastTimestamp) / Stopwatch.Frequency;
        if (elapsedSeconds <= 0.05)
        {
            return snapshot;
        }

        long rxDiff = currentRx >= _lastBytesReceived ? currentRx - _lastBytesReceived : 0;
        long txDiff = currentTx >= _lastBytesSent ? currentTx - _lastBytesSent : 0;

        _lastBytesReceived = currentRx;
        _lastBytesSent = currentTx;
        _lastTimestamp = now;

        snapshot.DownloadBytesPerSec = rxDiff / elapsedSeconds;
        snapshot.UploadBytesPerSec = txDiff / elapsedSeconds;

        snapshot.DownloadText = FormatSpeed(snapshot.DownloadBytesPerSec);
        snapshot.UploadText = FormatSpeed(snapshot.UploadBytesPerSec);
        snapshot.CompactDownloadText = FormatCompactSpeed(snapshot.DownloadBytesPerSec);
        snapshot.CompactUploadText = FormatCompactSpeed(snapshot.UploadBytesPerSec);

        return snapshot;
    }

    private static bool TryGetTotalBytes(out long totalRx, out long totalTx)
    {
        totalRx = 0;
        totalTx = 0;

        try
        {
            NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (NetworkInterface ni in interfaces)
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                    continue;

                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                    continue;

                IPv4InterfaceStatistics stats = ni.GetIPv4Statistics();
                totalRx += stats.BytesReceived;
                totalTx += stats.BytesSent;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string FormatSpeed(double bytesPerSec)
    {
        double kb = bytesPerSec / 1024.0;
        if (kb < 1.0)
        {
            return "0 KB/s";
        }
        if (kb < 1024.0)
        {
            return $"{kb:F0} KB/s";
        }
        double mb = kb / 1024.0;
        return mb >= 100.0 ? $"{mb:F0} MB/s" : $"{mb:F1} MB/s";
    }

    private static string FormatCompactSpeed(double bytesPerSec)
    {
        double kb = bytesPerSec / 1024.0;
        if (kb < 1.0)
        {
            return "0K";
        }
        if (kb < 1024.0)
        {
            return $"{kb:F0}K";
        }
        double mb = kb / 1024.0;
        return $"{mb:F1}M";
    }
}
