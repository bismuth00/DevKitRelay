using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;

namespace DevKitRelay;

internal sealed class ConfigurableVideoEncoderEndPoint : IDisposable
{
    private const int VideoSamplingRate = 90000;
    private const int DefaultFramesPerSecond = 30;
    private const int Vp8FormatId = 96;

    private static readonly List<VideoFormat> SupportedFormats =
    [
        new VideoFormat(VideoCodecsEnum.VP8, Vp8FormatId, VideoSamplingRate)
    ];

    private readonly VpxVideoEncoder _videoEncoder = new();
    private bool _isClosed;

    public ConfigurableVideoEncoderEndPoint(uint? targetKbps)
    {
        _videoEncoder.TargetKbps = targetKbps;
    }

    public event EncodedSampleDelegate? OnVideoSourceEncodedSample;

    public List<VideoFormat> GetVideoSourceFormats() => SupportedFormats;

    public void ExternalVideoSourceRawSample(
        uint durationMilliseconds,
        int width,
        int height,
        byte[] sample,
        VideoPixelFormatsEnum pixelFormat)
    {
        if (_isClosed || OnVideoSourceEncodedSample is null)
        {
            return;
        }

        var encodedBuffer = _videoEncoder.EncodeVideo(width, height, sample, pixelFormat, VideoCodecsEnum.VP8);
        if (encodedBuffer is null)
        {
            return;
        }

        var fps = durationMilliseconds > 0 ? 1000 / durationMilliseconds : DefaultFramesPerSecond;
        var durationRtpTimestamp = VideoSamplingRate / Math.Max(1, fps);
        OnVideoSourceEncodedSample.Invoke((uint)durationRtpTimestamp, encodedBuffer);
    }

    public Task CloseVideo()
    {
        _isClosed = true;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _isClosed = true;
        _videoEncoder.Dispose();
    }
}
