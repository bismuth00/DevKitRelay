using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using SIPSorcery.Net;
using System.Net.WebSockets;

namespace DevKitRelay;

internal static class RelayServer
{
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
        var dataChannel = await peerConnection.createDataChannel("frames", null);
        using var sendLoopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var localIceQueue = new List<RTCIceCandidate>();
        var remoteIceQueue = new List<RTCIceCandidateInit>();
        var offerSent = false;
        var remoteDescriptionSet = false;

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

        dataChannel.onopen += () =>
        {
            Console.WriteLine("DataChannel open.");
            _ = Task.Run(() => SendFramesAsync(dataChannel, windowHandle, options, sendLoopCts.Token), sendLoopCts.Token);
        };

        dataChannel.onclose += () =>
        {
            Console.WriteLine("DataChannel closed.");
            CancelQuietly(sendLoopCts);
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

    private static async Task SendFramesAsync(
        RTCDataChannel dataChannel,
        IntPtr windowHandle,
        CommandLineOptions options,
        CancellationToken cancellationToken)
    {
        var capture = new WindowCapture(windowHandle, options.JpegQuality);
        var delay = TimeSpan.FromMilliseconds(1000.0 / options.FramesPerSecond);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var jpeg = capture.CaptureJpeg();
                dataChannel.send(jpeg);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Frame capture failed: {ex.Message}");
            }

            await Task.Delay(delay, cancellationToken);
        }
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
