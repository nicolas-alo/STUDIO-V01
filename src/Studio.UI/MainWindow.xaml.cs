using Microsoft.Web.WebView2.Wpf;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace Studio.UI;

public partial class MainWindow : Window
{
    private const int OutputWidth = 1920;
    private const int OutputHeight = 1080;
    private const string ClimateUrl = "http://192.168.33.100/~radier/overlays/clima_extendido.html";
    private const string LogoUrl = "http://192.168.33.100/~radier/overlays/moscasola.html";
    private const string NewsUrl = "http://192.168.33.100/~radier/overlays/noticias.html";

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
    private CancellationTokenSource? _quadCts;
    private Process? _ffmpeg;
    private readonly List<Process> _quadProcesses = [];
    private LocalConfig? _config;
    private int _index = -1;
    private bool _manualSelection;
    private readonly bool[] _climateEnabled = new bool[5];
    private readonly bool[] _logoEnabled = new bool[5];
    private readonly bool[] _newsEnabled = new bool[5];
    private readonly string _logPath = Path.Combine(AppContext.BaseDirectory, "studio.log");
    private WebView2? _audioPlayer;
    private WebView2? _htmlPreview;
    private WebView2? _climateOverlay;
    private WebView2? _logoOverlay;
    private WebView2? _newsOverlay;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        SizeChanged += MainWindow_SizeChanged;
        Closed += MainWindow_Closed;
        _cycleTimer.Tick += async (_, _) =>
        {
            if (!_manualSelection)
                await ShowNextAsync();
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Log("INICIO STUDIO V01 ETAPA 2 RECONSTRUIDA");
        StatusText.Text = "VENTANA ABIERTA — CARGANDO...";
        try
        {
            CreateWebViews();
        }
        catch (Exception ex)
        {
            Log("WEBVIEW2 NO DISPONIBLE: " + ex);
            StatusText.Text = "WEBVIEW2 NO DISPONIBLE — VIDEO RTSP CONTINÚA";
        }
        StatusText.Text = "CARGANDO CONFIGURACIÓN...";
        try
        {
            _config = await LoadConfigAsync();
            if (_config.Sources.Count < 6)
            {
                StatusText.Text = "CONFIGURACIÓN INVÁLIDA: se requieren 6 fuentes.";
                Log(StatusText.Text);
                return;
            }

            _cycleTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, _config.CycleSeconds));
            _cycleTimer.Start();

            // Cargar overlays una sola vez; al cambiar de cámara solo se muestran/ocultan.
            _ = InitializeOverlayWebViewsSafeAsync();
            await ShowNextAsync();

            // El reproductor M3U8 nunca debe impedir que abra la ventana.
            _ = InitializeAudioPlayerSafeAsync();
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
            throw new FileNotFoundException("Falta config.local.json junto al ejecutable.", path);

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<LocalConfig>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("config.local.json no es válido.");
    }

    private async Task ShowNextAsync()
    {
        if (_config is null) return;

        // Orden de aire: Puente → Puerto → Esquina → Edificio JJ → 4 Cámaras → Grilla → Placa de programas.
        _index = (_index + 1) % 7;
        await ShowCurrentAsync();
    }

    private async Task SelectSourceAsync(int slot)
    {
        if (_config is null) return;
        if (slot < 0 || slot > 6) return;

        _manualSelection = true;
        _index = slot;
        await ShowCurrentAsync();
    }

    private async Task ShowCurrentAsync()
    {
        if (_config is null) return;

        await StopCurrentAsync();
        UpdateAirCard();
        HideAllOverlays();

        if (_index == 4)
        {
            StatusText.Text = "4 CÁMARAS • 1920×1080 • 29.97 FPS";
            await ShowQuadAsync();
            ApplyActiveOverlays(4);
            return;
        }

        // Los HTML ocupan las posiciones 5 y 6 del ciclo, pero son las fuentes 4 y 5 del JSON.
        var sourceIndex = _index >= 5 ? _index - 1 : _index;
        var source = _config.Sources[sourceIndex];
        StatusText.Text = source.Name + " • " + source.Type.ToUpperInvariant() + " • 1920×1080 • 29.97 FPS";

        if (source.Type.Equals("HTML", StringComparison.OrdinalIgnoreCase))
            await ShowHtmlAsync(source.Url);
        else
            await ShowRtspAsync(source.Url);

        if (_index >= 0 && _index < 4)
            ApplyActiveOverlays(_index);
    }

    private async void SourceCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender == CardPuente) await SelectSourceAsync(0);
        else if (sender == CardPuerto) await SelectSourceAsync(1);
        else if (sender == CardEsquina) await SelectSourceAsync(2);
        else if (sender == CardEdificio) await SelectSourceAsync(3);
        else if (sender == CardQuad) await SelectSourceAsync(4);
        else if (sender == CardGrilla) await SelectSourceAsync(5);
        else if (sender == CardProgramas) await SelectSourceAsync(6);
    }

    private void UpdateAirCard()
    {
        var cards = new[] { CardPuente, CardPuerto, CardEsquina, CardEdificio, CardQuad, CardGrilla, CardProgramas };
        foreach (var card in cards)
        {
            card.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(32, 32, 32));
            card.BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(48, 48, 48));
        }

        var active = _index >= 0 && _index <= 6 ? _index : -1;
        if (active >= 0)
        {
            cards[active].Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(125, 18, 18));
            cards[active].BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(230, 45, 45));
            AirStatusText.Text = "AL AIRE: " + (active switch
            {
                0 => "PUENTE",
                1 => "PUERTO",
                2 => "ESQUINA",
                3 => "EDIFICIO JJ",
                4 => "4 CÁMARAS",
                5 => "GRILLA",
                6 => "PLACA DE PROGRAMAS",
                _ => "—"
            });
        }
        else
        {
            AirStatusText.Text = "AL AIRE: " + (_index == 5 ? "GRILLA" : _index == 6 ? "PLACA DE PROGRAMAS" : "—");
        }
    }

    private void OverlayToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button || button.Tag is not string tag) return;
        var parts = tag.Split('|');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var slot) || slot < 0 || slot > 4) return;

        var enabled = button.IsChecked == true;
        switch (parts[1])
        {
            case "climate": _climateEnabled[slot] = enabled; break;
            case "logo": _logoEnabled[slot] = enabled; break;
            case "news": _newsEnabled[slot] = enabled; break;
        }

        if (GetActiveOverlaySlot() == slot)
            _ = ApplyActiveOverlaysAsync(slot);
    }

    private int GetActiveOverlaySlot() =>
        _index >= 0 && _index <= 4 ? _index : -1;

    private void HideAllOverlays()
    {
        ClimateHost.Visibility = Visibility.Collapsed;
        LogoHost.Visibility = Visibility.Collapsed;
        NewsHost.Visibility = Visibility.Collapsed;
    }

    private void ApplyActiveOverlays(int slot) => _ = ApplyActiveOverlaysAsync(slot);

    private async Task ApplyActiveOverlaysAsync(int slot)
    {
        HideAllOverlays();
        // Los WebView2 quedan cargados: no se vuelve a navegar al cambiar de cámara.
        if (_climateEnabled[slot] && _climateOverlay is not null)
            ClimateHost.Visibility = Visibility.Visible;

        if (_logoEnabled[slot] && _logoOverlay is not null)
            LogoHost.Visibility = Visibility.Visible;

        if (_newsEnabled[slot] && _newsOverlay is not null)
            NewsHost.Visibility = Visibility.Visible;

        await Task.CompletedTask;
    }

    private async Task InitializeOverlayWebViewsSafeAsync()
    {
        try
        {
            await InitializeOverlayWebViewAsync(_climateOverlay, ClimateUrl);
            await InitializeOverlayWebViewAsync(_logoOverlay, LogoUrl);
            await InitializeOverlayWebViewAsync(_newsOverlay, NewsUrl);
        }
        catch (Exception ex)
        {
            Log("OVERLAYS ERROR: " + ex);
        }

        var slot = GetActiveOverlaySlot();
        if (slot >= 0)
            await ApplyActiveOverlaysAsync(slot);
    }

    private async Task InitializeOverlayWebViewAsync(WebView2? view, string url)
    {
        if (view is null) return;

        await view.EnsureCoreWebView2Async();
        view.DefaultBackgroundColor = System.Drawing.Color.Transparent;

        await view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
            (() => {
                const style = document.createElement('style');
                style.textContent = 'html,body{background:transparent!important;background-color:transparent!important;margin:0!important;}';
                document.documentElement.appendChild(style);
            })();
        ");

        view.CoreWebView2.Navigate(url);
    }

    private void CreateWebViews()
    {
        if (_audioPlayer is null)
        {
            _audioPlayer = new WebView2();
            AudioHost.Children.Add(_audioPlayer);
        }
        if (_htmlPreview is null)
        {
            _htmlPreview = new WebView2();
            HtmlHost.Children.Add(_htmlPreview);
        }
        if (_climateOverlay is null)
        {
            _climateOverlay = new WebView2 { IsHitTestVisible = false };
            ClimateHost.Children.Add(_climateOverlay);
        }
        if (_logoOverlay is null)
        {
            _logoOverlay = new WebView2 { IsHitTestVisible = false };
            LogoHost.Children.Add(_logoOverlay);
        }
        if (_newsOverlay is null)
        {
            _newsOverlay = new WebView2 { IsHitTestVisible = false };
            NewsHost.Children.Add(_newsOverlay);
        }
    }

    private async Task InitializeAudioPlayerSafeAsync()
    {
        try
        {
            if (_audioPlayer is null) return;
            await _audioPlayer.EnsureCoreWebView2Async();
            _audioPlayer.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 11, 11, 11);

            var configured = _config?.M3u8Url?.Trim() ?? "";
            var jsonUrl = JsonSerializer.Serialize(configured);
            var html = @"<!doctype html><html><body style='margin:0;background:#0b0b0b;color:#fff;font-family:Arial;overflow:hidden'>
<div style='padding:4px'><audio id='a' controls style='width:100%'></audio><div id='s' style='font-size:11px;color:#aaa'>M3U8 no configurado</div></div>
<script src='https://cdn.jsdelivr.net/npm/hls.js@latest'></script><script>
const url=__M3U8__; const a=document.getElementById('a'); const s=document.getElementById('s');
if(url){s.textContent='M3U8 conectado'; if(a.canPlayType('application/vnd.apple.mpegurl')){a.src=url;} else if(window.Hls&&Hls.isSupported()){let h=new Hls();h.loadSource(url);h.attachMedia(a);}}
</script></body></html>";
            _audioPlayer.CoreWebView2.NavigateToString(html.Replace("__M3U8__", jsonUrl));
        }
        catch (Exception ex)
        {
            Log("M3U8/WebView2 ERROR: " + ex);
        }
    }

    private async Task ShowHtmlAsync(string url)
    {
        PreviewImage.Visibility = Visibility.Collapsed;
        QuadPreview.Visibility = Visibility.Collapsed;
        HtmlHost.Visibility = Visibility.Visible;

        try
        {
            if (_htmlPreview is null) return;
            await _htmlPreview.EnsureCoreWebView2Async();
            _htmlPreview.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            await _htmlPreview.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
                (() => {
                    const style = document.createElement('style');
                    style.textContent = 'html,body{background:transparent!important;background-color:transparent!important;margin:0!important;}';
                    document.documentElement.appendChild(style);
                })();
            ");
            _htmlPreview.ZoomFactor = CalculateWebZoom();
            _htmlPreview.Source = new Uri(url);
        }
        catch (Exception ex)
        {
            StatusText.Text = "HTML ERROR: " + ex.Message;
            Log("HTML ERROR: " + ex);
        }
    }

    private async Task ShowQuadAsync()
    {
        HtmlHost.Visibility = Visibility.Collapsed;
        PreviewImage.Visibility = Visibility.Collapsed;
        QuadPreview.Visibility = Visibility.Visible;

        var sources = _config?.Sources
            .Where(s => s.Type.Equals("RTSP", StringComparison.OrdinalIgnoreCase))
            .Take(4).ToList();

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

        _quadCts?.Cancel();
        _quadCts?.Dispose();
        _quadCts = new CancellationTokenSource();
        var token = _quadCts.Token;

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
            using var process = Process.Start(psi);
            if (process is null) return;
            lock (_quadProcesses) _quadProcesses.Add(process);

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
                var bitmap = BitmapSource.Create(width, height, 96, 96,
                    System.Windows.Media.PixelFormats.Bgr24, null, frame, width * bpp);
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
        catch (Exception ex) { Log($"4CAM[{slot + 1}] ERROR: {ex}"); }
    }

    private async Task ShowRtspAsync(string url)
    {
        HtmlHost.Visibility = Visibility.Collapsed;
        QuadPreview.Visibility = Visibility.Collapsed;
        PreviewImage.Visibility = Visibility.Visible;

        var ffmpegPath = FindFfmpeg();
        if (ffmpegPath is null)
        {
            StatusText.Text = "FFMPEG NO ENCONTRADO";
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
        psi.ArgumentList.Add("-vf"); psi.ArgumentList.Add("scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:(oh-ih)*0+((ow-iw)/2):(oh-ih)/2,fps=30000/1001");
        psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("bgr24");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("rawvideo");
        psi.ArgumentList.Add("pipe:1");

        try
        {
            Log("INICIANDO FFMPEG: " + url);
            _ffmpeg = Process.Start(psi);
            if (_ffmpeg is null) { StatusText.Text = "NO SE PUDO INICIAR FFMPEG."; return; }

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
        const int bpp = 3;
        var frameSize = OutputWidth * OutputHeight * bpp;
        var buffer = new byte[frameSize];

        try
        {
            var stream = process.StandardOutput.BaseStream;
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
                var bitmap = BitmapSource.Create(OutputWidth, OutputHeight, 96, 96,
                    System.Windows.Media.PixelFormats.Bgr24, null, frame, OutputWidth * bpp);
                bitmap.Freeze();

                await Dispatcher.InvokeAsync(() =>
                {
                    if (!token.IsCancellationRequested) PreviewImage.Source = bitmap;
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log("LECTURA VIDEO ERROR: " + ex); }

        if (!token.IsCancellationRequested)
            await Dispatcher.InvokeAsync(() => StatusText.Text = "SIN VIDEO RTSP — revisar studio.log");
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

    private double CalculateWebZoom() => 1.0;

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ResizeProgramPreview();
    }

    private void PreviewContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ResizeProgramPreview();
    }

    private void ResizeProgramPreview()
    {
        var availableWidth = Math.Max(1, PreviewContainer.ActualWidth);
        var availableHeight = Math.Max(1, PreviewContainer.ActualHeight);

        // El programa sigue siendo conceptualmente 1920x1080.
        // Solo el RECTÁNGULO DEL PREVIEW cambia de tamaño. No se usa RenderTransform
        // porque WebView2 es un control nativo y no debe quedar a otra escala.
        var scale = Math.Min(
            availableWidth / (double)OutputWidth,
            availableHeight / (double)OutputHeight);

        var width = Math.Max(1, OutputWidth * scale);
        var height = Math.Max(1, OutputHeight * scale);

        ProgramCanvas.Width = width;
        ProgramCanvas.Height = height;

        // WebView2 recibe el mismo factor de preview. Con zoom proporcional,
        // una página diseñada en 1920x1080 conserva su composición y solamente
        // se reduce visualmente para entrar en el visor.
        var webZoom = Math.Max(0.25, Math.Min(1.0, scale));

        if (_htmlPreview is not null)
            _htmlPreview.ZoomFactor = webZoom;
        if (_climateOverlay is not null)
            _climateOverlay.ZoomFactor = webZoom;
        if (_logoOverlay is not null)
            _logoOverlay.ZoomFactor = webZoom;
        if (_newsOverlay is not null)
            _newsOverlay.ZoomFactor = webZoom;
    }

    private void Log(string message)
    {
        try { File.AppendAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}"); }
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
        return null;
    }

    private async Task StopCurrentAsync()
    {
        _sourceCts?.Cancel();
        _sourceCts?.Dispose();
        _sourceCts = null;

        _quadCts?.Cancel();
        _quadCts?.Dispose();
        _quadCts = null;

        List<Process> processes;
        lock (_quadProcesses) processes = _quadProcesses.ToList();
        foreach (var process in processes)
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            try { process.Dispose(); } catch { }
        }
        lock (_quadProcesses) _quadProcesses.Clear();

        foreach (var image in new[] { QuadImage0, QuadImage1, QuadImage2, QuadImage3 })
        {
            image.Source = null;
            image.Visibility = Visibility.Collapsed;
        }

        if (_ffmpeg is not null)
        {
            try { if (!_ffmpeg.HasExited) _ffmpeg.Kill(true); } catch { }
            try { await _ffmpeg.WaitForExitAsync(); } catch { }
            try { _ffmpeg.Dispose(); } catch { }
            _ffmpeg = null;
        }
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        _cycleTimer.Stop();
        await StopCurrentAsync();
    }
}