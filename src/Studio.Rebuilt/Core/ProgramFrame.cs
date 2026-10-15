namespace Studio.Rebuilt.Core;

/// <summary>
/// An owned, immutable-by-convention BGRA32 frame from the single program timeline.
/// Buffers are copied on creation so a source cannot mutate a frame after publication.
/// </summary>
public sealed class ProgramFrame
{
    public const int Width = 1920;
    public const int Height = 1080;
    public const int BytesPerPixel = 4;
    public const int Stride = Width * BytesPerPixel;
    public const int BufferLength = Stride * Height;

    private readonly byte[] _pixels;

    private ProgramFrame(byte[] pixels, long generation, long sequence, DateTimeOffset capturedAt)
    {
        _pixels = pixels;
        Generation = generation;
        Sequence = sequence;
        CapturedAt = capturedAt;
    }

    public long Generation { get; }
    public long Sequence { get; }
    public DateTimeOffset CapturedAt { get; }
    public ReadOnlyMemory<byte> Pixels => _pixels;

    public static ProgramFrame Create(ReadOnlySpan<byte> pixels, long generation, long sequence,
        DateTimeOffset? capturedAt = null)
    {
        if (pixels.Length != BufferLength)
            throw new ArgumentException($"Expected {BufferLength} bytes for a 1920x1080 BGRA32 frame; got {pixels.Length}.", nameof(pixels));

        return new ProgramFrame(pixels.ToArray(), generation, sequence, capturedAt ?? DateTimeOffset.UtcNow);
    }
}
