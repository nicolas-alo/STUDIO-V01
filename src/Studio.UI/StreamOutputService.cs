using System.IO;
using System.Diagnostics;

namespace Studio.UI;

internal sealed class StreamOutputService : IDisposable
{
    private readonly object _gate = new();
    private Process? _process;
    private Stream? _input;
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
            lock (_gate) { _process = process; _input = process.StandardInput.BaseStream; }
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
            Status = "Conectando";
            return true;
        }
        catch (Exception ex) { Status = ex.Message; Stop(); return false; }
    }

    public void WriteFrame(byte[] frame)
    {
        lock (_gate)
        {
            if (_process is not { HasExited: false } || _input is null) return;
            try { _input.Write(frame, 0, frame.Length); }
            catch (Exception ex) { Status = ex.Message; StopLocked(); }
        }
    }

    public void Stop() { lock (_gate) StopLocked(); }
    private void StopLocked()
    {
        try { _input?.Dispose(); } catch { }
        _input = null;
        try { if (_process is { HasExited: false }) _process.Kill(true); } catch { }
        try { _process?.Dispose(); } catch { }
        _process = null;
    }
    public void Dispose() => Stop();
}