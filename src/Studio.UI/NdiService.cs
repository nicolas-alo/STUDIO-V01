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
    [StructLayout(LayoutKind.Sequential)]
    private struct NdiSource
    {
        public IntPtr Name;
        public IntPtr Url;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NdiFindCreate
    {
        [MarshalAs(UnmanagedType.I1)]
        public bool ShowLocalSources;
        public IntPtr Groups;
        public IntPtr ExtraIps;
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

    private IntPtr _library;
    private IntPtr _finder;
    private InitializeDelegate? _initialize;
    private DestroyDelegate? _destroy;
    private FindCreateV2Delegate? _findCreateV2;
    private FindDestroyDelegate? _findDestroy;
    private FindWaitForSourcesDelegate? _findWaitForSources;
    private FindGetCurrentSourcesDelegate? _findGetCurrentSources;
    private bool _initialized;

    public string? RuntimePath { get; private set; }
    public string Status { get; private set; } = "NDI no inicializado";

    public bool IsAvailable => _initialized && _finder != IntPtr.Zero;

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

                if (_initialize is null || _destroy is null ||
                    _findCreateV2 is null || _findDestroy is null ||
                    _findWaitForSources is null || _findGetCurrentSources is null)
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

                var name = source.Name == IntPtr.Zero
                    ? ""
                    : Marshal.PtrToStringUTF8(source.Name) ?? "";

                var url = source.Url == IntPtr.Zero
                    ? ""
                    : Marshal.PtrToStringUTF8(source.Url) ?? "";

                if (!string.IsNullOrWhiteSpace(name))
                    result.Add(new NdiSourceInfo { Name = name, Url = url });
            }

            return result
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Status = "NDI ERROR: " + ex.Message;
            return [];
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
        var yieldDummy = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var normalized = path.Trim().Trim('"');
            if (Directory.Exists(normalized))
                normalized = Path.Combine(normalized, "Processing.NDI.Lib.x64.dll");

            if (seen.Add(normalized))
                yieldDummy.Add(normalized);
        }

        Add(Environment.GetEnvironmentVariable("NDI_RUNTIME_DIR_V6"));
        Add(Environment.GetEnvironmentVariable("NDI_RUNTIME_DIR_V5"));

        Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NDI", "NDI 6 Runtime", "v6", "Processing.NDI.Lib.x64.dll"));

        Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NDI", "NDI 6 Tools", "Runtime", "Processing.NDI.Lib.x64.dll"));

        Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NDI", "NDI 5 Runtime", "v5", "Processing.NDI.Lib.x64.dll"));

        Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NDI", "NDI 5 Tools", "Runtime", "Processing.NDI.Lib.x64.dll"));

        Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NewTek", "NDI 5 Runtime", "v5", "Processing.NDI.Lib.x64.dll"));

        Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NewTek", "NDI 4 Runtime", "v4", "Processing.NDI.Lib.x64.dll"));

        Add(Path.Combine(AppContext.BaseDirectory, "Processing.NDI.Lib.x64.dll"));

        return yieldDummy;
    }

    private void UnloadLibrary()
    {
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
    }

    public void Dispose()
    {
        UnloadLibrary();
        GC.SuppressFinalize(this);
    }
}
