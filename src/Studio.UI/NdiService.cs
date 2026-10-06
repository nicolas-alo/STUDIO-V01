using System.IO;
using System.Runtime.InteropServices;

namespace Studio.UI;

public sealed class NdiSourceInfo
{
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";

    public override string ToString() => Name;
}

public sealed class NdiService : IDisposable
{
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct NdiSource
    {
        public IntPtr Name;
        public IntPtr Url;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct NdiFindCreate
    {
        [MarshalAs(UnmanagedType.I1)]
        public bool ShowLocalSources;
        public IntPtr Groups;
        public IntPtr ExtraIps;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct NdiRecvCreate
    {
        public NdiSource Source;
        public int ColorFormat;
        public int Bandwidth;
        [MarshalAs(UnmanagedType.I1)]
        public bool AllowVideoFields;
        public IntPtr NdiReceiverName;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct NdiSendCreate
    {
        public IntPtr NdiName;
        public IntPtr Groups;
        [MarshalAs(UnmanagedType.I1)]
        public bool ClockVideo;
        [MarshalAs(UnmanagedType.I1)]
        public bool ClockAudio;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct NdiVideoFrame
    {
        public int XRes;
        public int YRes;
        public int FourCC;
        public int FrameRateN;
        public int FrameRateD;
        public float PictureAspectRatio;
        public int FrameFormatType;
        public long Timecode;
        public IntPtr Data;
        public int LineStride;
        public IntPtr Metadata;
        public long Timestamp;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool InitializeDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DestroyDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr FindCreateV2Delegate(ref NdiFindCreate settings);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FindDestroyDelegate(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool FindWaitForSourcesDelegate(IntPtr instance, uint timeoutMs);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr FindGetCurrentSourcesDelegate(IntPtr instance, out uint count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr RecvCreateV3Delegate(ref NdiRecvCreate settings);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RecvDestroyDelegate(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int RecvCaptureV3Delegate(
        IntPtr instance,
        ref NdiVideoFrame video,
        IntPtr audio,
        IntPtr metadata,
        uint timeoutMs);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RecvFreeVideoV2Delegate(
        IntPtr instance,
        ref NdiVideoFrame video);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr SendCreateDelegate(ref NdiSendCreate settings);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SendDestroyDelegate(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SendVideoV2Delegate(
        IntPtr instance,
        ref NdiVideoFrame video);

    private const int FrameTypeVideo = 1;
    private const int RecvColorBgrxBgra = 0;
    private const int RecvBandwidthHighest = 100;
    private const int FrameFormatProgressive = 1;
    private const int FourCcBgrx = ('B') | ('G' << 8) | ('R' << 16) | ('X' << 24);

    private IntPtr _library;
    private IntPtr _finder;
    private IntPtr _receiver;
    private IntPtr _sender;

    private InitializeDelegate? _initialize;
    private DestroyDelegate? _destroy;
    private FindCreateV2Delegate? _findCreateV2;
    private FindDestroyDelegate? _findDestroy;
    private FindWaitForSourcesDelegate? _findWaitForSources;
    private FindGetCurrentSourcesDelegate? _findGetCurrentSources;
    private RecvCreateV3Delegate? _recvCreateV3;
    private RecvDestroyDelegate? _recvDestroy;
    private RecvCaptureV3Delegate? _recvCaptureV3;
    private RecvFreeVideoV2Delegate? _recvFreeVideoV2;
    private SendCreateDelegate? _sendCreate;
    private SendDestroyDelegate? _sendDestroy;
    private SendVideoV2Delegate? _sendVideoV2;

    private IntPtr _senderName;
    private bool _initialized;

    public string? RuntimePath { get; private set; }
    public string Status { get; private set; } = "NDI no inicializado";
    public bool IsAvailable => _initialized && _finder != IntPtr.Zero;
    public bool IsReceiverActive => _receiver != IntPtr.Zero;
    public bool IsSenderActive => _sender != IntPtr.Zero;

    public bool Initialize()
    {
        if (_initialized) return true;

        foreach (var candidate in GetLibraryCandidates())
        {
            if (!File.Exists(candidate)) continue;

            try
            {
                if (!NativeLibrary.TryLoad(candidate, out _library) || _library == IntPtr.Zero)
                    continue;

                _initialize = LoadDelegate<InitializeDelegate>("NDIlib_initialize");
                _destroy = LoadDelegate<DestroyDelegate>("NDIlib_destroy");
                _findCreateV2 = LoadDelegate<FindCreateV2Delegate>("NDIlib_find_create_v2");
                _findDestroy = LoadDelegate<FindDestroyDelegate>("NDIlib_find_destroy");
                _findWaitForSources = LoadDelegate<FindWaitForSourcesDelegate>("NDIlib_find_wait_for_sources");
                _findGetCurrentSources = LoadDelegate<FindGetCurrentSourcesDelegate>("NDIlib_find_get_current_sources");
                _recvCreateV3 = LoadDelegate<RecvCreateV3Delegate>("NDIlib_recv_create_v3");
                _recvDestroy = LoadDelegate<RecvDestroyDelegate>("NDIlib_recv_destroy");
                _recvCaptureV3 = LoadDelegate<RecvCaptureV3Delegate>("NDIlib_recv_capture_v3");
                _recvFreeVideoV2 = LoadDelegate<RecvFreeVideoV2Delegate>("NDIlib_recv_free_video_v2");
                _sendCreate = LoadDelegate<SendCreateDelegate>("NDIlib_send_create");
                _sendDestroy = LoadDelegate<SendDestroyDelegate>("NDIlib_send_destroy");
                _sendVideoV2 = LoadDelegate<SendVideoV2Delegate>("NDIlib_send_send_video_v2");

                if (_initialize is null || _destroy is null ||
                    _findCreateV2 is null || _findDestroy is null ||
                    _findWaitForSources is null || _findGetCurrentSources is null ||
                    _recvCreateV3 is null || _recvDestroy is null ||
                    _recvCaptureV3 is null || _recvFreeVideoV2 is null ||
                    _sendCreate is null || _sendDestroy is null || _sendVideoV2 is null)
                {
                    UnloadLibrary();
                    continue;
                }

                if (!_initialize())
                {
                    UnloadLibrary();
                    continue;
                }

                RuntimePath = candidate;
                _initialized = true;
                Status = "NDI Runtime detectado";
                EnsureFinder();
                return true;
            }
            catch
            {
                UnloadLibrary();
            }
        }

        Status = "NDI Runtime no encontrado";
        return false;
    }

    public IReadOnlyList<NdiSourceInfo> RefreshSources(uint waitMs = 250)
    {
        if (!Initialize())
            return [];

        try
        {
            EnsureFinder();
            if (_finder == IntPtr.Zero || _findWaitForSources is null || _findGetCurrentSources is null)
                return [];

            _findWaitForSources(_finder, waitMs);

            var pointer = _findGetCurrentSources(_finder, out var count);
            if (pointer == IntPtr.Zero || count == 0)
                return [];

            var result = new List<NdiSourceInfo>((int)count);
            var size = Marshal.SizeOf<NdiSource>();

            for (var i = 0; i < count; i++)
            {
                var sourcePtr = IntPtr.Add(pointer, checked((int)(i * (uint)size)));
                var source = Marshal.PtrToStructure<NdiSource>(sourcePtr);

                var name = source.Name == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(source.Name) ?? "";
                var url = source.Url == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(source.Url) ?? "";

                if (!string.IsNullOrWhiteSpace(name))
                    result.Add(new NdiSourceInfo { Name = name, Url = url });
            }

            return result.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            Status = "NDI ERROR: " + ex.Message;
            return [];
        }
    }

    public bool StartReceiver(NdiSourceInfo source)
    {
        if (!Initialize() || _recvCreateV3 is null)
            return false;

        StopReceiver();

        IntPtr sourceName = IntPtr.Zero;
        IntPtr sourceUrl = IntPtr.Zero;
        IntPtr receiverName = IntPtr.Zero;

        try
        {
            sourceName = Marshal.StringToCoTaskMemUTF8(source.Name);
            if (!string.IsNullOrWhiteSpace(source.Url))
                sourceUrl = Marshal.StringToCoTaskMemUTF8(source.Url);

            receiverName = Marshal.StringToCoTaskMemUTF8("STUDIO V01 NDI INPUT");

            var settings = new NdiRecvCreate
            {
                Source = new NdiSource { Name = sourceName, Url = sourceUrl },
                ColorFormat = RecvColorBgrxBgra,
                Bandwidth = RecvBandwidthHighest,
                AllowVideoFields = false,
                NdiReceiverName = receiverName
            };

            _receiver = _recvCreateV3(ref settings);
            if (_receiver == IntPtr.Zero)
            {
                Status = "NDI: no se pudo crear el receptor";
                return false;
            }

            Status = "NDI INPUT: " + source.Name;
            return true;
        }
        catch (Exception ex)
        {
            Status = "NDI INPUT ERROR: " + ex.Message;
            if (_receiver != IntPtr.Zero)
            {
                try { _recvDestroy?.Invoke(_receiver); } catch { }
                _receiver = IntPtr.Zero;
            }
            return false;
        }
        finally
        {
            if (sourceName != IntPtr.Zero) Marshal.FreeCoTaskMem(sourceName);
            if (sourceUrl != IntPtr.Zero) Marshal.FreeCoTaskMem(sourceUrl);
            if (receiverName != IntPtr.Zero) Marshal.FreeCoTaskMem(receiverName);
        }
    }

    public void StopReceiver()
    {
        if (_receiver != IntPtr.Zero && _recvDestroy is not null)
        {
            try { _recvDestroy(_receiver); } catch { }
        }
        _receiver = IntPtr.Zero;
    }

    public bool TryCaptureBgrx(out byte[]? pixels, out int width, out int height, out int stride)
    {
        pixels = null;
        width = 0;
        height = 0;
        stride = 0;

        if (_receiver == IntPtr.Zero || _recvCaptureV3 is null || _recvFreeVideoV2 is null)
            return false;

        var frame = new NdiVideoFrame();

        try
        {
            var type = _recvCaptureV3(_receiver, ref frame, IntPtr.Zero, IntPtr.Zero, 100);
            if (type != FrameTypeVideo || frame.Data == IntPtr.Zero || frame.XRes <= 0 || frame.YRes <= 0)
                return false;

            width = frame.XRes;
            height = frame.YRes;
            var srcStride = frame.LineStride;
            if (srcStride == 0) srcStride = width * 4;

            stride = width * 4;
            pixels = new byte[stride * height];

            var absStride = Math.Abs(srcStride);
            var row = new byte[absStride];

            for (var y = 0; y < height; y++)
            {
                var sourceRow = srcStride >= 0
                    ? IntPtr.Add(frame.Data, y * srcStride)
                    : IntPtr.Add(frame.Data, (height - 1 - y) * absStride);

                Marshal.Copy(sourceRow, row, 0, Math.Min(absStride, row.Length));
                Buffer.BlockCopy(row, 0, pixels, y * stride, Math.Min(stride, row.Length));
            }

            return true;
        }
        finally
        {
            try { _recvFreeVideoV2(_receiver, ref frame); } catch { }
        }
    }

    public bool StartSender(string name)
    {
        if (!Initialize() || _sendCreate is null)
            return false;

        StopSender();

        try
        {
            _senderName = Marshal.StringToCoTaskMemUTF8(
                string.IsNullOrWhiteSpace(name) ? "STUDIO V01" : name.Trim());

            var settings = new NdiSendCreate
            {
                NdiName = _senderName,
                Groups = IntPtr.Zero,
                ClockVideo = true,
                ClockAudio = false
            };

            _sender = _sendCreate(ref settings);
            if (_sender == IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(_senderName);
                _senderName = IntPtr.Zero;
                Status = "NDI: no se pudo crear SALIDA GENERAL";
                return false;
            }

            Status = "NDI SALIDA: " + (name.Trim().Length > 0 ? name.Trim() : "STUDIO V01");
            return true;
        }
        catch (Exception ex)
        {
            Status = "NDI SALIDA ERROR: " + ex.Message;
            StopSender();
            return false;
        }
    }

    public void StopSender()
    {
        if (_sender != IntPtr.Zero && _sendDestroy is not null)
        {
            try { _sendDestroy(_sender); } catch { }
        }
        _sender = IntPtr.Zero;

        if (_senderName != IntPtr.Zero)
        {
            try { Marshal.FreeCoTaskMem(_senderName); } catch { }
            _senderName = IntPtr.Zero;
        }
    }

    public void SendBgrx(byte[] pixels, int width, int height, int stride, int frameRateN = 30000, int frameRateD = 1001)
    {
        if (_sender == IntPtr.Zero || _sendVideoV2 is null || pixels.Length < stride * height)
            return;

        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            var frame = new NdiVideoFrame
            {
                XRes = width,
                YRes = height,
                FourCC = FourCcBgrx,
                FrameRateN = frameRateN,
                FrameRateD = frameRateD,
                PictureAspectRatio = width / (float)height,
                FrameFormatType = FrameFormatProgressive,
                Timecode = 0,
                Data = handle.AddrOfPinnedObject(),
                LineStride = stride,
                Metadata = IntPtr.Zero,
                Timestamp = 0
            };

            _sendVideoV2(_sender, ref frame);
        }
        finally
        {
            handle.Free();
        }
    }

    private void EnsureFinder()
    {
        if (_finder != IntPtr.Zero || _findCreateV2 is null) return;

        var settings = new NdiFindCreate
        {
            ShowLocalSources = true,
            Groups = IntPtr.Zero,
            ExtraIps = IntPtr.Zero
        };

        _finder = _findCreateV2(ref settings);
    }

    private T? LoadDelegate<T>(string export) where T : Delegate
    {
        if (_library == IntPtr.Zero) return null;
        if (!NativeLibrary.TryGetExport(_library, export, out var address))
            return null;
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private static IEnumerable<string> GetLibraryCandidates()
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var normalized = path.Trim().Trim('"');
            if (Directory.Exists(normalized))
                normalized = Path.Combine(normalized, "Processing.NDI.Lib.x64.dll");
            if (seen.Add(normalized))
                result.Add(normalized);
        }

        Add(Environment.GetEnvironmentVariable("NDI_RUNTIME_DIR_V6"));
        Add(Environment.GetEnvironmentVariable("NDI_RUNTIME_DIR_V5"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NDI", "NDI 6 Runtime", "v6", "Processing.NDI.Lib.x64.dll"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NDI", "NDI 6 Tools", "Runtime", "Processing.NDI.Lib.x64.dll"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NDI", "NDI 5 Runtime", "v5", "Processing.NDI.Lib.x64.dll"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NDI", "NDI 5 Tools", "Runtime", "Processing.NDI.Lib.x64.dll"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NewTek", "NDI 5 Runtime", "v5", "Processing.NDI.Lib.x64.dll"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NewTek", "NDI 4 Runtime", "v4", "Processing.NDI.Lib.x64.dll"));
        Add(Path.Combine(AppContext.BaseDirectory, "Processing.NDI.Lib.x64.dll"));

        return result;
    }

    private void UnloadLibrary()
    {
        StopReceiver();
        StopSender();

        try
        {
            if (_finder != IntPtr.Zero && _findDestroy is not null)
                _findDestroy(_finder);
        }
        catch { }

        _finder = IntPtr.Zero;

        try
        {
            if (_initialized)
                _destroy?.Invoke();
        }
        catch { }

        _initialized = false;

        try
        {
            if (_library != IntPtr.Zero)
                NativeLibrary.Free(_library);
        }
        catch { }

        _library = IntPtr.Zero;
        _initialize = null;
        _destroy = null;
        _findCreateV2 = null;
        _findDestroy = null;
        _findWaitForSources = null;
        _findGetCurrentSources = null;
        _recvCreateV3 = null;
        _recvDestroy = null;
        _recvCaptureV3 = null;
        _recvFreeVideoV2 = null;
        _sendCreate = null;
        _sendDestroy = null;
        _sendVideoV2 = null;
    }

    public void Dispose()
    {
        UnloadLibrary();
        GC.SuppressFinalize(this);
    }
}
