namespace DevKitRelay;

internal sealed record VideoMetadata(
    int SourceWidth,
    int SourceHeight,
    int FrameWidth,
    int FrameHeight,
    int DisplayWidth,
    int DisplayHeight,
    double Scale);
