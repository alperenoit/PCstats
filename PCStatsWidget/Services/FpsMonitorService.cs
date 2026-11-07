using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace PCStatsWidget.Services;

public sealed class FpsMonitorService : IDisposable
{
    // ETW Provider GUIDs for DirectX Frame Present events
    private static readonly Guid DxgiProviderGuid = new Guid("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9ProviderGuid = new Guid("783ACA0A-790E-4D7F-8451-AA850511C6B9");

    // Masaüstü / Tarayıcı / Editör süreçleri (bunların 1-2 FPS'lik imleç yanıp sönmeleri oyun FPS'i değildir)
    private static readonly HashSet<string> NonGameProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "SearchHost", "ShellExperienceHost", "StartMenuExperienceHost",
        "TextInputHost", "ApplicationFrameHost", "SystemSettings", "Taskmgr",
        "code", "antigravity", "devenv", "rider64",
        "chrome", "msedge", "firefox", "brave", "opera",
        "discord", "spotify", "steamwebhelper", "epicgameslauncher",
        "cmd", "powershell", "pwsh", "WindowsTerminal", "conhost",
        "PCStatsWidget"
    };

    private const ushort DXGI_PRESENT_START_EVENT_ID = 42;
    private const ushort D3D9_PRESENT_START_EVENT_ID = 1;
    private const string SESSION_NAME = "PCStatsWidget_FPS_ETW_Session";

    private readonly ConcurrentDictionary<uint, ConcurrentQueue<long>> _processFrames = new ConcurrentDictionary<uint, ConcurrentQueue<long>>();
    private readonly ConcurrentDictionary<uint, bool> _processIsGameCache = new ConcurrentDictionary<uint, bool>();
    private readonly ConcurrentQueue<long> _wpfRenderFrames = new ConcurrentQueue<long>();
    private readonly uint _currentAppPid = (uint)Environment.ProcessId;

    private ulong _sessionHandle;
    private ulong _traceHandle = INVALID_PROCESSTRACE_HANDLE;
    private Thread? _etwThread;
    private volatile bool _running;
    private EventRecordCallback? _callbackDelegate;

    private int _displayRefreshRate = 60;
    private string _fpsSource = "Ekran";

    public string FpsSource => _fpsSource;
    public bool IsEtwActive { get; private set; }

    public FpsMonitorService()
    {
        _displayRefreshRate = QueryDisplayRefreshRate();

        // WPF VSync Rendering olayını dinle (canlı ekran kompozisyon FPS ölçümü için)
        CompositionTarget.Rendering += OnWpfCompositionRendering;

        StartEtwSession();
    }

    private void OnWpfCompositionRendering(object? sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        _wpfRenderFrames.Enqueue(now);

        // Kuyruğu temiz tut
        long twoSecondsAgo = now - (Stopwatch.Frequency * 2);
        while (_wpfRenderFrames.TryPeek(out long oldTs) && oldTs < twoSecondsAgo)
        {
            _wpfRenderFrames.TryDequeue(out _);
        }
    }

    public int GetCurrentFps()
    {
        long now = Stopwatch.GetTimestamp();
        long oneSecondAgo = now - Stopwatch.Frequency;

        uint foregroundPid = Win32Helper.GetForegroundProcessId();

        // 1. Öncelik: Ön plandaki uygulama gerçek bir 3D Oyun ise ETW DXGI/D3D9 Present sayısını al
        if (foregroundPid != 0 && foregroundPid != _currentAppPid && IsLikelyGameProcess(foregroundPid))
        {
            if (_processFrames.TryGetValue(foregroundPid, out var fgQueue))
            {
                while (fgQueue.TryPeek(out long ts) && ts < oneSecondAgo)
                {
                    fgQueue.TryDequeue(out _);
                }

                int count = fgQueue.Count;
                // Gerçek bir oyun döngüsü en az 8+ FPS sürekli kare üretir (1-2 FPS masaüstü imleç yanıp sönmesidir)
                if (count >= 8)
                {
                    _fpsSource = "3D/Oyun";
                    return count;
                }
            }
        }

        // 2. Öncelik: Arka planda veya çerçevesiz pencerede aktif çalışan bir 3D Oyun var mı?
        int maxGameFps = 0;
        foreach (var kvp in _processFrames)
        {
            uint pid = kvp.Key;
            if (pid == _currentAppPid || !IsLikelyGameProcess(pid)) continue;

            var q = kvp.Value;
            while (q.TryPeek(out long ts) && ts < oneSecondAgo)
            {
                q.TryDequeue(out _);
            }

            int c = q.Count;
            if (c > maxGameFps)
            {
                maxGameFps = c;
            }
        }

        if (maxGameFps >= 12)
        {
            _fpsSource = "3D/Oyun";
            return maxGameFps;
        }

        // 3. Öncelik: Masaüstü / Ekran gerçek zamanlı VSync yenileme hızı
        _fpsSource = "Ekran";
        return GetScreenRefreshFps(now, oneSecondAgo);
    }

    private int GetScreenRefreshFps(long now, long oneSecondAgo)
    {
        int hz = QueryDisplayRefreshRate();
        if (hz > 0)
        {
            _displayRefreshRate = hz;
        }

        while (_wpfRenderFrames.TryPeek(out long ts) && ts < oneSecondAgo)
        {
            _wpfRenderFrames.TryDequeue(out _);
        }

        int renderedInLastSec = _wpfRenderFrames.Count;

        // Eğer WPF VSync tam hızda koşuyorsa onu döndür, aksi halde DWM/Monitör aktif Hz değerini döndür
        if (renderedInLastSec >= _displayRefreshRate - 5 && renderedInLastSec <= _displayRefreshRate + 5)
        {
            return renderedInLastSec;
        }

        return _displayRefreshRate;
    }

    private bool IsLikelyGameProcess(uint pid)
    {
        if (_processIsGameCache.TryGetValue(pid, out bool cached))
            return cached;

        try
        {
            using var proc = Process.GetProcessById((int)pid);
            string name = proc.ProcessName;
            bool isGame = !NonGameProcessNames.Contains(name);
            _processIsGameCache[pid] = isGame;
            return isGame;
        }
        catch
        {
            return false;
        }
    }

    private static int QueryDisplayRefreshRate()
    {
        try
        {
            var devMode = new DEVMODEW();
            devMode.dmSize = (ushort)Marshal.SizeOf<DEVMODEW>();
            if (EnumDisplaySettingsW(null, ENUM_CURRENT_SETTINGS, ref devMode))
            {
                if (devMode.dmDisplayFrequency is > 10 and <= 1000)
                {
                    return (int)devMode.dmDisplayFrequency;
                }
            }
        }
        catch
        {
            // Hata olursa 60 Hz varsayılan
        }
        return 60;
    }

    private void StartEtwSession()
    {
        try
        {
            _running = true;
            _etwThread = new Thread(EtwWorkerLoop)
            {
                IsBackground = true,
                Name = "ETW_FPS_Listener"
            };
            _etwThread.Start();
        }
        catch
        {
            IsEtwActive = false;
        }
    }

    private void EtwWorkerLoop()
    {
        IntPtr propPtr = IntPtr.Zero;
        try
        {
            int sessionNameBytes = (SESSION_NAME.Length + 1) * 2;
            int totalSize = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>() + sessionNameBytes + 256;
            propPtr = Marshal.AllocHGlobal(totalSize);
            RtlZeroMemory(propPtr, (UIntPtr)totalSize);

            var props = new EVENT_TRACE_PROPERTIES
            {
                Wnode = new WNODE_HEADER
                {
                    BufferSize = (uint)totalSize,
                    Flags = WNODE_FLAG_TRACED_GUID,
                    ClientContext = 1
                },
                LogFileMode = EVENT_TRACE_REAL_TIME_MODE,
                LoggerNameOffset = (uint)Marshal.SizeOf<EVENT_TRACE_PROPERTIES>()
            };

            Marshal.StructureToPtr(props, propPtr, false);
            ControlTraceW(0, SESSION_NAME, propPtr, EVENT_TRACE_CONTROL_STOP);

            RtlZeroMemory(propPtr, (UIntPtr)totalSize);
            Marshal.StructureToPtr(props, propPtr, false);

            uint status = StartTraceW(out _sessionHandle, SESSION_NAME, propPtr);
            if (status != 0)
            {
                IsEtwActive = false;
                return;
            }

            Guid dxgiGuid = DxgiProviderGuid;
            EnableTraceEx2(_sessionHandle, ref dxgiGuid, EVENT_CONTROL_CODE_ENABLE_PROVIDER, TRACE_LEVEL_INFORMATION, 0, 0, 0, IntPtr.Zero);

            Guid d3d9Guid = D3D9ProviderGuid;
            EnableTraceEx2(_sessionHandle, ref d3d9Guid, EVENT_CONTROL_CODE_ENABLE_PROVIDER, TRACE_LEVEL_INFORMATION, 0, 0, 0, IntPtr.Zero);

            _callbackDelegate = OnEventRecord;
            var logfile = new EVENT_TRACE_LOGFILEW
            {
                LoggerName = SESSION_NAME,
                ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD,
                CurrentEventPadding = new byte[328],
                LogfileHeaderPadding = new byte[112],
                EventRecordCallback = _callbackDelegate
            };

            _traceHandle = OpenTraceW(ref logfile);
            if (_traceHandle == INVALID_PROCESSTRACE_HANDLE)
            {
                IsEtwActive = false;
                return;
            }

            IsEtwActive = true;
            ulong[] handles = new ulong[] { _traceHandle };
            ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            IsEtwActive = false;
        }
        finally
        {
            if (propPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(propPtr);
            }
        }
    }

    private void OnEventRecord(ref EVENT_RECORD record)
    {
        if (!_running) return;

        ushort eventId = record.EventHeader.EventDescriptor.Id;
        Guid provider = record.EventHeader.ProviderId;

        bool isPresent = (provider == DxgiProviderGuid && eventId == DXGI_PRESENT_START_EVENT_ID) ||
                         (provider == D3D9ProviderGuid && eventId == D3D9_PRESENT_START_EVENT_ID);

        if (isPresent)
        {
            uint pid = record.EventHeader.ProcessId;
            if (pid != 0 && pid != _currentAppPid)
            {
                var queue = _processFrames.GetOrAdd(pid, _ => new ConcurrentQueue<long>());
                queue.Enqueue(Stopwatch.GetTimestamp());
            }
        }
    }

    public void Dispose()
    {
        _running = false;
        CompositionTarget.Rendering -= OnWpfCompositionRendering;

        try
        {
            if (_traceHandle != INVALID_PROCESSTRACE_HANDLE)
            {
                CloseTrace(_traceHandle);
                _traceHandle = INVALID_PROCESSTRACE_HANDLE;
            }

            if (_sessionHandle != 0)
            {
                int totalSize = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>() + 512;
                IntPtr propPtr = Marshal.AllocHGlobal(totalSize);
                RtlZeroMemory(propPtr, (UIntPtr)totalSize);
                var props = new EVENT_TRACE_PROPERTIES
                {
                    Wnode = new WNODE_HEADER { BufferSize = (uint)totalSize },
                    LoggerNameOffset = (uint)Marshal.SizeOf<EVENT_TRACE_PROPERTIES>()
                };
                Marshal.StructureToPtr(props, propPtr, false);
                ControlTraceW(_sessionHandle, SESSION_NAME, propPtr, EVENT_TRACE_CONTROL_STOP);
                Marshal.FreeHGlobal(propPtr);
                _sessionHandle = 0;
            }
        }
        catch
        {
            // Kapanış hatalarını yoksay
        }
    }

    #region Native Win32 ETW & Display Interop

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
    private const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
    private const uint EVENT_TRACE_CONTROL_STOP = 1;
    private const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
    private const byte TRACE_LEVEL_INFORMATION = 4;
    private const uint PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
    private const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
    private const ulong INVALID_PROCESSTRACE_HANDLE = ulong.MaxValue;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void EventRecordCallback(ref EVENT_RECORD EventRecord);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string? lpszDeviceName, int iModeNum, ref DEVMODEW lpDevMode);

    [DllImport("kernel32.dll")]
    private static extern void RtlZeroMemory(IntPtr dst, UIntPtr length);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint StartTraceW(out ulong TraceHandle, string InstanceName, IntPtr Properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ControlTraceW(ulong TraceHandle, string InstanceName, IntPtr Properties, uint ControlCode);

    [DllImport("advapi32.dll")]
    private static extern uint EnableTraceEx2(
        ulong TraceHandle,
        ref Guid ProviderId,
        uint ControlCode,
        byte Level,
        ulong MatchAnyKeyword,
        ulong MatchAllKeyword,
        uint Timeout,
        IntPtr EnableParameters);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ulong OpenTraceW(ref EVENT_TRACE_LOGFILEW Logfile);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint ProcessTrace(ulong[] HandleArray, uint HandleCount, IntPtr StartTime, IntPtr EndTime);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint CloseTrace(ulong TraceHandle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODEW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNODE_HEADER
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public long TimeStamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_TRACE_PROPERTIES
    {
        public WNODE_HEADER Wnode;
        public uint BufferSize;
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;
        public uint BuffersWritten;
        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct EVENT_TRACE_LOGFILEW
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? LogFileName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? LoggerName;
        public long CurrentTime;
        public uint BuffersRead;
        public uint ProcessTraceMode;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 328)]
        public byte[] CurrentEventPadding;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 112)]
        public byte[] LogfileHeaderPadding;
        public IntPtr BufferCallback;
        public uint BufferSize;
        public uint Filled;
        public uint EventsLost;
        public EventRecordCallback? EventRecordCallback;
        public uint IsKernelTrace;
        public IntPtr Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_DESCRIPTOR
    {
        public ushort Id;
        public byte Version;
        public byte Channel;
        public byte Level;
        public byte Opcode;
        public ushort Task;
        public ulong Keyword;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_HEADER
    {
        public ushort Size;
        public ushort HeaderType;
        public ushort Flags;
        public ushort EventProperty;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid ProviderId;
        public EVENT_DESCRIPTOR EventDescriptor;
        public ulong ProcessorTime;
        public Guid ActivityId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ETW_BUFFER_CONTEXT
    {
        public byte ProcessorNumber;
        public byte Alignment;
        public ushort LoggerId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_RECORD
    {
        public EVENT_HEADER EventHeader;
        public ETW_BUFFER_CONTEXT BufferContext;
        public ushort ExtendedDataCount;
        public ushort UserDataLength;
        public IntPtr ExtendedData;
        public IntPtr UserData;
        public IntPtr UserContext;
    }

    #endregion
}
