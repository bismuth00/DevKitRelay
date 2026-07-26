namespace DevKitRelay;

/// <summary>
/// Client-to-server messages on the "control" data channel. WebRTC PLI feedback is not surfaced
/// by SIPSorcery's peer connection, and both ends of this relay are our own code, so keyframe
/// requests travel over a data channel instead.
/// </summary>
internal sealed record ControlMessage
{
    public const string RequestKeyFrameType = "request-keyframe";

    public string Type { get; init; } = "";

    public static ControlMessage RequestKeyFrame() => new() { Type = RequestKeyFrameType };
}
