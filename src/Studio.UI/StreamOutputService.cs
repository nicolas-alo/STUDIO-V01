using System.IO;
using System.Diagnostics;

namespace Studio.UI;

internal sealed class StreamOutputService : IDisposable
{
    private readonly object _gate = new();
    private Process? _process;
    private Stream? _input;
    private CancellationTokenSource? _writerCts;
    private Task? _writerTask;
    private byte[]? _latestFrame;

    public string Status { get; private set; } = "Detenido";
    public bool IsActive { get { lock (_gate) return _process is { HasExited: false } && _input is not null; } }

    public bool Start(string ffmpegPath, string destination, bool rtmp, int width, int height)
    {
        Stop();
        try
        {
            var psi = new ProcessStartInfo(ffmpegPath)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("warning");
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("rawvideo");
            psi.ArgumentList.Add("-pixel_format"); psi.ArgumentList.Add("bgr0");
            psi.ArgumentList.Add("-video_size"); psi.ArgumentList.Add($"{width}x{height}");
            psi.ArgumentList.Add("-framerate"); psi.ArgumentList.Add("30000/1001");
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add("pipe:0");
            psi.ArgumentList.Add("-an");
            psi.ArgumentList.Add("-c:v"); psi.ArgumentList.Add("libx264");
            psi.ArgumentList.Add("-preset"); psi.ArgumentList.Add("veryfast");
            psi.ArgumentList.Add("-tune"); psi.ArgumentList.Add("zerolatency");
            psi.ArgumentList.Add("-pix_fmt"); psi.ArgumentList.Add("yuv420p");
            psi.ArgumentList.Add("-b:v"); psi.ArgumentList.Add("4500k");
            psi.ArgumentList.Add("-maxrate"); psi.ArgumentList.Add("4500k");
            psi.ArgumentList.Add("-bufsize"); psi.ArgumentList.Add("9000k");
            psi.ArgumentList.Add("-g"); psi.ArgumentList.Add("60");
            if (rtmp) { psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("flv"); }
            else { psi.ArgumentList.Add("-mpegts_flags"); psi.ArgumentList.Add("+resend_headers"); psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("mpegts"); }
            psi.ArgumentList.Add(destination);

            var process = Process.Start(psi);
            if (process is null) { Status = "FFmpeg no inició"; return false; }

            var cts = new CancellationTokenSource();
            lock (_gate)
            {
                _process = process;
                _input = process.StandardInput.BaseStream;
                _latestFrame = null;
                _writerCts = cts;
                Status = "Conectando";
                _writerTask = Task.Run(() => WriteFramesLoopAsync(process, cts.Token));
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!process.HasExited)
                    {
                        var line = await process.StandardError.ReadLineAsync();
                        if (line is null) break;
                        if (!string.IsNullOrWhiteSpace(line)) Status = line;
                    }
                }
                catch { }
            });
            return true;
        }
        catch (Exception ex) { Status = ex.Message; Stop(); return false; }
    }

    // La fuente nunca escribe directamente al pipe de FFmpeg. Conservamos el cuadro
    // más reciente y cada salida lo transmite desde su propio hilo, sin bloquear NDI
    // ni la captura cuando una conexión de red se ralentiza.
    public void WriteFrame(byte[] frame)
    {
        lock (_gate)
        {
            if (_process is not { HasExited: false } || _input is null) return;
            _latestFrame = frame;
        }
    }

    private async Task WriteFramesLoopAsync(Process process, CancellationToken token)
    {
        var interval = TimeSpan.FromMilliseconds(1001d / 30d);
        try
        {
            while (!token.IsCancellationRequested && !process.HasExited)
            {
                byte[]? frame;
                Stream? input;
                lock (_gate)
                {
                    if (!ReferenceEquals(_process, process)) return;
                    frame = _latestFrame;
                    input = _input;
                }

                if (frame is not null && input is not null)
                {
                    try
                    {
                        await input.WriteAsync(frame.AsMemory(0, frame.Length), token).ConfigureAwait(false);
                        if (Status == "Conectando") Status = "Emitiendo video";
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Status = "Error enviando video: " + ex.Message;
                        break;
                    }
                }

                await Task.Delay(interval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Process? process;
        lock (_gate)
        {
            cts = _writerCts;
            process = _process;
            _writerCts = null;
            _writerTask = null;
            _input = null;
            _process = null;
            _latestFrame = null;
            try { cts?.Cancel(); } catch { }
            try { if (process is { HasExited: false }) process.Kill(true); } catch { }
            try { process?.Dispose(); } catch { }
            Status = "Detenido";
        }
        cts?.Dispose();
    }

    public void Dispose() => Stop();
}
