using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace DevKitRelay;

internal static class RelayServer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task RunAsync(CommandLineOptions options, CancellationToken cancellationToken)
    {
        var window = WindowCatalog.FindByTitle(options.WindowQuery);
        Console.WriteLine($"Streaming window: {window.Title} ({window.Handle})");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(options.ListenUrl);

        var app = builder.Build();
        app.UseWebSockets();
        app.MapGet("/", () => "DevKitRelay signaling server. Connect WebSocket client to /signal.");
        app.Map("/signal", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            await HandlePeerAsync(webSocket, window.Handle, options, context.RequestAborted);
        });

        Console.WriteLine($"Listening: {options.ListenUrl}");
        await app.RunAsync(cancellationToken);
    }

    private static async Task HandlePeerAsync(
        WebSocket webSocket,
        IntPtr windowHandle,
        CommandLineOptions options,
        CancellationToken cancellationToken)
    {
        using var peerConnection = new RTCPeerConnection();
        using var videoEndPoint = new ConfigurableVideoEncoderEndPoint(options.VideoBitrateKbps);
        using var capture = CreateWindowCapture(windowHandle);
        using var sendLoopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var localIceQueue = new List<RTCIceCandidate>();
        var remoteIceQueue = new List<RTCIceCandidateInit>();
        var offerSent = false;
        var remoteDescriptionSet = false;

        var videoTrack = new MediaStreamTrack(videoEndPoint.GetVideoSourceFormats(), MediaStreamStatusEnum.SendOnly);
        peerConnection.addTrack(videoTrack);
        videoEndPoint.OnVideoSourceEncodedSample += peerConnection.SendVideo;

        var initialMetadata = capture.GetVideoMetadata(options.VideoScale);
        await WebSocketJson.SendAsync(webSocket, SignalingMessage.VideoMetadata(initialMetadata), cancellationToken);
        Console.WriteLine(
            $"Video metadata signaled: source={initialMetadata.SourceWidth}x{initialMetadata.SourceHeight}, frame={initialMetadata.FrameWidth}x{initialMetadata.FrameHeight}, display={initialMetadata.DisplayWidth}x{initialMetadata.DisplayHeight}, scale={initialMetadata.Scale:0.###}");

        var inputChannel = await peerConnection.createDataChannel("input", null);
        inputChannel.onopen += () => Console.WriteLine("Input DataChannel open.");
        inputChannel.onmessage += (_, _, data) =>
        {
            Console.WriteLine($"Gamepad input: {Encoding.UTF8.GetString(data)}");
        };

        var videoMetadataChannelOpen = false;
        var videoMetadataChannel = await peerConnection.createDataChannel("video-metadata", null);
        videoMetadataChannel.onopen += () =>
        {
            videoMetadataChannelOpen = true;
            Console.WriteLine("Video metadata DataChannel open.");
        };

        peerConnection.onicecandidate += async candidate =>
        {
            if (candidate is null)
            {
                return;
            }

            if (!offerSent)
            {
                localIceQueue.Add(candidate);
            }
            else if (webSocket.State == WebSocketState.Open)
            {
                await WebSocketJson.SendAsync(webSocket, SignalingMessage.Ice(candidate), cancellationToken);
            }
        };

        peerConnection.onconnectionstatechange += async state =>
        {
            Console.WriteLine($"Peer connection state: {state}");

            if (state == RTCPeerConnectionState.connected)
            {
                _ = Task.Run(
                    () => SendVideoAsync(
                        videoEndPoint,
                        capture,
                        options,
                        videoMetadataChannel,
                        () => videoMetadataChannelOpen,
                        sendLoopCts.Token),
                    sendLoopCts.Token);
            }
            else if (state is RTCPeerConnectionState.closed or RTCPeerConnectionState.failed or RTCPeerConnectionState.disconnected)
            {
                CancelQuietly(sendLoopCts);
                await videoEndPoint.CloseVideo();
            }
        };

        var offer = peerConnection.createOffer(null);
        await peerConnection.setLocalDescription(offer);
        await WebSocketJson.SendAsync(webSocket, SignalingMessage.Offer(offer), cancellationToken);
        offerSent = true;
        foreach (var candidate in localIceQueue)
        {
            await WebSocketJson.SendAsync(webSocket, SignalingMessage.Ice(candidate), cancellationToken);
        }
        localIceQueue.Clear();

        while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var message = await WebSocketJson.ReceiveAsync<SignalingMessage>(webSocket, cancellationToken);
            if (message is null)
            {
                break;
            }

            switch (message.Type)
            {
                case "answer":
                    peerConnection.setRemoteDescription(new RTCSessionDescriptionInit
                    {
                        type = RTCSdpType.answer,
                        sdp = message.Sdp!
                    });
                    remoteDescriptionSet = true;
                    foreach (var candidate in remoteIceQueue)
                    {
                        peerConnection.addIceCandidate(candidate);
                    }
                    remoteIceQueue.Clear();
                    break;

                case "ice":
                    var iceCandidate = new RTCIceCandidateInit
                    {
                        candidate = message.Candidate,
                        sdpMid = message.SdpMid,
                        sdpMLineIndex = message.SdpMLineIndex
                    };

                    if (remoteDescriptionSet)
                    {
                        peerConnection.addIceCandidate(iceCandidate);
                    }
                    else
                    {
                        remoteIceQueue.Add(iceCandidate);
                    }
                    break;
            }
        }

        CancelQuietly(sendLoopCts);
    }

    private static async Task SendVideoAsync(
        ConfigurableVideoEncoderEndPoint videoEndPoint,
        IWindowCapture capture,
        CommandLineOptions options,
        RTCDataChannel videoMetadataChannel,
        Func<bool> isVideoMetadataChannelOpen,
        CancellationToken cancellationToken)
    {
        var delayMilliseconds = Math.Max(1, (int)Math.Round(1000.0 / options.FramesPerSecond));
        var delay = TimeSpan.FromMilliseconds(delayMilliseconds);
        VideoMetadata? lastSentMetadata = null;
        Size? encodedFrameSize = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var frame = capture.CaptureBgrFrame(options.VideoScale, encodedFrameSize);
                encodedFrameSize ??= new Size(frame.Width, frame.Height);
                var metadata = WindowCapture.CreateMetadata(
                    frame.SourceWidth,
                    frame.SourceHeight,
                    frame.Width,
                    frame.Height,
                    options.VideoScale);
                if (metadata != lastSentMetadata &&
                    isVideoMetadataChannelOpen() &&
                    TrySendVideoMetadata(videoMetadataChannel, metadata))
                {
                    lastSentMetadata = metadata;
                    Console.WriteLine(
                        $"Video metadata sent: source={metadata.SourceWidth}x{metadata.SourceHeight}, frame={metadata.FrameWidth}x{metadata.FrameHeight}, display={metadata.DisplayWidth}x{metadata.DisplayHeight}, scale={metadata.Scale:0.###}");
                }

                if (frame.Width != encodedFrameSize.Value.Width || frame.Height != encodedFrameSize.Value.Height)
                {
                    Console.Error.WriteLine(
                        $"Video frame skipped because encoder size changed unexpectedly: expected={encodedFrameSize.Value.Width}x{encodedFrameSize.Value.Height}, actual={frame.Width}x{frame.Height}");
                    continue;
                }

                videoEndPoint.ExternalVideoSourceRawSample(
                    (uint)delayMilliseconds,
                    frame.Width,
                    frame.Height,
                    frame.Bgr,
                    VideoPixelFormatsEnum.Bgr);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Video frame capture failed: {ex.Message}");
            }

            await Task.Delay(delay, cancellationToken);
        }
    }

    private static bool TrySendVideoMetadata(RTCDataChannel dataChannel, VideoMetadata metadata)
    {
        try
        {
            dataChannel.send(JsonSerializer.Serialize(metadata, JsonOptions));
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (ApplicationException)
        {
            return false;
        }
    }

    private static IWindowCapture CreateWindowCapture(IntPtr windowHandle)
    {
        if (WindowsGraphicsWindowCapture.IsSupported)
        {
            try
            {
                Console.WriteLine("Using Windows Graphics Capture.");
                return new WindowsGraphicsWindowCapture(windowHandle);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Windows Graphics Capture unavailable: {ex.Message}");
            }
        }

        Console.WriteLine("Using PrintWindow capture fallback.");
        return new WindowCapture(windowHandle);
    }

    private static void CancelQuietly(CancellationTokenSource cancellationTokenSource)
    {
        try
        {
            cancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The peer connection can raise close events while the request scope is unwinding.
        }
    }
}
