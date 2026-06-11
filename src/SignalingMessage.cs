using SIPSorcery.Net;

namespace DevKitRelay;

internal sealed record SignalingMessage
{
    public string Type { get; init; } = "";
    public string? Sdp { get; init; }
    public string? Candidate { get; init; }
    public string? SdpMid { get; init; }
    public ushort SdpMLineIndex { get; init; }

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
}
