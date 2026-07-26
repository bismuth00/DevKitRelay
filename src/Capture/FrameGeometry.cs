namespace DevKitRelay;

/// <summary>
/// Frame sizing rules shared by the capture backends. I420 subsamples chroma by two, so every
/// encoded frame dimension has to be even and at least two pixels.
/// </summary>
internal static class FrameGeometry
{
    public static Size ScaleSize(Size sourceSize, double scale)
    {
        var width = (int)Math.Round(sourceSize.Width * scale);
        var height = (int)Math.Round(sourceSize.Height * scale);
        return ToEncodableSize(new Size(width, height));
    }

    public static Size ToEncodableSize(Size size)
    {
        var width = Math.Max(2, size.Width);
        var height = Math.Max(2, size.Height);

        width -= width % 2;
        height -= height % 2;
        return new Size(width, height);
    }
}
