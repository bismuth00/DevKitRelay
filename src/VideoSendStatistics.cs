namespace DevKitRelay;

/// <summary>
/// Aggregates send-loop timings and prints one line per second, so the effect of capture and
/// encoder changes can be measured without attaching a profiler.
/// </summary>
internal sealed class VideoSendStatistics
{
    private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(1);

    private TimeSpan _windowStartedAt;
    private int _frames;
    private TimeSpan _captureTime;
    private TimeSpan _encodeTime;
    private long _encodedBytes;
    private int _lastQuantizer = -1;

    public void Record(TimeSpan captureTime, TimeSpan encodeTime, int encodedBytes, int quantizer)
    {
        _frames++;
        _captureTime += captureTime;
        _encodeTime += encodeTime;
        _encodedBytes += encodedBytes;
        _lastQuantizer = quantizer;
    }

    public void ReportIfDue(TimeSpan now)
    {
        var elapsed = now - _windowStartedAt;
        if (elapsed < ReportInterval)
        {
            return;
        }

        if (_frames > 0)
        {
            Console.WriteLine(
                $"Video send: {_frames / elapsed.TotalSeconds:0.0} fps, " +
                $"capture={_captureTime.TotalMilliseconds / _frames:0.0} ms, " +
                $"encode={_encodeTime.TotalMilliseconds / _frames:0.0} ms, " +
                $"{_encodedBytes * 8 / elapsed.TotalSeconds / 1000:0} kbps, " +
                $"avg frame={_encodedBytes / _frames} bytes, " +
                $"q={_lastQuantizer}");
        }
        else
        {
            Console.WriteLine("Video send: no frames encoded in the last second.");
        }

        _windowStartedAt = now;
        _frames = 0;
        _captureTime = TimeSpan.Zero;
        _encodeTime = TimeSpan.Zero;
        _encodedBytes = 0;
    }
}
