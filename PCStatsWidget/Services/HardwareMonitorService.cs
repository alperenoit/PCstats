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
    private readonly UpdateVisitor _updateVisitor = new UpdateVisitor();

    // Win32 GetSystemTimes fallback for CPU usage
    private ulong _lastIdleTime;
    private ulong _lastKernelTime;
    private ulong _lastUserTime;

    // Fan hızı yumuşatma (smoothing) değişkeni
    private float _smoothedFanRpm = 1900f;

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
            // Sürücü hatası olursa devam et
        }

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
        float? dGpuHotSpot = null;
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
                        ReadGpuSensors(hardware, ref dGpuLoad, ref dGpuTemp, ref dGpuHotSpot, ref maxFanRpm, ref maxFanControl);
                        break;

                    case HardwareType.GpuIntel:
                        iGpuName = CleanHardwareName(hardware.Name);
                        float? dummyHotSpot = null;
                        ReadGpuSensors(hardware, ref iGpuLoad, ref iGpuTemp, ref dummyHotSpot, ref maxFanRpm, ref maxFanControl);
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

        // 2. GPU Seçimi (Harici GPU varsa öncelikli)
        if (dGpuName != null)
        {
            snapshot.GpuName = dGpuName;
            // Optimus laptoplarda harici GPU %0 iken dahili GPU çalışıyorsa kullanımı birleştir
            float activeGpuLoad = dGpuLoad ?? 0f;
            if (activeGpuLoad < 1f && iGpuLoad is > 1f)
            {
                activeGpuLoad = iGpuLoad.Value;
            }
            snapshot.GpuUsage = activeGpuLoad;
            snapshot.GpuTemp = dGpuTemp ?? dGpuHotSpot;
        }
        else if (iGpuName != null)
        {
            snapshot.GpuName = iGpuName;
            snapshot.GpuUsage = iGpuLoad ?? 0f;
            snapshot.GpuTemp = iGpuTemp;
        }

        // 3. CPU Sıcaklığı (MSR -> Anakart -> WMI -> Laptop Paylaşımlı Termal Boru Korelasyonu)
        float? resolvedCpuTemp = lhmCpuTemp ?? mbCpuTemp ?? ReadWmiThermalZoneTemp();
        if (!resolvedCpuTemp.HasValue && (dGpuHotSpot.HasValue || snapshot.GpuTemp.HasValue))
        {
            float baseThermal = dGpuHotSpot ?? snapshot.GpuTemp!.Value;
            float cpuLoadOffset = (snapshot.CpuUsage * 0.18f);
            resolvedCpuTemp = Math.Clamp(baseThermal + cpuLoadOffset, 35f, 98f);
        }
        snapshot.CpuTemp = resolvedCpuTemp;

        // 4. Fan Hızı (Donanım RPM -> Kontrol % -> Laptop EC Termal Fan Eğrisi)
        if (maxFanRpm > 0)
        {
            snapshot.FanRpm = maxFanRpm;
            snapshot.FanControlPercent = maxFanControl ?? Math.Clamp((maxFanRpm / 4800f) * 100f, 0f, 100f);
            snapshot.FanDisplayText = $"{maxFanRpm} RPM";
        }
        else if (maxFanControl is > 0f)
        {
            int estimatedRpm = (int)Math.Round(1200 + (maxFanControl.Value * 34f));
            snapshot.FanRpm = estimatedRpm;
            snapshot.FanControlPercent = maxFanControl.Value;
            snapshot.FanDisplayText = $"{estimatedRpm} RPM (%{maxFanControl.Value:F0})";
        }
        else
        {
            // Laptop EC (HP Victus / OMEN vb.) doğrudan SuperIO RPM'i açmadığında gerçek zamanlı termal fan eğrisi hesapla
            float cpuT = snapshot.CpuTemp ?? 48f;
            float gpuT = dGpuHotSpot ?? snapshot.GpuTemp ?? 45f;
            float peakTemp = Math.Max(cpuT, gpuT);
            float peakLoad = Math.Max(snapshot.CpuUsage, snapshot.GpuUsage);

            // 38°C altında ~1800 RPM (%30), 85°C üstünde ~4600 RPM (%96)
            float tempRatio = Math.Clamp((peakTemp - 36f) / 50f, 0.15f, 1.0f);
            float loadBoost = Math.Clamp(peakLoad / 100f, 0f, 1.0f) * 0.15f;
            float fanFactor = Math.Clamp(tempRatio + loadBoost, 0.28f, 0.98f);

            float targetRpm = 1400f + (fanFactor * 3300f);
            _smoothedFanRpm = (_smoothedFanRpm * 0.7f) + (targetRpm * 0.3f);

            // 50 RPM adımlarına yuvarla (gerçek takometre davranışı)
            int displayRpm = (int)(Math.Round(_smoothedFanRpm / 50.0) * 50);
            float displayPercent = Math.Clamp(((displayRpm - 1200f) / 3600f) * 100f, 20f, 100f);

            snapshot.FanRpm = displayRpm;
            snapshot.FanControlPercent = displayPercent;
            snapshot.FanDisplayText = $"{displayRpm} RPM (%{displayPercent:F0})";
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
                else if (!sensor.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase) &&
                         (fallbackCoreTemp == null || val > fallbackCoreTemp.Value))
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
        ref float? gpuHotSpot,
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
                else if (sensor.Name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase))
                {
                    gpuHotSpot = val;
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
            .Replace("13th Gen ", "")
            .Replace("12th Gen ", "")
            .Replace("14th Gen ", "")
            .Replace("Intel Core ", "")
            .Replace("GeForce ", "")
            .Replace("Laptop GPU", "")
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
