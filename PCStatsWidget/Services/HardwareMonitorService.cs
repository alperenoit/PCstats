using System.Diagnostics;
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
    private const uint HP_WMI_GM_COMMAND = 0x00020008;
    private const uint HP_CMD_GET_FAN_SPEED_V1 = 0x11; // 17: Fan 1 & Fan 2 RPM (/100)
    private const uint HP_CMD_GET_SENSOR_TEMP = 0x23;  // 35: GetSensorValue(sensorId) -> CPU/Sys Temp (°C)
    private const uint HP_CMD_GET_FAN_LEVEL_V2 = 0x2D; // 45: GetFanLevel -> Fan 1 & Fan 2 RPM (/100)

    private static readonly byte[] HpSecuSign = new byte[] { 0x53, 0x45, 0x43, 0x55 }; // "SECU"

    private readonly Computer _computer;
    private readonly UpdateVisitor _updateVisitor = new UpdateVisitor();
    private readonly PerformanceCounter? _cpuUtilityCounter;

    // Win32 GetSystemTimes fallback for CPU usage
    private ulong _lastIdleTime;
    private ulong _lastKernelTime;
    private ulong _lastUserTime;

    public bool IsAdministrator { get; }

    public HardwareMonitorService()
    {
        IsAdministrator = CheckIsAdministrator();

        // Windows 11 Görev Yöneticisi ile birebir aynı CPU kullanım sayacı (% Processor Utility)
        try
        {
            _cpuUtilityCounter = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total", true);
            _ = _cpuUtilityCounter.NextValue();
        }
        catch
        {
            try
            {
                _cpuUtilityCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
                _ = _cpuUtilityCounter.NextValue();
            }
            catch
            {
                _cpuUtilityCounter = null;
            }
        }

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
            // Sürücü yüklenemezse diğer kaynaklarla devam et
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

        // 1. CPU Kullanım Yüzdesi (Windows Görev Yöneticisi % Processor Utility -> LHM -> Win32)
        float win32Cpu = UpdateWin32CpuUsage();
        float? perfCounterCpu = ReadPerfCounterCpuUsage();
        if (perfCounterCpu is > 0.1f)
        {
            snapshot.CpuUsage = Math.Clamp(perfCounterCpu.Value, 0f, 100f);
        }
        else if (lhmCpuLoad is > 0.1f)
        {
            snapshot.CpuUsage = Math.Clamp(lhmCpuLoad.Value, 0f, 100f);
        }
        else
        {
            snapshot.CpuUsage = win32Cpu;
        }

        // 2. GPU Seçimi (Harici GPU varsa öncelikli)
        if (dGpuName != null)
        {
            snapshot.GpuName = dGpuName;
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

        // 3. HP Victus / OMEN BIOS WMI Üzerinden Gerçek Fan RPM ve CPU Sıcaklığı Okuma
        ReadHpVictusBiosSensors(out int hpFan1Rpm, out int hpFan2Rpm, out float? hpBiosCpuTemp);

        // 4. CPU Sıcaklığı Seçimi (LHM Intel MSR -> HP BIOS WMI EC Sensörü -> Anakart)
        if (lhmCpuTemp is > 20f and < 115f)
        {
            snapshot.CpuTemp = lhmCpuTemp.Value;
        }
        else if (hpBiosCpuTemp is > 20f and < 115f)
        {
            snapshot.CpuTemp = hpBiosCpuTemp.Value;
        }
        else if (mbCpuTemp is > 20f and < 115f)
        {
            snapshot.CpuTemp = mbCpuTemp.Value;
        }
        else
        {
            snapshot.CpuTemp = null;
        }

        // 5. Fan Hızı Seçimi (HP BIOS WMI Gerçek Fan RPM -> LHM Fan RPM -> LHM Fan Control %)
        if (hpFan1Rpm > 0 || hpFan2Rpm > 0)
        {
            int peakHpFan = Math.Max(hpFan1Rpm, hpFan2Rpm);
            snapshot.FanRpm = peakHpFan;
            snapshot.FanControlPercent = Math.Clamp((peakHpFan / 5200f) * 100f, 0f, 100f);

            if (hpFan1Rpm > 0 && hpFan2Rpm > 0 && Math.Abs(hpFan1Rpm - hpFan2Rpm) >= 100)
            {
                snapshot.FanDisplayText = $"{hpFan1Rpm} / {hpFan2Rpm} RPM";
            }
            else
            {
                snapshot.FanDisplayText = $"{peakHpFan} RPM";
            }
        }
        else if (maxFanRpm > 0)
        {
            snapshot.FanRpm = maxFanRpm;
            snapshot.FanControlPercent = maxFanControl ?? Math.Clamp((maxFanRpm / 4800f) * 100f, 0f, 100f);
            snapshot.FanDisplayText = $"{maxFanRpm} RPM";
        }
        else if (maxFanControl is > 0f)
        {
            snapshot.FanRpm = (int)Math.Round(maxFanControl.Value * 45f);
            snapshot.FanControlPercent = maxFanControl.Value;
            snapshot.FanDisplayText = $"%{maxFanControl.Value:F0} Hız";
        }
        else
        {
            snapshot.FanRpm = 0;
            snapshot.FanControlPercent = 0f;
            snapshot.FanDisplayText = "0 RPM";
        }

        return snapshot;
    }

    private float? ReadPerfCounterCpuUsage()
    {
        if (_cpuUtilityCounter == null) return null;
        try
        {
            float val = _cpuUtilityCounter.NextValue();
            if (val is >= 0f and <= 200f)
            {
                return Math.Clamp(val, 0f, 100f);
            }
        }
        catch
        {
            // Sayaç okunamazsa geç
        }
        return null;
    }

    private static void ReadHpVictusBiosSensors(out int fan1Rpm, out int fan2Rpm, out float? cpuTemp)
    {
        fan1Rpm = 0;
        fan2Rpm = 0;
        cpuTemp = null;

        try
        {
            var scope = new ManagementScope(@"\\.\root\wmi");
            scope.Connect();

            using var biosMethodsClass = new ManagementClass(scope, new ManagementPath("hpqBIntM"), null);
            using var dataInClass = new ManagementClass(scope, new ManagementPath("hpqBDataIn"), null);

            using ManagementObjectCollection instances = biosMethodsClass.GetInstances();
            ManagementObject? biosInstance = null;
            foreach (ManagementObject obj in instances)
            {
                biosInstance = obj;
                break;
            }

            if (biosInstance == null) return;

            // A) Fan Hızı Okuma: CommandType = 45 (0x2D - GetFanLevel: 100 RPM birimiyle Fan 1 ve Fan 2)
            byte[]? fanData = InvokeHpBiosMethod(biosInstance, dataInClass, HP_WMI_GM_COMMAND, HP_CMD_GET_FAN_LEVEL_V2, new byte[4], "hpqBIOSInt128");
            if (fanData != null && fanData.Length >= 2)
            {
                int f1 = fanData[0];
                int f2 = fanData[1];

                // HP Victus'ta değerler 100 RPM çarpanlıdır (örn. 24 = 2400 RPM, 52 = 5200 RPM)
                if (f1 is > 0 and <= 85) fan1Rpm = f1 * 100;
                if (f2 is > 0 and <= 85) fan2Rpm = f2 * 100;
            }

            // Eğer 0x2D'den 0 geldiyse eski nesil 0x11 (CommandType = 17) dene
            if (fan1Rpm == 0 && fan2Rpm == 0)
            {
                byte[]? fanV1 = InvokeHpBiosMethod(biosInstance, dataInClass, HP_WMI_GM_COMMAND, HP_CMD_GET_FAN_SPEED_V1, new byte[4], "hpqBIOSInt128");
                if (fanV1 != null && fanV1.Length >= 2)
                {
                    if (fanV1[0] is > 0 and <= 85) fan1Rpm = fanV1[0] * 100;
                    if (fanV1[1] is > 0 and <= 85) fan2Rpm = fanV1[1] * 100;
                }
            }

            // B) CPU Sıcaklığı Okuma: CommandType = 35 (0x23 - GetSensorValue(0))
            byte[]? tempData = InvokeHpBiosMethod(biosInstance, dataInClass, HP_WMI_GM_COMMAND, HP_CMD_GET_SENSOR_TEMP, new byte[] { 0, 0, 0, 0 }, "hpqBIOSInt4")
                            ?? InvokeHpBiosMethod(biosInstance, dataInClass, HP_WMI_GM_COMMAND, HP_CMD_GET_SENSOR_TEMP, new byte[] { 0, 0, 0, 0 }, "hpqBIOSInt128");

            if (tempData != null && tempData.Length >= 1 && tempData[0] is > 20 and < 115)
            {
                cpuTemp = tempData[0];
            }
        }
        catch
        {
            // HP WMI BIOS desteklenmiyorsa veya erişim yoksa sessizce geç
        }
    }

    private static byte[]? InvokeHpBiosMethod(
        ManagementObject biosInstance,
        ManagementClass dataInClass,
        uint command,
        uint commandType,
        byte[] inputPayload,
        string methodName)
    {
        try
        {
            using ManagementBaseObject dataIn = dataInClass.CreateInstance();
            dataIn["Sign"] = HpSecuSign;
            dataIn["Command"] = command;
            dataIn["CommandType"] = commandType;
            dataIn["Size"] = (uint)inputPayload.Length;

            // hpqBData alanını doldur
            foreach (PropertyData prop in dataIn.Properties)
            {
                if (prop.Name.Equals("hpqBData", StringComparison.OrdinalIgnoreCase) ||
                    prop.Name.Equals("Data", StringComparison.OrdinalIgnoreCase))
                {
                    prop.Value = inputPayload;
                    break;
                }
            }

            using ManagementBaseObject inParams = biosInstance.GetMethodParameters(methodName);
            inParams["InData"] = dataIn;

            using ManagementBaseObject? outParams = biosInstance.InvokeMethod(methodName, inParams, null);
            if (outParams?["OutData"] is ManagementBaseObject outData)
            {
                foreach (PropertyData prop in outData.Properties)
                {
                    if (prop.Value is byte[] bytes && bytes.Length > 0)
                    {
                        return bytes;
                    }
                }
            }
        }
        catch
        {
            // Metot hatası olursa null dön
        }

        return null;
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
            else if (sensor.SensorType == SensorType.Temperature && val is > 20f and < 120f)
            {
                if (sensor.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase))
                    continue;

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
            else if (sensor.SensorType == SensorType.Temperature && val is > 15f and < 125f)
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
            else if (sensor.SensorType == SensorType.Temperature && val is > 22f and < 115f)
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
            _cpuUtilityCounter?.Dispose();
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
