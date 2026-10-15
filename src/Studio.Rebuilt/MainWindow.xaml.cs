using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Studio.Rebuilt.Core;

namespace Studio.Rebuilt;

public partial class MainWindow : Window
{
    private readonly ProgramFrameBus _frameBus = new();
    private ProgramState _state = ProgramState.Initial;
    private long _sequence;
    private WriteableBitmap? _previewBitmap;
    private List<ConfiguredSource> _configuredSources = new();

    public MainWindow()
    {
        InitializeComponent();
        _frameBus.BeginNewProgramGeneration();
        UpdateStatus();
        TryLoadDefaultConfig();
    }

    private void TryLoadDefaultConfig()
    {
        // Local config is intentionally not committed: RTSP URLs may contain credentials.
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "config.local.json"),
            Path.Combine(AppContext.BaseDirectory, "config", "config.local.json"),
            Path.Combine(Environment.CurrentDirectory, "config.local.json"),
            Path.Combine(Environment.CurrentDirectory, "config", "config.local.json")
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is not null)
            LoadConfigFromPath(path, showErrors: false);
    }

    private void LoadConfig_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Seleccionar configuración de fuentes",
            Filter = "Archivos JSON (*.json)|*.json|Todos los archivos (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
            LoadConfigFromPath(dialog.FileName, showErrors: true);
    }

    private void LoadConfigFromPath(string path, bool showErrors)
    {
        try
        {
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<SourceConfiguration>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (config?.Sources is null)
                throw new InvalidDataException("El JSON no contiene una lista Sources.");

            var loaded = new List<ConfiguredSource>();
            foreach (var source in config.Sources)
            {
                if (string.IsNullOrWhiteSpace(source.Name) || string.IsNullOrWhiteSpace(source.Type) ||
                    string.IsNullOrWhiteSpace(source.Url))
                    continue;

                var type = source.Type.Trim().ToUpperInvariant();
                ProgramSourceKind kind;
                if (type == "RTSP") kind = ProgramSourceKind.Rtsp;
                else if (type == "HTML") kind = ProgramSourceKind.Html;
                else if (type == "NDI") kind = ProgramSourceKind.Ndi;
                else continue;

                if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) ||
                    (kind == ProgramSourceKind.Rtsp && !uri.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase)) ||
                    (kind == ProgramSourceKind.Html && uri.Scheme is not ("http" or "https")))
                    continue;

                loaded.Add(new ConfiguredSource(source.Name.Trim(), kind, source.Url.Trim()));
            }

            if (loaded.Count == 0)
                throw new InvalidDataException("No se encontraron fuentes RTSP, HTML o NDI con nombre y URL válidos.");

            _configuredSources = loaded;
            SourcesList.ItemsSource = null;
            SourcesList.ItemsSource = _configuredSources;
            SourcesList.SelectedIndex = 0;
            ConfigStatus.Text = $"{loaded.Count} fuentes cargadas · {Path.GetFileName(path)}";
            PipelineStatus.Text = "CONFIGURACIÓN CARGADA · ADAPTADORES DE VIDEO PENDIENTES";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            ConfigStatus.Text = "No se pudo cargar el JSON.";
            if (showErrors)
                MessageBox.Show(this, ex.Message, "STUDIO V01 · Configuración", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SourcesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourcesList.SelectedItem is not ConfiguredSource source) return;
        if (source.Kind == ProgramSourceKind.Rtsp) RtspAddress.Text = source.Url;
        if (source.Kind == ProgramSourceKind.Html) HtmlAddress.Text = source.Url;
    }

    private void UseSelectedSource_Click(object sender, RoutedEventArgs e)
    {
        if (SourcesList.SelectedItem is not ConfiguredSource source)
        {
            MessageBox.Show(this, "Primero seleccioná una fuente de la lista.", "STUDIO V01", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _frameBus.BeginNewProgramGeneration();
        _sequence = 0;
        _state = source.Kind switch
        {
            ProgramSourceKind.Rtsp => _state with
            {
                Source = new ProgramSource(source.Name, source.Kind, source.Url),
                RtspUrl = source.Url
            },
            ProgramSourceKind.Html => _state with
            {
                Source = new ProgramSource(source.Name, source.Kind, source.Url),
                HtmlUrl = source.Url
            },
            ProgramSourceKind.Ndi => _state with
            {
                Source = new ProgramSource(source.Name, source.Kind, source.Url)
            },
            _ => _state
        };
        PipelineStatus.Text = $"{source.Name.ToUpperInvariant()} SELECCIONADA · ADAPTADOR PENDIENTE";
        UpdateStatus();
    }

    private void ConfigureRtsp_Click(object sender, RoutedEventArgs e)
    {
        _frameBus.BeginNewProgramGeneration();
        _state = _state with
        {
            Source = new ProgramSource("RTSP manual", ProgramSourceKind.Rtsp, RtspAddress.Text),
            RtspUrl = RtspAddress.Text
        };
        PipelineStatus.Text = "RTSP CONFIGURADO · ADAPTADOR PENDIENTE";
        UpdateStatus();
    }

    private void RefreshNdi_Click(object sender, RoutedEventArgs e)
    {
        PipelineStatus.Text = "NDI · ADAPTADOR PENDIENTE";
        MessageBox.Show(this,
            "La carga de configuración reconoce entradas tipo NDI, pero el descubrimiento real y la recepción de vídeo todavía deben implementarse y probarse en Windows.",
            "STUDIO V01 · NDI", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SelectQuad_Click(object sender, RoutedEventArgs e)
    {
        _frameBus.BeginNewProgramGeneration();
        _state = _state with { Source = new ProgramSource("Grilla 2 × 2", ProgramSourceKind.Quad) };
        PipelineStatus.Text = "GRILLA SELECCIONADA · COMPOSITOR PENDIENTE";
        UpdateStatus();
    }

    private void ConfigureHtml_Click(object sender, RoutedEventArgs e)
    {
        _frameBus.BeginNewProgramGeneration();
        _state = _state with
        {
            Source = new ProgramSource("HTML manual", ProgramSourceKind.Html, HtmlAddress.Text),
            HtmlUrl = HtmlAddress.Text
        };
        PipelineStatus.Text = "HTML CONFIGURADO · CAPTURA PERSISTENTE PENDIENTE";
        UpdateStatus();
    }

    private void OverlayChanged(object sender, RoutedEventArgs e)
    {
        _state = _state with
        {
            ClockEnabled = ClockToggle.IsChecked == true,
            LogoEnabled = LogoToggle.IsChecked == true,
            WeatherEnabled = WeatherToggle.IsChecked == true,
            NewsEnabled = NewsToggle.IsChecked == true
        };
        UpdateStatus();
    }

    private void OutputChanged(object sender, RoutedEventArgs e)
    {
        _state = _state with
        {
            NdiOutputEnabled = NdiOutputToggle.IsChecked == true,
            SrtOutputEnabled = SrtOutputToggle.IsChecked == true,
            YouTubeOutputEnabled = YouTubeOutputToggle.IsChecked == true
        };
        UpdateStatus();
    }

    private void ResetProgram_Click(object sender, RoutedEventArgs e)
    {
        var generation = _frameBus.BeginNewProgramGeneration();
        _sequence = 0;
        ProgramPreview.Source = null;
        _previewBitmap = null;
        FrameStatus.Text = $"Frame bus: generación {generation}, esperando nuevo fotograma";
        PipelineStatus.Text = "FUENTE INVALIDADA · ESPERANDO NUEVA COMPOSICIÓN";
    }

    private void UpdateStatus()
    {
        var activeOverlays = new[]
        {
            _state.ClockEnabled ? "reloj" : null,
            _state.LogoEnabled ? "logo" : null,
            _state.WeatherEnabled ? "clima" : null,
            _state.NewsEnabled ? "noticias" : null
        }.Where(x => x is not null);
        var overlayText = string.Join(", ", activeOverlays);
        FrameStatus.Text = $"Fuente: {_state.Source.Name} · Capas activadas: {(overlayText.Length == 0 ? "ninguna" : overlayText)} · Frame bus: sin fotogramas publicados";
    }

    // Preview will be one consumer of the final frame bus; source adapters must not
    // write directly to the image control.
    private void RenderProgramFrame(ProgramFrame frame)
    {
        Dispatcher.Invoke(() =>
        {
            if (_previewBitmap is null)
            {
                _previewBitmap = new WriteableBitmap(ProgramFrame.Width, ProgramFrame.Height, 96, 96,
                    PixelFormats.Bgra32, null);
                ProgramPreview.Source = _previewBitmap;
            }
            _previewBitmap.WritePixels(
                new Int32Rect(0, 0, ProgramFrame.Width, ProgramFrame.Height),
                frame.Pixels.ToArray(), ProgramFrame.Stride, 0);
            FrameStatus.Text = $"Generación: {frame.Generation} · Fotograma: {frame.Sequence} · Captura: {frame.CapturedAt:HH:mm:ss.fff}";
        });
    }

    private sealed record SourceConfiguration
    {
        public int CycleSeconds { get; init; } = 20;
        public List<SourceConfigItem>? Sources { get; init; }
    }

    private sealed record SourceConfigItem
    {
        public string? Name { get; init; }
        public string? Type { get; init; }
        public string? Url { get; init; }
    }

    private sealed record ConfiguredSource(string Name, ProgramSourceKind Kind, string Url);
}