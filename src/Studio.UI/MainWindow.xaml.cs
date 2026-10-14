using Microsoft.Web.WebView2.Wpf;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;
using SharpCompress.Archives;
using SharpCompress.Common;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

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
        public string SrtHost { get; set; } = "";
        public int SrtPort { get; set; } = 9000;
        public string SrtLocalIp { get; set; } = "";
        public string YoutubeServer { get; set; } = "rtmps://a.rtmps.youtube.com/live2";
        public string YoutubeKey { get; set; } = "";
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
    private CancellationTokenSource? _ndiCts;
    private Process? _ffmpeg;
    private readonly List<Process> _quadProcesses = [];
    private LocalConfig? _config;
    private int _index = -1;
    private bool _manualSelection;
    private volatile bool _quadProgramActive;
    private volatile bool _htmlProgramActive;
    private readonly bool[] _climateEnabled = new bool[5];
    private readonly bool[] _logoEnabled = new bool[5];
    private readonly bool[] _newsEnabled = new bool[5];
    private readonly string _logPath = Path.Combine(AppContext.BaseDirectory, "studio.log");
    private WebView2? _audioPlayer;
    private WebView2? _htmlPreview;
    private WebView2? _htmlPreview2;
    private WebView2? _climateOverlay;
    private WebView2? _logoOverlay;
    private WebView2? _newsOverlay;
    private readonly NdiService _ndiService = new();
    private readonly object _ndiOutputLock = new();
    private readonly byte[]?[] _quadNdiFrames = new byte[4][];
    private bool _ndiInputOnAir;
    private readonly StreamOutputService _srtOutput = new();
    private readonly StreamOutputService _rtmpOutput = new();
    private bool _srtFirstFrameReported;
    private bool _rtmpFirstFrameReported;
    private readonly object _overlayFrameLock = new();
    private readonly object _overlayStateLock = new();
    private byte[]? _climateFrame;
    private byte[]? _logoFrame;
    private byte[]? _newsFrame;
    private CancellationTokenSource? _htmlCaptureCts;
    private WebView2? _htmlCaptureView;

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
        Log("INICIO STUDIO V01 ETAPA 3 — NDI");
        // Preview y NDI consumen la misma composición publicada, con workers
        // desacoplados de los lectores de cámara.
        _ = Task.Run(() => PreviewOutputPumpAsync(_ndiPumpCts.Token));
        _ = Task.Run(() => NdiOutputPumpAsync(_ndiPumpCts.Token));
        StatusText.Text = "VENTANA ABIERTA — CARGANDO...";
        try
        {
            CreateWebViews();
            // Fija el primer layout antes de iniciar las capturas HTML; evita
            // que el primer frame use las dimensiones transitorias del arranque.
            await Dispatcher.InvokeAsync(() => ResizeProgramPreview(), DispatcherPriority.Loaded);
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

            LoadOutputSettings();
            await InitializeHtmlProgramViewsAsync();
            _cycleTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, _config.CycleSeconds));
            _cycleTimer.Start();

            // Cargar overlays una sola vez; al cambiar de cámara solo se muestran/ocultan.
            _ = InitializeOverlayWebViewsSafeAsync();
            _ = InitializeNdiSafeAsync();
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
        lock (_overlayStateLock) _index = (_index + 1) % 7;
        await ShowCurrentAsync();
    }

    private async Task SelectSourceAsync(int slot)
    {
        if (_config is null) return;
        if (slot < 0 || slot > 6) return;

        // La selección manual cambia el plano actual, pero no desactiva el ciclo automático de 20 s.
        _manualSelection = false;
        lock (_overlayStateLock) _index = slot;
        await ShowCurrentAsync();
    }

    private async Task ShowCurrentAsync()
    {
        if (_config is null) return;

        Interlocked.Increment(ref _activeOverlayGeneration);
        // Al cambiar de fuente, descartar inmediatamente cualquier captura HTML
        // en vuelo para no publicar un fotograma de la página anterior.
        _htmlProgramActive = false;
        _quadProgramActive = false;
        await StopCurrentAsync();
        UpdateAirCard();
        int selectedSlot;
        lock (_overlayStateLock) selectedSlot = _index;
        SyncOverlayToggleButtons(selectedSlot >= 0 && selectedSlot < 5 ? selectedSlot : -1);
        HideAllOverlays();
        lock (_overlayFrameLock)
        {
            _climateFrame = null;
            _logoFrame = null;
            _newsFrame = null;
        }

        if (selectedSlot == 4)
        {
            StatusText.Text = "4 CÁMARAS • 1920×1080 • 29.97 FPS";
            await ShowQuadAsync();
            // La grilla de cámaras es una vista limpia: no superponer los overlays del programa.
            return;
        }

        // Los HTML ocupan las posiciones 5 y 6 del ciclo, pero son las fuentes 4 y 5 del JSON.
        var sourceIndex = selectedSlot >= 5 ? selectedSlot - 1 : selectedSlot;
        var source = _config.Sources[sourceIndex];
        StatusText.Text = source.Name + " • " + source.Type.ToUpperInvariant() + " • 1920×1080 • 29.97 FPS";

        if (source.Type.Equals("HTML", StringComparison.OrdinalIgnoreCase))
            await ShowHtmlAsync(source.Url);
        else
            await ShowRtspAsync(source.Url);

        if (selectedSlot >= 0 && selectedSlot < 4)
            ApplyActiveOverlays(selectedSlot);
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

        int currentIndex;
        lock (_overlayStateLock) currentIndex = _index;
        var active = currentIndex >= 0 && currentIndex <= 6 ? currentIndex : -1;
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
            int currentIndex;
            lock (_overlayStateLock) currentIndex = _index;
            AirStatusText.Text = "AL AIRE: " + (currentIndex == 5 ? "GRILLA" : currentIndex == 6 ? "PLACA DE PROGRAMAS" : "—");
        }
    }

    private void OverlayToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button || button.Tag is not string tag) return;
        var parts = tag.Split('|');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var slot) || slot < 0 || slot > 4) return;

        var enabled = button.IsChecked == true;
        // Invalida cualquier captura que haya empezado antes del cambio del botón.
        Interlocked.Increment(ref _activeOverlayGeneration);
        if (!enabled)
        {
            lock (_overlayFrameLock)
            {
                switch (parts[1])
                {
                    case "climate": _climateFrame = null; break;
                    case "logo": _logoFrame = null; break;
                    case "news": _newsFrame = null; break;
                }
            }
        }

        lock (_overlayStateLock)
        {
            switch (parts[1])
            {
                case "climate": _climateEnabled[slot] = enabled; break;
                case "logo": _logoEnabled[slot] = enabled; break;
                case "news": _newsEnabled[slot] = enabled; break;
            }
        }

        if (GetActiveOverlaySlot() == slot)
            _ = ApplyActiveOverlaysAsync(slot);
    }

    private int GetActiveOverlaySlot() =>
        GetOverlaySlotSnapshot();

    // Los botones son por fuente. Al cambiar de fuente, reflejar el estado real
    // guardado en los arrays para que el usuario no vea el botón apagado mientras
    // la composición conserva el overlay activado para esa fuente.
    private void SyncOverlayToggleButtons(int slot)
    {
        foreach (var button in FindVisualChildren<ToggleButton>(this))
        {
            if (button.Tag is not string tag) continue;
            var parts = tag.Split('|');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var buttonSlot)) continue;
            if (buttonSlot != slot) continue;

            button.IsChecked = parts[1] switch
            {
                "climate" => _climateEnabled[slot],
                "logo" => _logoEnabled[slot],
                "news" => _newsEnabled[slot],
                _ => false
            };
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    private void HideAllOverlays()
    {
        // No colapsar WebView2: al ocultarlo Chromium frena o reduce sus
        // animaciones. Opacity oculta la capa en el programa, pero la página
        // permanece renderizando para que al activarla no vuelva congelada.
        ClimateHost.Visibility = Visibility.Visible;
        LogoHost.Visibility = Visibility.Visible;
        NewsHost.Visibility = Visibility.Visible;
        ClimateHost.Opacity = 0;
        LogoHost.Opacity = 0;
        NewsHost.Opacity = 0;
    }

    private void ApplyActiveOverlays(int slot) => _ = ApplyActiveOverlaysAsync(slot);

    private async Task ApplyActiveOverlaysAsync(int slot)
    {
        HideAllOverlays();
        // Las páginas HTML (grilla/programación) son fuentes limpias: nunca
        // deben recibir los overlays gráficos de las cámaras.
        if (_quadProgramActive || _htmlProgramActive) return;
        // Los WebView2 quedan cargados: no se vuelve a navegar al cambiar de cámara.
        // La composición final ya incorpora las capas capturadas. Los WebView2
        // permanecen activos pero nunca se dibujan por encima del preview: eso
        // duplicaría overlays y separaría la imagen visible de la salida real.
        ClimateHost.Opacity = 0;
        LogoHost.Opacity = 0;
        NewsHost.Opacity = 0;
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

        // Los overlays HTML se rasterizan en segundo plano y se mezclan con cada
        // cuadro del programa antes de distribuirlo a NDI, SRT y YouTube.
        _ = Task.Run(CaptureOverlayFramesLoopAsync);
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
        if (_htmlPreview2 is null)
        {
            _htmlPreview2 = new WebView2();
            HtmlHost2.Children.Add(_htmlPreview2);
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
            if (File.Exists(configured))
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(configured))!;
                _audioPlayer.CoreWebView2.SetVirtualHostNameToFolderMapping("studio-v01.local", directory, CoreWebView2HostResourceAccessKind.Allow);
                configured = "https://studio-v01.local/" + Uri.EscapeDataString(Path.GetFileName(configured));
            }
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

    private async Task InitializeHtmlProgramViewsAsync()
    {
        if (_config is null) return;
        var htmlSources = _config.Sources
            .Where(s => s.Type.Equals("HTML", StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        var views = new[] { _htmlPreview, _htmlPreview2 };

        for (var i = 0; i < htmlSources.Count && i < views.Length; i++)
        {
            var view = views[i];
            if (view is null) continue;
            try
            {
                await view.EnsureCoreWebView2Async();
                view.DefaultBackgroundColor = System.Drawing.Color.Transparent;
                await view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
                    (() => {
                        const style = document.createElement('style');
                        style.textContent = 'html,body{background:transparent!important;background-color:transparent!important;margin:0!important;}';
                        document.documentElement.appendChild(style);
                    })();
                ");
                var uri = new Uri(htmlSources[i].Url);
                if (view.Source is null || !string.Equals(view.Source.AbsoluteUri, uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
                    view.Source = uri;
            }
            catch (Exception ex)
            {
                Log($"HTML EN SEGUNDO PLANO [{i + 1}] ERROR: {ex.Message}");
            }
        }

        HtmlHost.Visibility = Visibility.Visible;
        HtmlHost2.Visibility = Visibility.Visible;
        HtmlHost.Opacity = 0;
        HtmlHost2.Opacity = 0;
        await Dispatcher.InvokeAsync(() => ResizeProgramPreview(), DispatcherPriority.Loaded);
    }

    private async Task ShowHtmlAsync(string url)
    {
        _quadProgramActive = false;
        _htmlProgramActive = false;
        HideAllOverlays();
        PreviewImage.Visibility = Visibility.Collapsed;
        QuadPreview.Visibility = Visibility.Collapsed;
        HtmlHost.Visibility = Visibility.Visible;

        try
        {
            var requestedUri = new Uri(url);
            var htmlSources = _config?.Sources
                .Where(s => s.Type.Equals("HTML", StringComparison.OrdinalIgnoreCase))
                .ToList() ?? [];
            var htmlSlot = htmlSources.FindIndex(s =>
                Uri.TryCreate(s.Url, UriKind.Absolute, out var configuredUri) &&
                string.Equals(configuredUri.AbsoluteUri, requestedUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase));
            // Cada página HTML conserva su propia WebView2 para no recargar grilla
            // al cambiar a programación, ni reiniciar al volver a seleccionarla.
            var view = htmlSlot <= 0 ? _htmlPreview : _htmlPreview2;
            if (view is null) return;

            await view.EnsureCoreWebView2Async();
            view.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            if (view.CoreWebView2 is not null && view.Source is null)
            {
                await view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
                    (() => {
                        const style = document.createElement('style');
                        style.textContent = 'html,body{background:transparent!important;background-color:transparent!important;margin:0!important;}';
                        document.documentElement.appendChild(style);
                    })();
                ");
            }

            if (view.Source is null || !string.Equals(view.Source.AbsoluteUri, requestedUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
                view.Source = requestedUri;

            HtmlHost.Opacity = view == _htmlPreview ? 1 : 0;
            HtmlHost2.Opacity = view == _htmlPreview2 ? 1 : 0;
            HtmlHost.Visibility = Visibility.Visible;
            HtmlHost2.Visibility = Visibility.Visible;
            ResizeProgramPreview();
            await Dispatcher.InvokeAsync(() => ResizeProgramPreview(), DispatcherPriority.Loaded);

            // El trabajador se conserva entre fuentes y apunta a la WebView
            // seleccionada. La página no se vuelve a navegar al recuperar el aire.
            _htmlCaptureView = view;
            if (_htmlCaptureCts is null || _htmlCaptureCts.IsCancellationRequested)
            {
                _htmlCaptureCts?.Dispose();
                _htmlCaptureCts = new CancellationTokenSource();
                var captureToken = _htmlCaptureCts.Token;
                _ = Task.Run(() => CaptureHtmlProgramLoopAsync(captureToken));
            }
            _htmlProgramActive = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "HTML ERROR: " + ex.Message;
            Log("HTML ERROR: " + ex);
        }
    }

    private async Task ShowQuadAsync()
    {
        _quadProgramActive = true;
        _htmlProgramActive = false;
        HideAllOverlays();
        // Mantener el HTML renderizando detrás de la grilla para que sus
        // animaciones sigan avanzando aunque la fuente no esté al aire.
        HtmlHost.Visibility = Visibility.Visible;
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

        Array.Clear(_quadNdiFrames, 0, _quadNdiFrames.Length);
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
        psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("bgr0");
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

            const int width = 960, height = 540, bpp = 4;
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
                    System.Windows.Media.PixelFormats.Bgr32, null, frame, width * bpp);
                bitmap.Freeze();

                byte[]? composed;
                lock (_ndiOutputLock)
                {
                    _quadNdiFrames[slot] = frame;
                    composed = ComposeQuadNdiFrame();
                }
                if (composed is not null) PublishProgramFrame(composed);

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

    private byte[]? ComposeQuadNdiFrame()
    {
        const int cellWidth = 960;
        const int cellHeight = 540;
        const int bpp = 4;
        var output = new byte[OutputWidth * OutputHeight * bpp];

        for (var slot = 0; slot < 4; slot++)
        {
            var source = _quadNdiFrames[slot];
            if (source is null) continue;

            var dstX = (slot % 2) * cellWidth;
            var dstY = (slot / 2) * cellHeight;

            for (var y = 0; y < cellHeight; y++)
            {
                var srcOffset = y * cellWidth * bpp;
                var dstOffset = ((dstY + y) * OutputWidth + dstX) * bpp;
                Buffer.BlockCopy(source, srcOffset, output, dstOffset, cellWidth * bpp);
            }
        }

        return output;
    }

    private async Task ShowRtspAsync(string url)
    {
        _quadProgramActive = false;
        _htmlProgramActive = false;
        // Mantener el HTML renderizando detrás de la cámara.
        HtmlHost.Visibility = Visibility.Visible;
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
        psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("bgr0");
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
        // FFmpeg entrega BGR0/BGRX: 4 bytes por pixel.
        const int bpp = 4;
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
                PublishProgramFrame(frame);
                // El preview se actualiza desde PreviewOutputPumpAsync con el
                // mismo fotograma compuesto que reciben SRT, NDI y YouTube.
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

    private double CalculateWebZoom()
    {
        var availableWidth = Math.Max(1, PreviewContainer.ActualWidth);
        var availableHeight = Math.Max(1, PreviewContainer.ActualHeight);
        var scale = Math.Min(
            availableWidth / (double)OutputWidth,
            availableHeight / (double)OutputHeight);

        return Math.Max(0.25, Math.Min(1.0, scale));
    }

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

        // WebView2 tiene un viewport del tamaño visual del preview. Aplicamos
        // el mismo factor de escala para conservar un lienzo lógico 1920x1080;
        // con ZoomFactor=1 el HTML se renderiza dentro del viewport pequeño y,
        // al ampliar la captura a 1920x1080, queda gigante/recortado.
        var webZoom = Math.Max(0.25, Math.Min(1.0, scale));

        if (_htmlPreview is not null)
            _htmlPreview.ZoomFactor = webZoom;
        if (_htmlPreview2 is not null)
            _htmlPreview2.ZoomFactor = webZoom;
        if (_climateOverlay is not null)
            _climateOverlay.ZoomFactor = webZoom;
        if (_logoOverlay is not null)
            _logoOverlay.ZoomFactor = webZoom;
        if (_newsOverlay is not null)
            _newsOverlay.ZoomFactor = webZoom;
    }

    private async Task InitializeNdiSafeAsync()
    {
        try
        {
            await Task.Run(RefreshNdiSources);
            if (!_ndiService.IsSenderActive)
            {
                var name = NdiOutputNameBox.Text.Trim();
                if (_ndiService.StartSender(string.IsNullOrWhiteSpace(name) ? "STUDIO V01" : name))
                    NdiOutputStatus.Text = "NDI SALIDA: ACTIVA — video 1920×1080";
                else
                {
                    NdiOutputStatus.Text = "NDI SALIDA ERROR — " + _ndiService.Status;
                    Log("NDI SALIDA ERROR: " + _ndiService.Status);
                }
            }
        }
        catch (Exception ex)
        {
            Log("NDI INICIALIZACIÓN ERROR: " + ex);
            NdiRuntimeStatus.Text = "NDI ERROR: " + ex.Message;
        }
    }

    private void NdiSourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NdiSourceCombo.SelectedItem is NdiSourceInfo source)
        {
            StatusText.Text = "NDI seleccionado: " + source.Name + " — presioná CONECTAR NDI / AL AIRE";
            Log("NDI seleccionado (sin conectar): " + source.Name);
        }
    }


    private async void MainWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBoxBase or PasswordBox) return;
        if (e.Key == Key.D0 || e.Key == Key.NumPad0)
        {
            if (!_ndiInputOnAir && NdiSourceCombo.SelectedItem is NdiSourceInfo)
                NdiConnect_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.D5 || e.Key == Key.NumPad5)
        {
            if (_ndiInputOnAir)
                NdiReturn_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private async void NdiConnect_Click(object sender, RoutedEventArgs e)
    {
        if (NdiSourceCombo.SelectedItem is not NdiSourceInfo source)
        {
            StatusText.Text = "NDI: seleccioná una fuente primero.";
            return;
        }

        try
        {
            _cycleTimer.Stop();
            _manualSelection = true;
            _ndiInputOnAir = false;
            lock (_overlayStateLock) _index = -1;

            await StopCurrentAsync();

            _ndiService.StopReceiver();
            _ndiCts?.Cancel();
            _ndiCts?.Dispose();
            _ndiCts = null;
            HideAllOverlays();

            HtmlHost.Visibility = Visibility.Collapsed;
            QuadPreview.Visibility = Visibility.Collapsed;
            PreviewImage.Visibility = Visibility.Visible;

            if (!_ndiService.StartReceiver(source))
            {
                StatusText.Text = "NDI INPUT ERROR — " + _ndiService.Status;
                Log("NDI INPUT ERROR: " + _ndiService.Status);
                return;
            }

            _ndiInputOnAir = true;
            _ndiCts = new CancellationTokenSource();
            NdiConnectButton.IsEnabled = false;
            NdiReturnButton.IsEnabled = true;
            StatusText.Text = "NDI AL AIRE • " + source.Name + " • esperando video...";
            Log("NDI AL AIRE: " + source.Name);
            _ = Task.Run(() => ReadNdiFramesAsync(_ndiCts.Token), _ndiCts.Token);
        }
        catch (Exception ex)
        {
            Log("NDI CONNECT ERROR: " + ex);
            StatusText.Text = "NDI INPUT ERROR: " + ex.Message;
        }
    }

    private async void NdiReturn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _ndiInputOnAir = false;
            _ndiCts?.Cancel();
            _ndiCts?.Dispose();
            _ndiCts = null;
            _ndiService.StopReceiver();

            NdiSourceCombo.SelectedIndex = -1;
            NdiConnectButton.IsEnabled = true;
            NdiReturnButton.IsEnabled = false;

            _manualSelection = false;
            lock (_overlayStateLock) _index = -1;

            Log("NDI: retorno al programa automático.");
            await ShowNextAsync();
            _cycleTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, _config?.CycleSeconds ?? 20));
            _cycleTimer.Start();
            StatusText.Text = "PROGRAMA AL AIRE — ciclo automático";
        }
        catch (Exception ex)
        {
            Log("NDI RETURN ERROR: " + ex);
            StatusText.Text = "ERROR AL VOLVER AL PROGRAMA: " + ex.Message;
        }
    }

    private async Task ReadNdiFramesAsync(CancellationToken token)
    {
        var firstFrame = true;

        while (!token.IsCancellationRequested && _ndiService.IsReceiverActive)
        {
            try
            {
                if (_ndiService.TryCaptureBgrx(out var pixels, out var width, out var height, out var stride) &&
                    pixels is not null)
                {
                    var outputFrame = NormalizeFrame(pixels, width, height, stride);
                    WriteStreamFrame(outputFrame);

                    if (firstFrame)
                    {
                        firstFrame = false;
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (token.IsCancellationRequested) return;
                            StatusText.Text = $"NDI INPUT • {width}×{height} • fuente NDI";
                        });
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log("NDI INPUT FRAME ERROR: " + ex);
                try { await Task.Delay(100, token); } catch { break; }
            }
        }
    }

    private static byte[] NormalizeFrame(byte[] pixels, int width, int height, int stride)
    {
        if (width == OutputWidth && height == OutputHeight && stride == OutputWidth * 4)
            return pixels;
        var source = BitmapSource.Create(width, height, 96, 96,
            System.Windows.Media.PixelFormats.Bgr32, null, pixels, stride);
        var scaled = new System.Windows.Media.Imaging.TransformedBitmap(source,
            new System.Windows.Media.ScaleTransform(OutputWidth / (double)width, OutputHeight / (double)height));
        scaled.Freeze();
        var output = new byte[OutputWidth * OutputHeight * 4];
        scaled.CopyPixels(output, OutputWidth * 4, 0);
        return output;
    }

    private void NdiRefresh_Click(object sender, RoutedEventArgs e)
    {
        _ = RefreshNdiSourcesAsync();
    }

    private async Task RefreshNdiSourcesAsync()
    {
        try
        {
            NdiRuntimeStatus.Text = "NDI: buscando fuentes...";
            await Task.Run(RefreshNdiSources);
        }
        catch (Exception ex)
        {
            Log("NDI REFRESH ERROR: " + ex);
            NdiRuntimeStatus.Text = "NDI ERROR: " + ex.Message;
        }
    }

    private void RefreshNdiSources()
    {
        var available = _ndiService.Initialize();
        if (!available)
        {
            Dispatcher.Invoke(() =>
            {
                NdiSourceCombo.ItemsSource = null;
                NdiRuntimeStatus.Text = "NDI: Runtime no encontrado";
            });
            return;
        }

        var sources = _ndiService.RefreshSources();

        Dispatcher.Invoke(() =>
        {
            NdiSourceCombo.ItemsSource = sources;
            NdiSourceCombo.SelectedIndex = -1;

            var runtime = Path.GetFileName(_ndiService.RuntimePath ?? "NDI Runtime");
            NdiRuntimeStatus.Text = sources.Count == 0
                ? $"NDI: Runtime OK ({runtime}) — sin fuentes detectadas"
                : $"NDI: {sources.Count} fuente(s) detectada(s) — {runtime}";

            NdiConnectButton.IsEnabled = sources.Count > 0;
            NdiReturnButton.IsEnabled = _ndiInputOnAir;
            Log(NdiRuntimeStatus.Text);
        });
    }

    private void NdiOutputNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ndiService.IsAvailable || !IsLoaded)
            return;

        var name = NdiOutputNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
            name = "STUDIO V01";

        if (_ndiService.StartSender(name))
        {
            NdiOutputStatus.Text = "NDI SALIDA: ACTIVA — " + name;
            Log("NDI SALIDA ACTIVA: " + name);
        }
        else
        {
            NdiOutputStatus.Text = "NDI SALIDA ERROR — " + _ndiService.Status;
            Log("NDI SALIDA ERROR: " + _ndiService.Status);
        }
    }


    // Punto único de distribución del programa: la misma señal se entrega a
    // NDI, SRT y YouTube, evitando que cada salida use un camino de video diferente.
    private readonly object _programFrameLock = new();
    private readonly object _ndiFrameLock = new();
    private byte[]? _latestNdiFrame;
    private readonly CancellationTokenSource _ndiPumpCts = new();
    private long _programFrameSequence;
    private long _lastProgramFrameTimestamp;
    private int _activeOverlayGeneration;

    private void PublishProgramFrame(byte[] frame)
    {
        if (frame.Length < OutputWidth * OutputHeight * 4) return;

        var composedFrame = ComposeProgramFrame(frame);
        lock (_programFrameLock)
        {
            // Limita la distribución a 29.97 fps incluso si las fuentes generan
            // cuadros en paralelo. La composición se publica como unidad indivisible.
            var now = Stopwatch.GetTimestamp();
            var minTicks = Stopwatch.Frequency * 1001L / 30000L;
            if (now - _lastProgramFrameTimestamp < minTicks) return;
            _lastProgramFrameTimestamp = now;

            lock (_ndiFrameLock)
            {
                _programFrameSequence++;
                _latestNdiFrame = composedFrame;
            }

            // Solo publicar el mismo buffer final a los codificadores de red.
            // Sus propios workers impiden que las conexiones lentas bloqueen cámaras.
            _srtOutput.WriteFrame(composedFrame);
            _rtmpOutput.WriteFrame(composedFrame);
        }

        if (_srtOutput.IsActive && !_srtFirstFrameReported)
        {
            _srtFirstFrameReported = true;
            _ = Dispatcher.BeginInvoke(() => SrtStatusText.Text = "SRT Listener: enviando cuadros del programa");
        }
        if (_rtmpOutput.IsActive && !_rtmpFirstFrameReported)
        {
            _rtmpFirstFrameReported = true;
            _ = Dispatcher.BeginInvoke(() => YoutubeStatusText.Text = "YouTube RTMP: enviando cuadros del programa");
        }
    }

    private async Task PreviewOutputPumpAsync(CancellationToken token)
    {
        var interval = TimeSpan.FromMilliseconds(1001d / 30d);
        long lastPresentedSequence = -1;
        while (!token.IsCancellationRequested)
        {
            byte[]? frame;
            long sequence;
            lock (_ndiFrameLock)
            {
                frame = _latestNdiFrame;
                sequence = _programFrameSequence;
            }

            // No recrear el bitmap ni poner trabajo en el Dispatcher si no llegó
            // una nueva composición desde el ciclo anterior.
            if (frame is not null && sequence != lastPresentedSequence)
            {
                try
                {
                    var bitmap = BitmapSource.Create(OutputWidth, OutputHeight, 96, 96,
                        System.Windows.Media.PixelFormats.Bgr32, null, frame, OutputWidth * 4);
                    bitmap.Freeze();
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (token.IsCancellationRequested) return;
                        // El preview presenta el mismo fotograma compuesto distribuido.
                        HtmlHost.Visibility = Visibility.Visible;
                        QuadPreview.Visibility = Visibility.Collapsed;
                        PreviewImage.Source = bitmap;
                        PreviewImage.Visibility = Visibility.Visible;
                    }, DispatcherPriority.Render);
                    lastPresentedSequence = sequence;
                }
                catch (Exception ex) { Log("PREVIEW FINAL ERROR: " + ex.Message); }
            }

            try { await Task.Delay(interval, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task NdiOutputPumpAsync(CancellationToken token)
    {
        var interval = TimeSpan.FromMilliseconds(1001d / 30d);
        long lastSentSequence = -1;
        while (!token.IsCancellationRequested)
        {
            byte[]? frame;
            long sequence;
            lock (_ndiFrameLock)
            {
                frame = _latestNdiFrame;
                sequence = _programFrameSequence;
            }

            if (frame is not null && sequence != lastSentSequence && _ndiService.IsSenderActive)
            {
                try
                {
                    _ndiService.SendBgrx(frame, OutputWidth, OutputHeight, OutputWidth * 4);
                    lastSentSequence = sequence;
                }
                catch (Exception ex)
                {
                    Log("NDI SALIDA FRAME ERROR: " + ex);
                    _ = Dispatcher.BeginInvoke(() => NdiOutputStatus.Text = "NDI SALIDA ERROR — " + ex.Message);
                }
            }

            try { await Task.Delay(interval, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private byte[] ComposeProgramFrame(byte[] source)
    {
        var output = new byte[OutputWidth * OutputHeight * 4];
        Buffer.BlockCopy(source, 0, output, 0, output.Length);

        byte[]? climate, logo, news;
        lock (_overlayFrameLock)
        {
            climate = _climateFrame;
            logo = _logoFrame;
            news = _newsFrame;
        }

        // Solo las cuatro fuentes de cámara admiten overlays. No reutilizar
        // capas antiguas al emitir grilla, HTML o una entrada NDI limpia.
        // Los botones mandan: si un overlay está desactivado, nunca se mezcla
        // una captura vieja que haya quedado en vuelo en el momento del cambio.
        int slot;
        bool climateEnabled, newsEnabled, logoEnabled;
        lock (_overlayStateLock)
        {
            slot = _index;
            climateEnabled = slot >= 0 && slot < 4 && _climateEnabled[slot];
            newsEnabled = slot >= 0 && slot < 4 && _newsEnabled[slot];
            logoEnabled = slot >= 0 && slot < 4 && _logoEnabled[slot];
        }
        if (!_quadProgramActive && !_htmlProgramActive && slot >= 0 && slot < 4)
        {
            if (climateEnabled) BlendOverlay(output, climate);
            if (newsEnabled) BlendOverlay(output, news);
            if (logoEnabled) BlendOverlay(output, logo);
        }
        return output;
    }

    private static void BlendOverlay(byte[] destination, byte[]? overlay)
    {
        if (overlay is null || overlay.Length < destination.Length) return;
        for (var i = 0; i < destination.Length; i += 4)
        {
            var alpha = overlay[i + 3];
            if (alpha == 0) continue;
            if (alpha == 255)
            {
                destination[i] = overlay[i];
                destination[i + 1] = overlay[i + 1];
                destination[i + 2] = overlay[i + 2];
                continue;
            }

            var inverse = 255 - alpha;
            destination[i] = (byte)((overlay[i] * alpha + destination[i] * inverse) / 255);
            destination[i + 1] = (byte)((overlay[i + 1] * alpha + destination[i + 1] * inverse) / 255);
            destination[i + 2] = (byte)((overlay[i + 2] * alpha + destination[i + 2] * inverse) / 255);
        }
    }

    private async Task CaptureOverlayFramesLoopAsync()
    {
        while (!Dispatcher.HasShutdownStarted)
        {
            try
            {
                // Las fuentes HTML y la grilla son limpias. No capturar overlays
                // cuando no forman parte del programa, pero mantener las WebViews
                // vivas para que sus relojes y datos sigan actualizándose.
                var mode = await Dispatcher.InvokeAsync(() => new
                {
                    CleanSource = _quadProgramActive || _htmlProgramActive,
                    Slot = GetActiveOverlaySlot()
                });

                if (mode.CleanSource || mode.Slot < 0)
                {
                    await Task.Delay(50);
                    continue;
                }

                var slot = mode.Slot;
                var generation = Volatile.Read(ref _activeOverlayGeneration);
                var enabled = await Dispatcher.InvokeAsync(() =>
                {
                    lock (_overlayStateLock)
                    {
                        return new[]
                        {
                            _climateEnabled[slot],
                            _logoEnabled[slot],
                            _newsEnabled[slot]
                        };
                    }
                });

                // Limpiar capas desactivadas sin conservar una imagen vieja.
                lock (_overlayFrameLock)
                {
                    if (!enabled[0]) _climateFrame = null;
                    if (!enabled[1]) _logoFrame = null;
                    if (!enabled[2]) _newsFrame = null;
                }

                // Ejecutar captura en paralelo, pero no iniciar otra ronda hasta que
                // finalicen las tres tareas. Esto evita capturas PNG acumuladas
                // compitiendo con el renderizado de WebView2.
                var captures = await Task.WhenAll(
                    enabled[0] ? CaptureWebViewFrameAsync(_climateOverlay, true) : Task.FromResult<byte[]?>(null),
                    enabled[1] ? CaptureWebViewFrameAsync(_logoOverlay, true) : Task.FromResult<byte[]?>(null),
                    enabled[2] ? CaptureWebViewFrameAsync(_newsOverlay, true) : Task.FromResult<byte[]?>(null));

                lock (_overlayFrameLock)
                {
                    // Si la fuente cambió mientras capturábamos, no publicar frames
                    // de una ronda antigua sobre la nueva cámara.
                    if (generation == Volatile.Read(ref _activeOverlayGeneration) &&
                        !_quadProgramActive && !_htmlProgramActive && GetActiveOverlaySlot() == slot)
                    {
                        if (enabled[0] && captures[0] is not null) _climateFrame = captures[0];
                        if (enabled[1] && captures[1] is not null) _logoFrame = captures[1];
                        if (enabled[2] && captures[2] is not null) _newsFrame = captures[2];
                    }
                }
            }
            catch (Exception ex) { Log("CAPTURA OVERLAYS ERROR: " + ex.Message); }

            // CapturePreviewAsync genera PNG y decodifica 1080p: capturar más
            // rápido que el ritmo de salida añade carga sin mejorar los 29.97 fps.
            // Las capturas siguen siendo paralelas y nunca se solapan entre rondas.
            await Task.Delay(33);
        }
    }

    private async Task CaptureHtmlProgramLoopAsync(CancellationToken token)
    {
        // Mantener un único worker; la WebView se conserva cargada incluso fuera
        // del aire, pero solo capturamos PNG cuando una página HTML está activa.
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!_htmlProgramActive)
                {
                    await Task.Delay(100, token);
                    continue;
                }

                var view = _htmlCaptureView;
                if (view is null)
                {
                    await Task.Delay(50, token);
                    continue;
                }

                var frame = await CaptureWebViewFrameAsync(view, true);
                // Descartar el frame si cambió la fuente mientras WebView2 capturaba.
                if (frame is not null &&
                    _htmlProgramActive &&
                    ReferenceEquals(view, _htmlCaptureView) &&
                    !token.IsCancellationRequested)
                {
                    PublishProgramFrame(frame);
                }

                // CapturePreviewAsync es costoso; mantenerlo cerca de 29.97 fps.
                await Task.Delay(33, token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log("CAPTURA HTML PROGRAMA ERROR: " + ex.Message);
                try { await Task.Delay(30, token); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task<byte[]?> CaptureWebViewFrameAsync(WebView2? view, bool enabled)
    {
        if (!enabled || view is null) return null;

        try
        {
            using var stream = new MemoryStream();
            var captureTask = await Dispatcher.InvokeAsync(() =>
            {
                if (view.CoreWebView2 is null) return Task.CompletedTask;
                return view.CoreWebView2.CapturePreviewAsync(
                    CoreWebView2CapturePreviewImageFormat.Png, stream);
            });
            await captureTask;
            if (stream.Length == 0) return null;
            stream.Position = 0;
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;

            var bgra = new FormatConvertedBitmap(decoder.Frames[0],
                System.Windows.Media.PixelFormats.Bgra32, null, 0);
            bgra.Freeze();

            // Si WebView2 ya entrega el tamaño de salida, evitar crear una
            // TransformedBitmap y hacer un segundo recorrido de escalado.
            var pixels = new byte[OutputWidth * OutputHeight * 4];
            if (bgra.PixelWidth == OutputWidth && bgra.PixelHeight == OutputHeight)
            {
                bgra.CopyPixels(pixels, OutputWidth * 4, 0);
            }
            else
            {
                var scaled = new TransformedBitmap(bgra, new System.Windows.Media.ScaleTransform(
                    OutputWidth / (double)bgra.PixelWidth, OutputHeight / (double)bgra.PixelHeight));
                scaled.Freeze();
                scaled.CopyPixels(pixels, OutputWidth * 4, 0);
            }
            return pixels;
        }
        catch (Exception ex)
        {
            Log("CAPTURA WEBVIEW ERROR: " + ex.Message);
            return null;
        }
    }

    // Adaptador para los lectores que aún usan el nombre anterior.
    private void WriteStreamFrame(byte[] frame) => PublishProgramFrame(frame);

    private void LoadOutputSettings()
    {
        if (_config is null) return;
        YoutubeServerBox.Text = string.IsNullOrWhiteSpace(_config.YoutubeServer) ? "rtmps://a.rtmps.youtube.com/live2" : _config.YoutubeServer;
        YoutubeKeyBox.Password = _config.YoutubeKey ?? "";
        SrtPortBox.Text = (_config.SrtPort > 0 ? _config.SrtPort : 9000).ToString();
        RefreshLocalIpAddresses();
        if (!string.IsNullOrWhiteSpace(_config.SrtLocalIp)) SrtLocalIpCombo.SelectedItem = _config.SrtLocalIp;
        NdiOutputNameBox2.Text = NdiOutputNameBox.Text;
        if (!string.IsNullOrWhiteSpace(_config.M3u8Url)) { M3u8PathBox.Text = _config.M3u8Url; M3u8StatusText.Text = File.Exists(_config.M3u8Url) ? "Archivo local configurado." : "URL/ruta configurada."; }
    }

    private void RefreshLocalIpAddresses()
    {
        var ips = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                try
                {
                    ips.AddRange(nic.GetIPProperties().UnicastAddresses
                        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                        .Select(a => a.Address.ToString()));
                }
                catch { }
            }
        }
        catch (Exception ex) { Log("IP DETECTION NETWORKINTERFACE: " + ex.Message); }

        if (ips.Count == 0)
        {
            try { ips.AddRange(Dns.GetHostAddresses(Dns.GetHostName()).Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)).Select(a => a.ToString())); }
            catch (Exception ex) { Log("IP DETECTION DNS: " + ex.Message); }
        }

        ips = ips.Distinct().ToList();
        string? preferredIp = null;
        try
        {
            // UDP Connect no envía datos; Windows elige la interfaz usada para salir a la red.
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 9));
            preferredIp = (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch (Exception ex) { Log("IP DETECTION ROUTE: " + ex.Message); }

        ips = ips.OrderByDescending(ip => ip == preferredIp).ThenBy(ip => ip).ToList();
        SrtLocalIpCombo.ItemsSource = ips;
        SrtLocalIpCombo.SelectedIndex = ips.Count > 0 ? 0 : -1;
        SrtStatusText.Text = ips.Count > 0
            ? "IP local detectada automáticamente: " + ips[0] + ". El destino debe ser la IP de la otra PC."
            : "No se detectó IPv4. Revisá el adaptador de red.";
        Log("IP LOCALES: " + (ips.Count > 0 ? string.Join(", ", ips) : "ninguna") + "; preferida=" + (preferredIp ?? "ninguna"));
    }

    private void SrtStart_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(SrtPortBox.Text.Trim(), out var port) || port < 1 || port > 65535)
        {
            SrtStatusText.Text = "Ingresá un puerto válido (1–65535).";
            return;
        }

        var localIp = SrtLocalIpCombo.SelectedItem?.ToString() ?? "";
        // Listener escucha en todas las interfaces; la IP detectada se muestra para que el Caller remoto conecte.
        var url = $"srt://0.0.0.0:{port}?mode=listener&transtype=live&latency=120000";

        if (_config is not null) { _config.SrtPort = port; _config.SrtLocalIp = localIp; SaveConfig(); }
        SrtStatusText.Text = $"SRT Listener: escuchando en {localIp}:{port}…";
        StartStreamOutput(_srtOutput, url, false);
    }

    private void RtmpStart_Click(object sender, RoutedEventArgs e)
    {
        var server = YoutubeServerBox.Text.Trim().TrimEnd((char)47);
        var key = YoutubeKeyBox.Password.Trim();
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(key)) { YoutubeStatusText.Text = "Ingresá la URL del servidor y la clave de transmisión."; return; }
        if (_config is not null) { _config.YoutubeServer = server; _config.YoutubeKey = key; SaveConfig(); }
        StartStreamOutput(_rtmpOutput, server + "/" + key, true);
    }

    private void SaveConfig()
    {
        try { if (_config is not null) File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "config.local.json"), JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { Log("No se pudo guardar configuración: " + ex.Message); }
    }

    private void ApplyNdiOutput_Click(object sender, RoutedEventArgs e) => NdiOutputNameBox.Text = string.IsNullOrWhiteSpace(NdiOutputNameBox2.Text) ? "STUDIO V01" : NdiOutputNameBox2.Text.Trim();

    private async void M3u8Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Seleccionar playlist M3U8", Filter = "Playlist HLS (*.m3u8)|*.m3u8|Todos los archivos (*.*)|*.*", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        M3u8PathBox.Text = dialog.FileName; M3u8StatusText.Text = "Archivo seleccionado: " + Path.GetFileName(dialog.FileName);
        if (_config is not null) { _config.M3u8Url = dialog.FileName; SaveConfig(); }
        await InitializeAudioPlayerSafeAsync();
    }

    private async void Stinger0Browse_Click(object sender, RoutedEventArgs e) => await SelectStingerArchiveAsync(Stinger0PathBox, Stinger0StatusText, "Stinger0");
    private async void Stinger5Browse_Click(object sender, RoutedEventArgs e) => await SelectStingerArchiveAsync(Stinger5PathBox, Stinger5StatusText, "Stinger5");

    private async Task SelectStingerArchiveAsync(TextBox pathBox, TextBlock status, string folderName)
    {
        var dialog = new OpenFileDialog { Title = "Seleccionar secuencia de imágenes (ZIP o RAR)", Filter = "Secuencias ZIP/RAR (*.zip;*.rar)|*.zip;*.rar", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        pathBox.Text = dialog.FileName; status.Text = "Copiando y extrayendo secuencia…";
        try
        {
            await Task.Run(() =>
            {
                var destination = Path.Combine(AppContext.BaseDirectory, "Stingers", folderName); Directory.CreateDirectory(destination);
                foreach (var file in Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories)) File.Delete(file);
                using var archive = ArchiveFactory.Open(dialog.FileName);
                foreach (var entry in archive.Entries.Where(e => !e.IsDirectory)) entry.WriteToDirectory(destination, new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
                var images = Directory.EnumerateFiles(destination, "*.*", SearchOption.AllDirectories).Count(f => new[] { ".png", ".jpg", ".jpeg", ".bmp", ".webp" }.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
                if (images == 0) throw new InvalidDataException("El archivo no contiene imágenes compatibles.");
            });
            status.Text = "Secuencia extraída correctamente (" + folderName + "). La transición visual se integrará en la siguiente etapa."; Log(folderName + " cargado desde " + dialog.FileName);
        }
        catch (Exception ex) { status.Text = "No se pudo extraer: " + ex.Message; Log(folderName + " ERROR: " + ex); }
    }

    private void StartStreamOutput(StreamOutputService output, string url, bool rtmp)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            StreamOutputStatus.Text = rtmp ? "Ingresá URL RTMP de YouTube + clave." : "No se pudo iniciar SRT Listener; revisá FFmpeg, puerto y Firewall.";
            return;
        }
        var ffmpeg = FindFfmpeg();
        if (ffmpeg is null)
        {
            StreamOutputStatus.Text = "FFmpeg no encontrado.";
            return;
        }
        if (output.Start(ffmpeg, url, rtmp, OutputWidth, OutputHeight))
        {
            StreamOutputStatus.Text = (rtmp ? "YOUTUBE RTMP" : "SRT LISTENER") + ": iniciando • 1920×1080 • 29.97 FPS";
            Log(StreamOutputStatus.Text);
            if (rtmp) { YoutubeStatusText.Text = "YouTube RTMP: conectando…"; RtmpStartButton2.IsEnabled = false; RtmpStopButton2.IsEnabled = true; RtmpStartButton.IsEnabled = false; RtmpStopButton.IsEnabled = true; }
            else { SrtStatusText.Text = "SRT Listener: esperando conexión…"; SrtStartButton2.IsEnabled = false; SrtStopButton2.IsEnabled = true; SrtStartButton.IsEnabled = false; SrtStopButton.IsEnabled = true; }
        }
        else
        {
            StreamOutputStatus.Text = (rtmp ? "YOUTUBE RTMP" : "SRT") + " ERROR: " + output.Status;
            Log(StreamOutputStatus.Text);
        }
    }

    private void SrtStop_Click(object sender, RoutedEventArgs e)
    {
        _srtOutput.Stop();
        _srtFirstFrameReported = false;
        SrtStartButton2.IsEnabled = true; SrtStopButton2.IsEnabled = false; SrtStatusText.Text = "SRT Listener: detenido";
        SrtStartButton.IsEnabled = true;
        SrtStopButton.IsEnabled = false;
        StreamOutputStatus.Text = "SRT: detenido";
    }

    private void RtmpStop_Click(object sender, RoutedEventArgs e)
    {
        _rtmpOutput.Stop();
        _rtmpFirstFrameReported = false;
        RtmpStartButton2.IsEnabled = true; RtmpStopButton2.IsEnabled = false; YoutubeStatusText.Text = "YouTube RTMP: detenido";
        RtmpStartButton.IsEnabled = true;
        RtmpStopButton.IsEnabled = false;
        StreamOutputStatus.Text = "YOUTUBE RTMP: detenido";
    }

    private void Log(string message)
    {
        try { File.AppendAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}"); }
        catch { }
    }

    private static string? FindFfmpeg()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            Path.Combine(Environment.CurrentDirectory, "tools", "ffmpeg.exe")
        };

        // También permite usar FFmpeg si está instalado globalmente y agregado al PATH de Windows.
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                candidates.Add(Path.Combine(directory.Trim('"'), "ffmpeg.exe"));
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            if (File.Exists(candidate)) return candidate;

        return null;
    }

    private async Task StopCurrentAsync()
    {
        // El capturador HTML es persistente entre cambios de fuente. Se cancela
        // solamente al cerrar la ventana, no cada vez que una cámara vuelve al aire.
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
        _ndiPumpCts.Cancel();
        _htmlCaptureCts?.Cancel();
        _htmlCaptureCts?.Dispose();
        _htmlCaptureCts = null;
        _htmlCaptureView = null;
        await StopCurrentAsync();
        _srtOutput.Dispose();
        _rtmpOutput.Dispose();
        _ndiService.Dispose();
    }
}