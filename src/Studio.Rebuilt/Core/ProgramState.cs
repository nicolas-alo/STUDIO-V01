namespace Studio.Rebuilt.Core;

public enum ProgramSourceKind
{
    NoSignal,
    Rtsp,
    Ndi,
    Quad,
    Html
}

public sealed record ProgramSource(string Name, ProgramSourceKind Kind, string? Address = null);

/// <summary>Snapshot of operator choices consumed by the one compositor.</summary>
public sealed record ProgramState(
    ProgramSource Source,
    bool ClockEnabled,
    bool LogoEnabled,
    bool WeatherEnabled,
    bool NewsEnabled,
    string? LogoPath,
    string? HtmlUrl,
    string? RtspUrl,
    bool NdiOutputEnabled,
    bool SrtOutputEnabled,
    bool YouTubeOutputEnabled)
{
    public static ProgramState Initial { get; } = new(
        new ProgramSource("Sin señal", ProgramSourceKind.NoSignal),
        ClockEnabled: false,
        LogoEnabled: false,
        WeatherEnabled: false,
        NewsEnabled: false,
        LogoPath: null,
        HtmlUrl: null,
        RtspUrl: null,
        NdiOutputEnabled: false,
        SrtOutputEnabled: false,
        YouTubeOutputEnabled: false);
}
