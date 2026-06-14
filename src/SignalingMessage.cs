using SIPSorcery.Net;

namespace DevKitRelay;

internal sealed record SignalingMessage
{
    public string Type { get; init; } = "";
    public string? Sdp { get; init; }
    public string? Candidate { get; init; }
    public string? SdpMid { get; init; }
    public ushort SdpMLineIndex { get; init; }
    public int SourceWidth { get; init; }
    public int SourceHeight { get; init; }
    public int FrameWidth { get; init; }
    public int FrameHeight { get; init; }
    public int DisplayWidth { get; init; }
    public int DisplayHeight { get; init; }
    public double Scale { get; init; }

    public static SignalingMessage Offer(RTCSessionDescriptionInit description) => new()
    {
        Type = "offer",
        Sdp = description.sdp
    };

    public static SignalingMessage Answer(RTCSessionDescriptionInit description) => new()
    {
        Type = "answer",
        Sdp = description.sdp
    };

    public static SignalingMessage Ice(RTCIceCandidate candidate) => new()
    {
        Type = "ice",
        Candidate = candidate.candidate,
        SdpMid = candidate.sdpMid,
        SdpMLineIndex = candidate.sdpMLineIndex
    };

    public static SignalingMessage VideoMetadata(VideoMetadata metadata) => new()
    {
        Type = "video-metadata",
        SourceWidth = metadata.SourceWidth,
        SourceHeight = metadata.SourceHeight,
        FrameWidth = metadata.FrameWidth,
        FrameHeight = metadata.FrameHeight,
        DisplayWidth = metadata.DisplayWidth,
        DisplayHeight = metadata.DisplayHeight,
        Scale = metadata.Scale
    };
}
