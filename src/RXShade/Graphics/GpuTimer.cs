using Vortice.Direct3D11;

namespace RXShade.Graphics;

/// <summary>
/// Measures GPU time for the filter chain using timestamp queries.
///
/// Results are read back N frames late and polled without flushing, so this
/// never stalls the pipeline - which would defeat the point of measuring it.
/// If the result is not ready we simply keep the previous number.
/// </summary>
public sealed class GpuTimer : IDisposable
{
    private const int FrameLatency = 3;

    private sealed class Frame : IDisposable
    {
        public required ID3D11Query Disjoint { get; init; }
        public required ID3D11Query Start { get; init; }
        public required ID3D11Query End { get; init; }
        public bool InFlight;

        public void Dispose()
        {
            Disjoint.Dispose();
            Start.Dispose();
            End.Dispose();
        }
    }

    private readonly GraphicsDevice _gfx;
    private readonly Frame[] _frames = new Frame[FrameLatency];
    private int _index;

    /// <summary>Most recent successfully resolved GPU time for the filter chain.</summary>
    public double LastMilliseconds { get; private set; }

    public GpuTimer(GraphicsDevice gfx)
    {
        _gfx = gfx;
        for (int i = 0; i < FrameLatency; i++)
        {
            _frames[i] = new Frame
            {
                Disjoint = gfx.Device.CreateQuery(new QueryDescription(QueryType.TimestampDisjoint)),
                Start = gfx.Device.CreateQuery(new QueryDescription(QueryType.Timestamp)),
                End = gfx.Device.CreateQuery(new QueryDescription(QueryType.Timestamp))
            };
        }
    }

    public void Begin()
    {
        var frame = _frames[_index];
        _gfx.Context.Begin(frame.Disjoint);
        _gfx.Context.End(frame.Start);
    }

    public void End()
    {
        var frame = _frames[_index];
        _gfx.Context.End(frame.End);
        _gfx.Context.End(frame.Disjoint);
        frame.InFlight = true;

        _index = (_index + 1) % FrameLatency;
        TryResolve(_frames[_index]);
    }

    private void TryResolve(Frame frame)
    {
        if (!frame.InFlight) return;

        var ctx = _gfx.Context;

        // DoNotFlush: if the GPU has not finished this frame yet we just skip
        // the measurement rather than blocking the render thread on it.
        if (!ctx.GetData<QueryDataTimestampDisjoint>(frame.Disjoint, AsyncGetDataFlags.DoNotFlush, out var disjoint))
            return;
        if (!ctx.GetData<ulong>(frame.Start, AsyncGetDataFlags.DoNotFlush, out ulong start))
            return;
        if (!ctx.GetData<ulong>(frame.End, AsyncGetDataFlags.DoNotFlush, out ulong end))
            return;

        frame.InFlight = false;

        // Disjoint means the clock changed frequency mid-measurement (power
        // state transition); the timestamps are meaningless, so discard them.
        if (disjoint.Disjoint || disjoint.Frequency == 0) return;
        if (end <= start) return;

        LastMilliseconds = (end - start) * 1000.0 / disjoint.Frequency;
    }

    public void Dispose()
    {
        foreach (var frame in _frames)
            frame?.Dispose();
    }
}
