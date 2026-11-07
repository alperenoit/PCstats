using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PCStatsWidget.Services;

public sealed class FpsMonitorService : IDisposable
{
    // ETW Provider GUIDs for DirectX Frame Present events
    private static readonly Guid DxgiProviderGuid = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9ProviderGuid = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");

    private const ushort DXGI_PRESENT_START_EVENT_ID = 42;
    private const ushort D3D9_PRESENT_START_EVENT_ID = 1;
    private const string SESSION_NAME = "PCStatsWidget_FPS_ETW_Session";

    private readonly ConcurrentDictionary<uint, ConcurrentQueue<long>> _processFrames = new();
    private readonly uint _currentAppPid = (uint)Environment.ProcessId;

    private ulong _sessionHandle;
    private ulong _traceHandle = INVALID_PROCESSTRACE_HANDLE;
    private Thread? _etwThread;
    private volatile bool _running;
    private EventRecordCallback? _callbackDelegate;

    // DWM fallback fields
    private ulong _lastDwmRefreshCount;
    private long _lastDwmTimestamp;
    private int _lastCalculatedFps;
    private string _fpsSource = "DWM";

    public string FpsSource => _fpsSource;
    public bool IsEtwActive { get; private set; }

    public FpsMonitorService()
    {
        StartEtwSession();
    }

    public int GetCurrentFps()
    {
        long now = Stopwatch.GetTimestamp();
        long oneSecondAgo = now - Stopwatch.Frequency;

        uint foregroundPid = Win32Helper.GetForegroundProcessId();

        // 1. Öncelik: Ön plandaki uygulamanın (oyunun) DXGI / D3D9 Present kareleri
        if (foregroundPid != 0 && foregroundPid != _currentAppPid &&
            _processFrames.TryGetValue(foregroundPid, out var fgQueue))
        {
            while (fgQueue.TryPeek(out long ts) && ts < oneSecondAgo)
            {
                fgQueue.TryDequeue(out _);
            }

            int count = fgQueue.Count;
            if (count > 0)
            {
                _fpsSource = "3D/Oyun";
                _lastCalculatedFps = count;
                return count;
            }
        }

        // 2. Öncelik: Eğer kullanıcı widget'a tıkladıysa bile arka planda çalışan en aktif 3D oyunun FPS'i
        int maxGameFps = 0;
        foreach (var kvp in _processFrames)
        {
            if (kvp.Key == _currentAppPid) continue;

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

        if (maxGameFps > 5)
        {
            _fpsSource = "3D/Oyun";
            _lastCalculatedFps = maxGameFps;
            return maxGameFps;
        }

        // 3. Öncelik: DWM (Desktop Window Manager) gerçek zamanlı ekran yenileme / kompozisyon FPS'i
        int dwmFps = GetDwmCompositionFps(now);
        if (dwmFps > 0)
        {
            _fpsSource = "Ekran";
            _lastCalculatedFps = dwmFps;
            return dwmFps;
        }

        return _lastCalculatedFps;
    }

    private int GetDwmCompositionFps(long nowTimestamp)
    {
        try
        {
            var timingInfo = new DWM_TIMING_INFO
            {
                cbSize = (uint)Marshal.SizeOf<DWM_TIMING_INFO>()
            };

            int hr = DwmGetCompositionTimingInfo(IntPtr.Zero, ref timingInfo);
            if (hr == 0)
            {
                ulong currentRefresh = timingInfo.cRefresh;
                if (_lastDwmTimestamp > 0 && currentRefresh >= _lastDwmRefreshCount)
                {
                    double elapsedSeconds = (double)(nowTimestamp - _lastDwmTimestamp) / Stopwatch.Frequency;
                    if (elapsedSeconds >= 0.25)
                    {
                        ulong deltaFrames = currentRefresh - _lastDwmRefreshCount;
                        int fps = (int)Math.Round(deltaFrames / elapsedSeconds);
                        _lastDwmRefreshCount = currentRefresh;
                        _lastDwmTimestamp = nowTimestamp;

                        if (fps > 0 && fps <= 1000)
                        {
                            return fps;
                        }
                    }
                }
                else
                {
                    _lastDwmRefreshCount = currentRefresh;
                    _lastDwmTimestamp = nowTimestamp;
                }

                // İlk ölçümde qpcRefreshPeriod veya rateRefresh oranını kullan
                if (timingInfo.rateRefresh.uiDenominator > 0)
                {
                    int rate = (int)Math.Round((double)timingInfo.rateRefresh.uiNumerator / timingInfo.rateRefresh.uiDenominator);
                    if (rate > 0 && rate <= 1000)
                        return rate;
                }
            }
        }
        catch
        {
            // DWM erişilemezse sessizce geç
        }

        return _lastCalculatedFps > 0 ? _lastCalculatedFps : 60;
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
                    ClientContext = 1 // QPC clock resolution
                },
                LogFileMode = EVENT_TRACE_REAL_TIME_MODE,
                LoggerNameOffset = (uint)Marshal.SizeOf<EVENT_TRACE_PROPERTIES>()
            };

            Marshal.StructureToPtr(props, propPtr, false);

            // Önceki kalmış oturumu temizle
            ControlTraceW(0, SESSION_NAME, propPtr, EVENT_TRACE_CONTROL_STOP);

            RtlZeroMemory(propPtr, (UIntPtr)totalSize);
            Marshal.StructureToPtr(props, propPtr, false);

            uint status = StartTraceW(out _sessionHandle, SESSION_NAME, propPtr);
            if (status != 0)
            {
                IsEtwActive = false;
                return;
            }

            // DXGI ve D3D9 sağlayıcılarını etkinleştir
            Guid dxgiGuid = DxgiProviderGuid;
            EnableTraceEx2(_sessionHandle, ref dxgiGuid, EVENT_CONTROL_CODE_ENABLE_PROVIDER, TRACE_LEVEL_INFORMATION, 0, 0, 0, IntPtr.Zero);

            Guid d3d9Guid = D3D9ProviderGuid;
            EnableTraceEx2(_sessionHandle, ref d3d9Guid, EVENT_CONTROL_CODE_ENABLE_PROVIDER, TRACE_LEVEL_INFORMATION, 0, 0, 0, IntPtr.Zero);

            _callbackDelegate = OnEventRecord;
            var logfile = new EVENT_TRACE_LOGFILEW
            {
                LoggerName = SESSION_NAME,
                ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD,
                EventRecordCallback = _callbackDelegate
            };

            _traceHandle = OpenTraceW(ref logfile);
            if (_traceHandle == INVALID_PROCESSTRACE_HANDLE)
            {
                IsEtwActive = false;
                return;
            }

            IsEtwActive = true;
            ulong[] handles = [_traceHandle];
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
            // Kapanışta hataları yut
        }
    }

    #region Native Win32 ETW & DWM Interop

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

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, ref DWM_TIMING_INFO pTimingInfo);

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

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct UNSIGNED_RATIO
    {
        public uint uiNumerator;
        public uint uiDenominator;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DWM_TIMING_INFO
    {
        public uint cbSize;
        public UNSIGNED_RATIO rateRefresh;
        public ulong qpcRefreshPeriod;
        public UNSIGNED_RATIO rateCompose;
        public ulong qpcVBlank;
        public ulong cRefresh;
        public uint cDXRefresh;
        public ulong qpcCompose;
        public ulong cFrame;
        public uint cDXPresent;
        public ulong cRefreshFrame;
        public ulong cFrameSubmitted;
        public uint cDXPresentSubmitted;
        public ulong cFrameConfirmed;
        public uint cDXPresentConfirmed;
        public ulong cRefreshConfirmed;
        public uint cDXRefreshConfirmed;
        public ulong cFramesLate;
        public uint cFramesOutstanding;
        public ulong cFrameDisplayed;
        public ulong qpcFrameDisplayed;
        public ulong cRefreshFrameDisplayed;
        public ulong cFrameComplete;
        public ulong qpcFrameComplete;
        public ulong cFramePending;
        public ulong qpcFramePending;
        public ulong cFramesDisplayed;
        public ulong cFramesComplete;
        public ulong cFramesPending;
        public ulong cFramesAvailable;
        public ulong cFramesDropped;
        public ulong cFramesMissed;
        public ulong cRefreshNextDisplayed;
        public ulong cRefreshNextPresented;
        public ulong cRefreshesDisplayed;
        public ulong cRefreshesPresented;
        public ulong cRefreshStarted;
        public ulong cPixelsReceived;
        public ulong cPixelsDrawn;
        public ulong cBuffersEmpty;
    }

    #endregion
}
