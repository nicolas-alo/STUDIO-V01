namespace Studio.Rebuilt.Core;

/// <summary>
/// Thread-safe handoff for the latest final-program frame. Every output adapter reads
/// this same bus; adapters must never compose their own overlays or select their own source.
/// </summary>
public sealed class ProgramFrameBus
{
    private readonly object _gate = new();
    private ProgramFrame? _latest;
    private long _generation;
    private long _lastSequence = -1;

    public long Generation
    {
        get { lock (_gate) return _generation; }
    }

    public ProgramFrame? Latest
    {
        get { lock (_gate) return _latest; }
    }

    public long BeginNewProgramGeneration()
    {
        lock (_gate)
        {
            _generation++;
            _latest = null;
            _lastSequence = -1;
            return _generation;
        }
    }

    /// <summary>Rejects frames from an old source generation and non-monotonic sequence numbers.</summary>
    public bool TryPublish(ProgramFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            if (frame.Generation != _generation || frame.Sequence <= _lastSequence)
                return false;

            _latest = frame;
            _lastSequence = frame.Sequence;
            return true;
        }
    }
}
