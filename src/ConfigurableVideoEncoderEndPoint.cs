using SIPSorceryMedia.Abstractions;

namespace DevKitRelay;

internal sealed class ConfigurableVideoEncoderEndPoint : IDisposable
{
    private const int VideoSamplingRate = 90000;
    private const int Vp8FormatId = 96;

    // 90000 Hz RTP clock / 1000 ms.
    private const uint RtpTicksPerMillisecond = VideoSamplingRate / 1000;

    // A single frame never legitimately spans more than a second; clamp so a stalled
    // capture cannot push the RTP timestamp far ahead of the wall clock.
    private const long MaxFrameDurationMilliseconds = 1000;

    // Bits per pixel per second used to derive a target bitrate when the caller does not
    // supply one. Tuned for LAN screen sharing, where bandwidth is cheap and artefacts are not.
    private const double BitsPerPixelPerSecond = 0.10;
    private const uint MinRecommendedKbps = 1500;
    private const uint MaxRecommendedKbps = 20000;

    private static readonly List<VideoFormat> SupportedFormats =
    [
        new VideoFormat(VideoCodecsEnum.VP8, Vp8FormatId, VideoSamplingRate)
    ];

    // The send loop runs on its own task while the peer connection can be torn down from a
    // callback thread, so encoding and disposal have to be mutually exclusive.
    private readonly object _encoderLock = new();
    private readonly uint _targetKbps;
    private readonly int _cpuUsed;
    private ScreenVp8Encoder? _videoEncoder;
    private bool _forceKeyFrame = true;
    private bool _isClosed;

    public ConfigurableVideoEncoderEndPoint(uint targetKbps, int cpuUsed)
    {
        _targetKbps = targetKbps;
        _cpuUsed = cpuUsed;
    }

    public event EncodedSampleDelegate? OnVideoSourceEncodedSample;

    public List<VideoFormat> GetVideoSourceFormats() => SupportedFormats;

    /// <summary>
    /// The quantizer used for the most recent frame (0-63), or -1 when unavailable. A value stuck
    /// near the maximum means the target bitrate is the limiting factor rather than the pipeline.
    /// </summary>
    public int LastQuantizer
    {
        get
        {
            lock (_encoderLock)
            {
                return _isClosed ? -1 : _videoEncoder?.LastQuantizer ?? -1;
            }
        }
    }

    /// <summary>
    /// Requests that the next encoded frame be a keyframe. Called when the receiver reports that
    /// it cannot decode, which otherwise leaves it showing a broken image until the next
    /// automatic keyframe.
    /// </summary>
    public void RequestKeyFrame() => _forceKeyFrame = true;

    /// <summary>
    /// Derives a target bitrate from the encoded frame size and frame rate. Without this the
    /// encoder falls back to the libvpx default, which is far too low for a full-size window.
    /// </summary>
    public static uint RecommendTargetKbps(int width, int height, int framesPerSecond)
    {
        var bitsPerSecond = (double)width * height * Math.Max(1, framesPerSecond) * BitsPerPixelPerSecond;
        var kbps = bitsPerSecond / 1000.0;

        if (kbps <= MinRecommendedKbps)
        {
            return MinRecommendedKbps;
        }

        return kbps >= MaxRecommendedKbps ? MaxRecommendedKbps : (uint)kbps;
    }

    /// <summary>
    /// Encodes and sends one frame. Returns the encoded byte count, or zero when nothing was sent.
    /// </summary>
    public int ExternalVideoSourceRawSample(
        long elapsedMilliseconds,
        int width,
        int height,
        byte[] sample,
        VideoPixelFormatsEnum pixelFormat)
    {
        if (_isClosed || OnVideoSourceEncodedSample is null)
        {
            return 0;
        }

        var frameDuration = Math.Clamp(elapsedMilliseconds, 1, MaxFrameDurationMilliseconds);

        // Captured frames are tightly packed, so the stride is simply the row length.
        var stride = width * BytesPerPixel(pixelFormat);
        var i420 = PixelConverter.ToI420(width, height, stride, sample, pixelFormat);

        byte[]? encodedBuffer;
        lock (_encoderLock)
        {
            if (_isClosed)
            {
                return 0;
            }

            // Read and clear together so a request that arrives mid-encode is honoured by the next
            // frame rather than being lost.
            var forceKeyFrame = _forceKeyFrame;
            _forceKeyFrame = false;

            encodedBuffer = GetEncoder(width, height).Encode(i420, (uint)frameDuration, forceKeyFrame);
        }

        if (encodedBuffer is null)
        {
            return 0;
        }

        OnVideoSourceEncodedSample.Invoke((uint)frameDuration * RtpTicksPerMillisecond, encodedBuffer);
        return encodedBuffer.Length;
    }

    public Task CloseVideo()
    {
        _isClosed = true;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (_encoderLock)
        {
            _isClosed = true;
            _videoEncoder?.Dispose();
            _videoEncoder = null;
        }
    }

    private static int BytesPerPixel(VideoPixelFormatsEnum pixelFormat) => pixelFormat switch
    {
        VideoPixelFormatsEnum.Bgr or VideoPixelFormatsEnum.Rgb => 3,
        VideoPixelFormatsEnum.Bgra or VideoPixelFormatsEnum.Rgba => 4,
        _ => throw new NotSupportedException($"Unsupported capture pixel format: {pixelFormat}.")
    };

    /// <summary>
    /// libvpx fixes the frame size at initialisation, so the encoder is created once the first
    /// frame reveals the encoded dimensions and rebuilt if they ever change.
    /// </summary>
    private ScreenVp8Encoder GetEncoder(int width, int height)
    {
        if (_videoEncoder is { } existing && existing.Width == width && existing.Height == height)
        {
            return existing;
        }

        _videoEncoder?.Dispose();
        _videoEncoder = new ScreenVp8Encoder(width, height, _targetKbps, _cpuUsed);
        _forceKeyFrame = true;
        Console.WriteLine($"VP8 encoder initialised: {width}x{height}, {_targetKbps} kbps, cpu-used={_cpuUsed}");
        return _videoEncoder;
    }
}
