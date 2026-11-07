using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;

namespace PCStatsWidget.Services;

public sealed class HardwareSnapshot
{
    public string CpuName { get; set; } = "CPU";
    public float CpuUsage { get; set; }
    public float? CpuTemp { get; set; }

    public string GpuName { get; set; } = "GPU";
    public float GpuUsage { get; set; }
    public float? GpuTemp { get; set; }

    public int FanRpm { get; set; }
    public float? FanControlPercent { get; set; }
    public string FanDisplayText { get; set; } = "0 RPM";
}

public sealed class HardwareMonitorService : IDisposable
{
    private readonly Computer _computer;
    private readonly UpdateVisitor _updateVisitor = new();

    // Win32 GetSystemTimes fallback for CPU usage
    private ulong _lastIdleTime;
    private ulong _lastKernelTime;
    private ulong _lastUserTime;

    public bool IsAdministrator { get; }

    public HardwareMonitorService()
    {
        IsAdministrator = CheckIsAdministrator();

        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true
        };

        try
        {
            _computer.Open();
        }
        catch
        {
            // Bazı sürücüler yüklenemezse devam et
        }

        // İlk CPU zamanlarını al
        UpdateWin32CpuUsage();
    }

    public static bool CheckIsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public HardwareSnapshot ReadSnapshot()
    {
        var snapshot = new HardwareSnapshot();

        float? lhmCpuLoad = null;
        float? lhmCpuTemp = null;
        float? mbCpuTemp = null;

        float? dGpuLoad = null;
        float? dGpuTemp = null;
        string? dGpuName = null;

        float? iGpuLoad = null;
        float? iGpuTemp = null;
        string? iGpuName = null;

        int maxFanRpm = 0;
        float? maxFanControl = null;

        try
        {
            _computer.Accept(_updateVisitor);

            foreach (IHardware hardware in _computer.Hardware)
            {
                switch (hardware.HardwareType)
                {
                    case HardwareType.Cpu:
                        snapshot.CpuName = CleanHardwareName(hardware.Name);
                        ReadCpuSensors(hardware, ref lhmCpuLoad, ref lhmCpuTemp, ref maxFanRpm, ref maxFanControl);
                        break;

                    case HardwareType.GpuNvidia:
                    case HardwareType.GpuAmd:
                        dGpuName = CleanHardwareName(hardware.Name);
                        ReadGpuSensors(hardware, ref dGpuLoad, ref dGpuTemp, ref maxFanRpm, ref maxFanControl);
                        break;

                    case HardwareType.GpuIntel:
                        iGpuName = CleanHardwareName(hardware.Name);
                        ReadGpuSensors(hardware, ref iGpuLoad, ref iGpuTemp, ref maxFanRpm, ref maxFanControl);
                        break;

                    default:
                        ReadMotherboardAndFanSensors(hardware, ref mbCpuTemp, ref maxFanRpm, ref maxFanControl);
                        break;
                }
            }
        }
        catch
        {
            // Sensör okuma hatası olursa yedeklere geç
        }

        // 1. CPU Kullanımı
        float win32Cpu = UpdateWin32CpuUsage();
        snapshot.CpuUsage = lhmCpuLoad is > 0.1f ? lhmCpuLoad.Value : win32Cpu;

        // 2. CPU Sıcaklığı
        snapshot.CpuTemp = lhmCpuTemp ?? mbCpuTemp ?? ReadWmiThermalZoneTemp();

        // 3. GPU Seçimi (Harici GPU varsa öncelikli, yoksa Dahili GPU)
        if (dGpuName != null)
        {
            snapshot.GpuName = dGpuName;
            snapshot.GpuUsage = dGpuLoad ?? 0f;
            snapshot.GpuTemp = dGpuTemp;
        }
        else if (iGpuName != null)
        {
            snapshot.GpuName = iGpuName;
            snapshot.GpuUsage = iGpuLoad ?? 0f;
            snapshot.GpuTemp = iGpuTemp;
        }

        // 4. Fan Hızı
        snapshot.FanRpm = maxFanRpm;
        snapshot.FanControlPercent = maxFanControl;

        if (maxFanRpm > 0)
        {
            snapshot.FanDisplayText = $"{maxFanRpm} RPM";
        }
        else if (maxFanControl is > 0f)
        {
            snapshot.FanDisplayText = $"%{maxFanControl.Value:F0} Hız";
        }
        else
        {
            snapshot.FanDisplayText = "0 RPM";
        }

        return snapshot;
    }

    private static void ReadCpuSensors(
        IHardware cpu,
        ref float? cpuLoad,
        ref float? cpuTemp,
        ref int maxFanRpm,
        ref float? maxFanControl)
    {
        float? fallbackCoreTemp = null;

        foreach (ISensor sensor in cpu.Sensors)
        {
            if (sensor.Value == null) continue;
            float val = sensor.Value.Value;

            if (sensor.SensorType == SensorType.Load)
            {
                if (sensor.Name.Contains("Total", StringComparison.OrdinalIgnoreCase))
                {
                    cpuLoad = Math.Clamp(val, 0f, 100f);
                }
            }
            else if (sensor.SensorType == SensorType.Temperature && val is > 0f and < 130f)
            {
                if (sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                    sensor.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
                    sensor.Name.Contains("Average", StringComparison.OrdinalIgnoreCase) ||
                    sensor.Name.Contains("Core Max", StringComparison.OrdinalIgnoreCase))
                {
                    cpuTemp = val;
                }
                else if (fallbackCoreTemp == null || val > fallbackCoreTemp.Value)
                {
                    fallbackCoreTemp = val;
                }
            }
            else if (sensor.SensorType == SensorType.Fan && val > maxFanRpm)
            {
                maxFanRpm = (int)Math.Round(val);
            }
            else if (sensor.SensorType == SensorType.Control && (maxFanControl == null || val > maxFanControl.Value))
            {
                maxFanControl = Math.Clamp(val, 0f, 100f);
            }
        }

        cpuTemp ??= fallbackCoreTemp;
    }

    private static void ReadGpuSensors(
        IHardware gpu,
        ref float? gpuLoad,
        ref float? gpuTemp,
        ref int maxFanRpm,
        ref float? maxFanControl)
    {
        float? d3dLoad = null;
        float? fallbackTemp = null;

        foreach (ISensor sensor in gpu.Sensors)
        {
            if (sensor.Value == null) continue;
            float val = sensor.Value.Value;

            if (sensor.SensorType == SensorType.Load)
            {
                if (sensor.Name.Contains("GPU Core", StringComparison.OrdinalIgnoreCase))
                {
                    gpuLoad = Math.Clamp(val, 0f, 100f);
                }
                else if (sensor.Name.Contains("D3D 3D", StringComparison.OrdinalIgnoreCase))
                {
                    if (d3dLoad == null || val > d3dLoad.Value)
                        d3dLoad = Math.Clamp(val, 0f, 100f);
                }
            }
            else if (sensor.SensorType == SensorType.Temperature && val is > 0f and < 130f)
            {
                if (sensor.Name.Contains("GPU Core", StringComparison.OrdinalIgnoreCase))
                {
                    gpuTemp = val;
                }
                else if (fallbackTemp == null)
                {
                    fallbackTemp = val;
                }
            }
            else if (sensor.SensorType == SensorType.Fan && val > maxFanRpm)
            {
                maxFanRpm = (int)Math.Round(val);
            }
            else if (sensor.SensorType == SensorType.Control && (maxFanControl == null || val > maxFanControl.Value))
            {
                maxFanControl = Math.Clamp(val, 0f, 100f);
            }
        }

        if (gpuLoad == null || (d3dLoad != null && d3dLoad > gpuLoad))
            gpuLoad = d3dLoad ?? gpuLoad;

        gpuTemp ??= fallbackTemp;
    }

    private static void ReadMotherboardAndFanSensors(
        IHardware hw,
        ref float? mbCpuTemp,
        ref int maxFanRpm,
        ref float? maxFanControl)
    {
        foreach (ISensor sensor in hw.Sensors)
        {
            if (sensor.Value == null) continue;
            float val = sensor.Value.Value;

            if (sensor.SensorType == SensorType.Fan && val is > 0f and < 15000f)
            {
                int rpm = (int)Math.Round(val);
                if (rpm > maxFanRpm)
                    maxFanRpm = rpm;
            }
            else if (sensor.SensorType == SensorType.Control && val is >= 0f and <= 100f)
            {
                if (maxFanControl == null || val > maxFanControl.Value)
                    maxFanControl = val;
            }
            else if (sensor.SensorType == SensorType.Temperature && val is > 10f and < 115f)
            {
                if (sensor.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase) ||
                    sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                     mbCpuTemp == null)
                {
                    mbCpuTemp = val;
                }
            }
        }

        foreach (IHardware sub in hw.SubHardware)
        {
            ReadMotherboardAndFanSensors(sub, ref mbCpuTemp, ref maxFanRpm, ref maxFanControl);
        }
    }

    private float UpdateWin32CpuUsage()
    {
        if (!GetSystemTimes(out FILETIME idleFt, out FILETIME kernelFt, out FILETIME userFt))
            return 0f;

        ulong idle = ((ulong)idleFt.dwHighDateTime << 32) | idleFt.dwLowDateTime;
        ulong kernel = ((ulong)kernelFt.dwHighDateTime << 32) | kernelFt.dwLowDateTime;
        ulong user = ((ulong)userFt.dwHighDateTime << 32) | userFt.dwLowDateTime;

        ulong idleDiff = idle - _lastIdleTime;
        ulong kernelDiff = kernel - _lastKernelTime;
        ulong userDiff = user - _lastUserTime;

        _lastIdleTime = idle;
        _lastKernelTime = kernel;
        _lastUserTime = user;

        ulong totalSystem = kernelDiff + userDiff;
        if (totalSystem == 0) return 0f;

        float busy = (float)(totalSystem - idleDiff) * 100f / totalSystem;
        return Math.Clamp(busy, 0f, 100f);
    }

    private static float? ReadWmiThermalZoneTemp()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\CIMV2",
                "SELECT HighPrecisionTemperature, Temperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation");

            foreach (ManagementObject obj in searcher.Get())
            {
                if (obj["HighPrecisionTemperature"] is uint hpTemp && hpTemp > 2732)
                {
                    float c = (hpTemp / 10f) - 273.15f;
                    if (c is > 10f and < 120f) return c;
                }

                if (obj["Temperature"] is uint temp && temp > 273)
                {
                    float c = temp - 273.15f;
                    if (c is > 10f and < 120f) return c;
                }
            }
        }
        catch
        {
            // WMI termal bölge desteklenmiyorsa geç
        }

        return null;
    }

    private static string CleanHardwareName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Donanım";
        return raw
            .Replace("(R)", "")
            .Replace("(TM)", "")
            .Replace("Processor", "")
            .Replace("CPU", "")
            .Replace("NVIDIA ", "")
            .Replace("AMD ", "")
            .Trim();
    }

    public void Dispose()
    {
        try
        {
            _computer.Close();
        }
        catch
        {
            // Kapanış hatalarını yoksay
        }
    }

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (IHardware subHardware in hardware.SubHardware)
                subHardware.Accept(this);
        }

        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);
}
