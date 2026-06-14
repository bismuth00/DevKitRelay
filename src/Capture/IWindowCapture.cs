namespace DevKitRelay;

internal interface IWindowCapture : IDisposable
{
    VideoMetadata GetVideoMetadata(double scale);

    CapturedVideoFrame CaptureBgrFrame(double scale, Size? outputSize = null);
}
