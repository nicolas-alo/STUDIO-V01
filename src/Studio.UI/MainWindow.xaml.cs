using Microsoft.Web.WebView2.Core;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace Studio.UI;

public partial class MainWindow : Window
{
    private const int OutputWidth = 1920;
    private const int OutputHeight = 1080;

    private sealed class LocalConfig
    {
        public int CycleSeconds { get; set; } = 20;
        public string M3u8Url { get; set; } = "";
        public List<Source> Sources { get; set; } = [];
    }

    private sealed class Source
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public string Url { get; set; } = "";
    }

    private readonly DispatcherTimer _cycleTimer = new();
    private CancellationTokenSource? _sourceCts;
    private Process? _ffmpeg;
    private readonly List<Process> _quadProcesses = [];
    private readonly CancellationTokenSource _quadLifetime = new();
    private LocalConfig? _config;
    private int _index = -1;
    private readonly bool[] _clockEnabled = new bool[5];
    private readonly bool[] _climateEnabled = new bool[5];
    private readonly bool[] _logoEnabled = new bool[5];
    private readonly DispatcherTimer _clockTimer = new();
    private const string ClimateUrl = "http://192.168.33.100/~radier/overlays/clima_extendido.html";
    private const string LogoUrl = "http://192.168.33.100/~radier/overlays/moscasola.html";
    private readonly string _logPath = Path.Combine(AppContext.BaseDirectory, "studio.log");

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        SizeChanged += MainWindow_SizeChanged;
        Closed += MainWindow_Closed;
        _cycleTimer.Tick += async (_, _) => await ShowNextAsync();
        _clockTimer.Interval = TimeSpan.FromSeconds(1);
        _clockTimer.Tick += (_, _) => ClockOverlay.Text = DateTime.Now.ToString("HH:mm:ss");
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Log("INICIO STUDIO V01 ETAPA 1");
            _config = await LoadConfigAsync();
            if (_config.Sources.Count < 6)
            {
                StatusText.Text = "CONFIGURACIÓN INVÁLIDA: faltan las 6 fuentes base.";
                return;
            }

            _cycleTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, _config.CycleSeconds));
            _cycleTimer.Start();
            _clockTimer.Start();
            await InitializeAudioPlayerAsync();
            await ShowNextAsync();
        }
        catch (Exception ex)
        {
            Log("ERROR DE INICIO: " + ex);
            StatusText.Text = "ERROR DE INICIO: " + ex.Message;
        }
    }

    private static async Task<LocalConfig> LoadConfigAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "config.local.json");
        if (!File.Exists(path))
            throw new FileNotFoundException(
                "Falta config.local.json junto al ejecutable. Copiá config.example.json y completalo.",
                path);

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<LocalConfig>(
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("config.local.json no es válido.");
    }

    private async Task ShowNextAsync()
    {
        if (_config is null) return;

        var totalSources = _config.Sources.Count + 1; // +1 = fuente virtual 4 CÁMARAS
        _index = (_index + 1) % totalSources;

        await StopCurrentAsync();
        UpdateAirCard();
        HideAllOverlays();

        if (_index == _config.Sources.Count)
        {
            StatusText.Text = "4 CÁMARAS • 1920×1080 • 29.97 FPS";
            await ShowQuadAsync();
            ApplyActiveOverlays(4);
            return;
        }

        var source = _config.Sources[_index];
        StatusText.Text = source.Name + " • " + source.Type.ToUpperInvariant() + " • 1920×1080 • 29.97 FPS";

        if (source.Type.Equals("HTML", StringComparison.OrdinalIgnoreCase))
            await ShowHtmlAsync(source.Url);
        else
            await ShowRtspAsync(source.Url);

        if (_index >= 0 && _index < 4)
            ApplyActiveOverlays(_index);
    }


    private void UpdateAirCard()
    {
        var cards = new[] { CardPuente, CardPuerto, CardEsquina, CardEdificio, CardQuad };
        foreach (var card in cards)
        {
            card.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(32,32,32));
            card.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(48,48,48));
        }

        var active = _index switch
        {
            0 => 0, 1 => 1, 2 => 2, 3 => 3,
            var x when _config is not null && x == _config.Sources.Count => 4,
            _ => -1
        };

        if (active >= 0)
        {
            cards[active].Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(125,18,18));
            cards[active].BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(230,45,45));
            AirStatusText.Text = active switch
            {
                0 => "AL AIRE: PUENTE", 1 => "AL AIRE: PUERTO", 2 => "AL AIRE: ESQUINA",
                3 => "AL AIRE: EDIFICIO JJ", 4 => "AL AIRE: 4 CÁMARAS", _ => "AL AIRE: —"
            };
        }
        else
            AirStatusText.Text = _index == 4 ? "AL AIRE: GRILLA" : _index == 5 ? "AL AIRE: PROMO" : "AL AIRE: —";
    }

    private void OverlayToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button || button.Tag is not string tag) return;
        var parts = tag.Split('|');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var slot) || slot < 0 || slot > 4) return;

        var enabled = button.IsChecked == true;
        switch (parts[1])
        {
            case "clock": _clockEnabled[slot] = enabled; break;
            case "climate": _climateEnabled[slot] = enabled; break;
            case "logo": _logoEnabled[slot] = enabled; break;
        }

        if (GetActiveOverlaySlot() == slot)
            _ = ApplyActiveOverlaysAsync(slot);
    }

    private int GetActiveOverlaySlot()
    {
        if (_config is null) return -1;
        if (_index >= 0 && _index < 4) return _index;
        if (_index == _config.Sources.Count) return 4;
        return -1;
    }

    private void HideAllOverlays()
    {
        ClockOverlay.Visibility = Visibility.Collapsed;
        ClimateOverlay.Visibility = Visibility.Collapsed;
        LogoOverlay.Visibility = Visibility.Collapsed;
    }

    private void ApplyActiveOverlays(int slot) => _ = ApplyActiveOverlaysAsync(slot);

    private async Task ApplyActiveOverlaysAsync(int slot)
    {
        HideAllOverlays();
        ClockOverlay.Text = DateTime.Now.ToString("HH:mm:ss");
        if (_clockEnabled[slot]) ClockOverlay.Visibility = Visibility.Visible;

        if (_climateEnabled[slot])
        {
            await ShowTransparentWebViewAsync(ClimateOverlay, ClimateUrl);
            ClimateOverlay.Visibility = Visibility.Visible;
        }

        if (_logoEnabled[slot])
        {
            await ShowTransparentWebViewAsync(LogoOverlay, LogoUrl);
            LogoOverlay.Visibility = Visibility.Visible;
        }
    }

    private static async Task ShowTransparentWebViewAsync(Microsoft.Web.WebView2.Wpf.WebView2 view, string url)
    {
        await view.EnsureCoreWebView2Async();
        view.CoreWebView2Controller!.DefaultBackgroundColor = System.Drawing.Color.Transparent;
        view.CoreWebView2.Navigate(url);
    }

    private async Task InitializeAudioPlayerAsync()
    {
        await AudioPlayer.EnsureCoreWebView2Async();
        AudioPlayer.CoreWebView2Controller!.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255,11,11,11);

        var configured = _config?.M3u8Url ?? "";
        var encoded = System.Net.WebUtility.HtmlEncode(configured);
        var html = @"<!doctype html><html><body style='margin:0;background:#0b0b0b;color:#fff;font-family:Arial;overflow:hidden'>
<div style='padding:4px'><audio id='a' controls style='width:100%'></audio><div id='s' style='font-size:11px;color:#aaa'>M3U8 no configurado</div></div>
<script src='https://cdn.jsdelivr.net/npm/hls.js@latest'></script><script>
const url='__M3U8__'; const a=document.getElementById('a'); const s=document.getElementById('s');
if(url){s.textContent='M3U8 conectado'; if(a.canPlayType('application/vnd.apple.mpegurl')){a.src=url;} else if(window.Hls&&Hls.isSupported()){let h=new Hls();h.loadSource(url);h.attachMedia(a);}}
</script></body></html>";
        AudioPlayer.CoreWebView2.NavigateToString(html.Replace("__M3U8__", encoded));
    }

    private async Task ShowHtmlAsync(string url)
    {
        PreviewImage.Visibility = Visibility.Collapsed;
        QuadPreview.Visibility = Visibility.Collapsed;
        HtmlPreview.Visibility = Visibility.Visible;

        try
        {
            await HtmlPreview.EnsureCoreWebView2Async();
            HtmlPreview.ZoomFactor = CalculateWebZoom();
            HtmlPreview.Source = new Uri(url);
        }
        catch (Exception ex)
        {
            StatusText.Text = "HTML ERROR: " + ex.Message;
        }
    }

    private async Task ShowQuadAsync()
    {
        HtmlPreview.Visibility = Visibility.Collapsed;
        PreviewImage.Visibility = Visibility.Collapsed;
        QuadPreview.Visibility = Visibility.Visible;

        var sources = _config?.Sources.Where(s => s.Type.Equals("RTSP", StringComparison.OrdinalIgnoreCase)).Take(4).ToList();
        if (sources is null || sources.Count < 4)
        {
            StatusText.Text = "FALTAN CÁMARAS RTSP";
            return;
        }

        var images = new[] { QuadImage0, QuadImage1, QuadImage2, QuadImage3 };
        foreach (var image in images)
        {
            image.Source = null;
            image.Visibility = Visibility.Collapsed;
        }

        _quadProcesses.Clear();
        var token = _quadLifetime.Token;
        var tasks = sources.Select((source, i) => ReadQuadCameraAsync(source, images[i], i, token)).ToArray();
        await Task.WhenAll(tasks);

        var active = images.Count(i => i.Visibility == Visibility.Visible);
        StatusText.Text = $"4 CÁMARAS • {active} ACTIVAS • 1920×1080 • 29.97 FPS";
    }

    private async Task ReadQuadCameraAsync(Source source, System.Windows.Controls.Image target, int slot, CancellationToken token)
    {
        var ffmpegPath = FindFfmpeg();
        if (ffmpegPath is null) return;

        var psi = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-rtsp_transport"); psi.ArgumentList.Add("tcp");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(source.Url);
        psi.ArgumentList.Add("-an");
        psi.ArgumentList.Add("-vf"); psi.ArgumentList.Add("scale=960:540:force_original_aspect_ratio=decrease,pad=960:540:(ow-iw)/2:(oh-ih)/2,fps=30000/1001");
        psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("bgr24");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("rawvideo");
        psi.ArgumentList.Add("pipe:1");

        try
        {
            Log($"4CAM[{slot + 1}] INICIANDO: {source.Url}");
            using var process = Process.Start(psi);
            if (process is null) return;
            _quadProcesses.Add(process);

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        var line = await process.StandardError.ReadLineAsync(token);
                        if (line is null) break;
                        if (!string.IsNullOrWhiteSpace(line)) Log($"4CAM[{slot + 1}] FFMPEG: {line}");
                    }
                }
                catch { }
            }, token);

            const int width = 960, height = 540, bpp = 3;
            var frameSize = width * height * bpp;
            var buffer = new byte[frameSize];
            var stream = process.StandardOutput.BaseStream;
            var firstFrame = true;

            while (!token.IsCancellationRequested && !process.HasExited)
            {
                var read = 0;
                while (read < frameSize && !token.IsCancellationRequested)
                {
                    var count = await stream.ReadAsync(buffer.AsMemory(read, frameSize - read), token);
                    if (count == 0) return;
                    read += count;
                }
                if (read != frameSize) return;

                var frame = new byte[frameSize];
                Buffer.BlockCopy(buffer, 0, frame, 0, frameSize);
                var bitmap = BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgr24, null, frame, width * bpp);
                bitmap.Freeze();

                await Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested) return;
                    target.Source = bitmap;
                    target.Visibility = Visibility.Visible;
                    if (firstFrame)
                    {
                        firstFrame = false;
                        StatusText.Text = "4 CÁMARAS • RECIBIENDO SEÑAL...";
                    }
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log($"4CAM[{slot + 1}] ERROR: {ex.Message}");
        }
    }

    private async Task ShowRtspAsync(string url)
    {
        HtmlPreview.Visibility = Visibility.Collapsed;
        QuadPreview.Visibility = Visibility.Collapsed;
        PreviewImage.Visibility = Visibility.Visible;

        var ffmpegPath = FindFfmpeg();
        if (ffmpegPath is null)
        {
            StatusText.Text = "FFMPEG NO ENCONTRADO: colocá ffmpeg.exe en tools.";
            return;
        }

        _sourceCts = new CancellationTokenSource();
        var token = _sourceCts.Token;

        var psi = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-rtsp_transport"); psi.ArgumentList.Add("tcp");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(url);
        psi.ArgumentList.Add("-an");
        psi.ArgumentList.Add("-vf");
        psi.ArgumentList.Add("scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2,fps=30000/1001");
        psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("bgr24");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("rawvideo");
        psi.ArgumentList.Add("pipe:1");

        try
        {
            Log("INICIANDO FFMPEG: " + url);
            _ffmpeg = Process.Start(psi);
            if (_ffmpeg is null)
            {
                StatusText.Text = "NO SE PUDO INICIAR FFMPEG.";
                return;
            }

            _ = Task.Run(() => ReadFramesAsync(_ffmpeg, token), token);
            _ = Task.Run(() => ReadFfmpegErrorsAsync(_ffmpeg, token), token);
        }
        catch (Exception ex)
        {
            Log("FFMPEG ERROR: " + ex);
            StatusText.Text = "FFMPEG ERROR: " + ex.Message;
        }
    }

    private async Task ReadFramesAsync(Process process, CancellationToken token)
    {
        const int bytesPerPixel = 3;
        var frameSize = OutputWidth * OutputHeight * bytesPerPixel;
        var buffer = new byte[frameSize];

        try
        {
            var stream = process.StandardOutput.BaseStream;

            while (!token.IsCancellationRequested && !process.HasExited)
            {
                var read = 0;
                while (read < frameSize && !token.IsCancellationRequested)
                {
                    var count = await stream.ReadAsync(
                        buffer.AsMemory(read, frameSize - read), token);

                    if (count == 0) return;
                    read += count;
                }

                if (read != frameSize) return;

                var frame = new byte[frameSize];
                Buffer.BlockCopy(buffer, 0, frame, 0, frameSize);

                await Dispatcher.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested) return;

                    var bitmap = BitmapSource.Create(
                        OutputWidth, OutputHeight, 96, 96,
                        System.Windows.Media.PixelFormats.Bgr24,
                        null, frame, OutputWidth * bytesPerPixel);

                    bitmap.Freeze();
                    PreviewImage.Source = bitmap;
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log("LECTURA VIDEO ERROR: " + ex);
        }

        if (!token.IsCancellationRequested)
        {
            await Dispatcher.InvokeAsync(() => StatusText.Text = "SIN VIDEO RTSP — revisar studio.log");
        }
    }

    private async Task ReadFfmpegErrorsAsync(Process process, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && !process.HasExited)
            {
                var line = await process.StandardError.ReadLineAsync();
                if (line is null) break;
                if (!string.IsNullOrWhiteSpace(line)) Log("FFMPEG: " + line);
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) Log("STDERR ERROR: " + ex.Message);
        }
    }

    private double CalculateWebZoom()
    {
        var w = Math.Max(320, ActualWidth - 16);
        var h = Math.Max(240, ActualHeight - 48);
        return Math.Min(w / OutputWidth, h / OutputHeight);
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (HtmlPreview.CoreWebView2 is not null)
            HtmlPreview.ZoomFactor = CalculateWebZoom();
    }

    private void Log(string message)
    {
        try
        {
            File.AppendAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    private static string? FindFfmpeg()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            Path.Combine(Environment.CurrentDirectory, "tools", "ffmpeg.exe")
        };

        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return candidate;

        return "ffmpeg";
    }

    private async Task StopCurrentAsync()
    {
        _sourceCts?.Cancel();
        _sourceCts?.Dispose();
        _sourceCts = null;

        foreach (var process in _quadProcesses.ToList())
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            try { process.Dispose(); } catch { }
        }
        _quadProcesses.Clear();
        foreach (var image in new[] { QuadImage0, QuadImage1, QuadImage2, QuadImage3 })
        {
            image.Source = null;
            image.Visibility = Visibility.Collapsed;
        }

        if (_ffmpeg is not null)
        {
            try { if (!_ffmpeg.HasExited) _ffmpeg.Kill(true); } catch { }
            try { await _ffmpeg.WaitForExitAsync(); } catch { }
            _ffmpeg.Dispose();
            _ffmpeg = null;
        }
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        _cycleTimer.Stop();
        _clockTimer.Stop();
        await StopCurrentAsync();
        _quadLifetime.Cancel();
    }
}
