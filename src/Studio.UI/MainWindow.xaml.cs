using Microsoft.Web.WebView2.Core;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Studio.UI;

public partial class MainWindow : Window
{
    private const int OutputWidth = 1920;
    private const int OutputHeight = 1080;

    private sealed class LocalConfig
    {
        public int CycleSeconds { get; set; } = 20;
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
    private LocalConfig? _config;
    private int _index = -1;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        _cycleTimer.Tick += async (_, _) => await ShowNextAsync();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _config = await LoadConfigAsync();
            if (_config.Sources.Count != 6)
            {
                StatusText.Text = "CONFIGURACIÓN INVÁLIDA: deben existir 6 fuentes.";
                return;
            }

            _cycleTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, _config.CycleSeconds));
            _cycleTimer.Start();
            await ShowNextAsync();
        }
        catch (Exception ex)
        {
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

        _index = (_index + 1) % _config.Sources.Count;
        var source = _config.Sources[_index];

        await StopCurrentAsync();
        StatusText.Text = source.Name + " • " + source.Type.ToUpperInvariant() + " • 1920×1080 • 29.97 FPS";

        if (source.Type.Equals("HTML", StringComparison.OrdinalIgnoreCase))
            await ShowHtmlAsync(source.Url);
        else
            await ShowRtspAsync(source.Url);
    }

    private async Task ShowHtmlAsync(string url)
    {
        PreviewImage.Visibility = Visibility.Collapsed;
        HtmlPreview.Visibility = Visibility.Visible;

        try
        {
            await HtmlPreview.EnsureCoreWebView2Async();
            HtmlPreview.Source = new Uri(url);
        }
        catch (Exception ex)
        {
            StatusText.Text = "HTML ERROR: " + ex.Message;
        }
    }

    private async Task ShowRtspAsync(string url)
    {
        HtmlPreview.Visibility = Visibility.Collapsed;
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
        psi.ArgumentList.Add("-rw_timeout"); psi.ArgumentList.Add("8000000");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(url);
        psi.ArgumentList.Add("-an");
        psi.ArgumentList.Add("-vf");
        psi.ArgumentList.Add("scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2,fps=30000/1001");
        psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("bgr24");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("rawvideo");
        psi.ArgumentList.Add("pipe:1");

        try
        {
            _ffmpeg = Process.Start(psi);
            if (_ffmpeg is null)
            {
                StatusText.Text = "NO SE PUDO INICIAR FFMPEG.";
                return;
            }

            _ = Task.Run(() => ReadFramesAsync(_ffmpeg, token), token);
        }
        catch (Exception ex)
        {
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
        await StopCurrentAsync();
    }
}
