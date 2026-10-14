using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Studio.Rebuilt.Core;

namespace Studio.Rebuilt;

public partial class MainWindow : Window
{
    private readonly ProgramFrameBus _frameBus = new();
    private ProgramState _state = ProgramState.Initial;
    private long _sequence;
    private WriteableBitmap? _previewBitmap;

    public MainWindow()
    {
        InitializeComponent();
        _frameBus.BeginNewProgramGeneration();
        UpdateStatus();
    }

    private void ConfigureRtsp_Click(object sender, RoutedEventArgs e)
    {
        _state = _state with
        {
            Source = new ProgramSource("RTSP configurado", ProgramSourceKind.Rtsp, RtspAddress.Text),
            RtspUrl = RtspAddress.Text
        };
        PipelineStatus.Text = "RTSP CONFIGURADO · ADAPTADOR PENDIENTE";
        UpdateStatus();
    }

    private void RefreshNdi_Click(object sender, RoutedEventArgs e)
    {
        PipelineStatus.Text = "NDI · ADAPTADOR PENDIENTE";
        MessageBox.Show(this,
            "La base limpia ya reserva NDI como tipo de fuente y salida. El descubrimiento real de dispositivos y la recepción de vídeo todavía deben implementarse y probarse en Windows.",
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
            Source = new ProgramSource("HTML / programación", ProgramSourceKind.Html, HtmlAddress.Text),
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

    // This is the only preview sink. When adapters are added, they will consume the
    // same ProgramFrameBus; sources must never write directly to this image control.
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
                frame.Pixels.ToArray(),
                ProgramFrame.Stride,
                0);
            FrameStatus.Text = $"Generación: {frame.Generation} · Fotograma: {frame.Sequence} · Captura: {frame.CapturedAt:HH:mm:ss.fff}";
        });
    }
}
