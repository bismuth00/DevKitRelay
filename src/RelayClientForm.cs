using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using System.Drawing.Drawing2D;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DevKitRelay;

internal sealed class RelayClientForm : Form
{
    private static readonly JsonSerializerOptions GamepadJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Uri _serverUri;
    private readonly int _durationSeconds;
    private readonly VideoDisplayMode _displayMode;
    private readonly VideoDisplayPanel _videoPanel;
    private readonly Label _statusLabel;
    private readonly CancellationTokenSource _closingCts = new();
    private int _framesReceived;
    private volatile bool _sendGamepadInput;
    private Size _sourceWindowSize = Size.Empty;
    private Size _videoSize = Size.Empty;
    private RTCDataChannel? _controlChannel;

    public RelayClientForm(CommandLineOptions options)
    {
        _serverUri = options.ServerUri;
        _durationSeconds = options.ClientDurationSeconds;
        _displayMode = options.DisplayMode;
        Text = "DevKitRelay Client";
        Width = 1280;
        Height = 800;
        StartPosition = FormStartPosition.CenterScreen;

        _statusLabel = new Label
        {
            Text = "Connecting...",
            Dock = DockStyle.Top,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0)
        };

        _videoPanel = new VideoDisplayPanel
        {
            Dock = DockStyle.Fill,
            ScalingMode = options.FilterMode == VideoFilterMode.Nearest
                ? InterpolationMode.NearestNeighbor
                : InterpolationMode.HighQualityBicubic
        };

        Controls.Add(_videoPanel);
        Controls.Add(_statusLabel);

        Load += async (_, _) =>
        {
            if (_durationSeconds > 0)
            {
                var closeTimer = new System.Windows.Forms.Timer
                {
                    Interval = _durationSeconds * 1000
                };
                closeTimer.Tick += (_, _) =>
                {
                    closeTimer.Stop();
                    Close();
                };
                closeTimer.Start();
            }

            await ConnectAsync();
        };
        Activated += (_, _) => _sendGamepadInput = true;
        Deactivate += (_, _) => _sendGamepadInput = false;
        FormClosing += (_, _) => _closingCts.Cancel();
    }

    private async Task ConnectAsync()
    {
        try
        {
            using var webSocket = new ClientWebSocket();
            await webSocket.ConnectAsync(_serverUri, _closingCts.Token);
            SetStatus($"Signaling connected: {_serverUri}");

            using var peerConnection = new RTCPeerConnection();
            using var videoEndPoint = new VideoEncoderEndPoint();
            using var gamepadReader = new XInputGamepadReader();
            var localIceQueue = new List<RTCIceCandidate>();
            var remoteIceQueue = new List<RTCIceCandidateInit>();
            var answerSent = false;
            var remoteDescriptionSet = false;

            var videoTrack = new MediaStreamTrack(videoEndPoint.GetVideoSinkFormats(), MediaStreamStatusEnum.RecvOnly);
            peerConnection.addTrack(videoTrack);
            peerConnection.OnVideoFrameReceived += videoEndPoint.GotVideoFrame;
            peerConnection.OnVideoFormatsNegotiated += formats => videoEndPoint.SetVideoSinkFormat(formats.First());
            videoEndPoint.OnVideoSinkDecodedSample += ShowDecodedFrame;

            peerConnection.onicecandidate += async candidate =>
            {
                if (candidate is null)
                {
                    return;
                }

                if (!answerSent)
                {
                    localIceQueue.Add(candidate);
                }
                else if (webSocket.State == WebSocketState.Open)
                {
                    await WebSocketJson.SendAsync(webSocket, SignalingMessage.Ice(candidate), _closingCts.Token);
                }
            };

            peerConnection.ondatachannel += dataChannel =>
            {
                if (string.Equals(dataChannel.label, "input", StringComparison.OrdinalIgnoreCase))
                {
                    SetStatus("Input DataChannel created.");
                    _ = Task.Run(
                        () => SendGamepadInputAsync(dataChannel, gamepadReader, _closingCts.Token),
                        _closingCts.Token);
                    return;
                }

                if (string.Equals(dataChannel.label, "control", StringComparison.OrdinalIgnoreCase))
                {
                    SetStatus("Control DataChannel created.");
                    _controlChannel = dataChannel;
                    _ = Task.Run(() => WatchForStalledVideoAsync(_closingCts.Token), _closingCts.Token);
                    return;
                }

                if (string.Equals(dataChannel.label, "video-metadata", StringComparison.OrdinalIgnoreCase))
                {
                    SetStatus("Video metadata DataChannel created.");
                    dataChannel.onmessage += (_, _, data) =>
                    {
                        var metadata = JsonSerializer.Deserialize<VideoMetadata>(data, GamepadJsonOptions);
                        if (metadata is not null)
                        {
                            ResizeToSourceWindow(metadata);
                        }
                    };
                }
            };

            peerConnection.onconnectionstatechange += async state =>
            {
                SetStatus($"Peer connection state: {state}");
                if (state == RTCPeerConnectionState.connected)
                {
                    SetStatus("Receiving video stream.");
                }
                else if (state is RTCPeerConnectionState.closed or RTCPeerConnectionState.failed)
                {
                    await videoEndPoint.CloseVideo();
                }
            };

            while (!_closingCts.IsCancellationRequested && webSocket.State == WebSocketState.Open)
            {
                var message = await WebSocketJson.ReceiveAsync<SignalingMessage>(webSocket, _closingCts.Token);
                if (message is null)
                {
                    break;
                }

                switch (message.Type)
                {
                    case "video-metadata":
                        ResizeToSourceWindow(new VideoMetadata(
                            message.SourceWidth,
                            message.SourceHeight,
                            message.FrameWidth,
                            message.FrameHeight,
                            message.DisplayWidth,
                            message.DisplayHeight,
                            message.Scale));
                        break;

                    case "offer":
                        peerConnection.setRemoteDescription(new RTCSessionDescriptionInit
                        {
                            type = RTCSdpType.offer,
                            sdp = message.Sdp!
                        });
                        remoteDescriptionSet = true;
                        foreach (var candidate in remoteIceQueue)
                        {
                            peerConnection.addIceCandidate(candidate);
                        }
                        remoteIceQueue.Clear();

                        var answer = peerConnection.createAnswer(null);
                        await peerConnection.setLocalDescription(answer);
                        await WebSocketJson.SendAsync(webSocket, SignalingMessage.Answer(answer), _closingCts.Token);
                        answerSent = true;
                        foreach (var candidate in localIceQueue)
                        {
                            await WebSocketJson.SendAsync(webSocket, SignalingMessage.Ice(candidate), _closingCts.Token);
                        }
                        localIceQueue.Clear();
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
        }
        catch (OperationCanceledException)
        {
            // Window is closing.
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            MessageBox.Show(this, ex.Message, "DevKitRelay", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task SendGamepadInputAsync(
        RTCDataChannel dataChannel,
        IGamepadReader gamepadReader,
        CancellationToken cancellationToken)
    {
        GamepadState? lastSent = null;
        var nextHeartbeat = DateTimeOffset.MinValue;
        ulong sequence = 1;
        var disconnectedLogged = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!_sendGamepadInput)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(16), cancellationToken);
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            var current = gamepadReader.Read() with
            {
                Sequence = sequence,
                TimestampUnixMilliseconds = now.ToUnixTimeMilliseconds()
            };

            if (!current.IsConnected)
            {
                if (!disconnectedLogged)
                {
                    Console.WriteLine($"No {gamepadReader.ProviderName} gamepad connected.");
                    disconnectedLogged = true;
                }

                if (lastSent?.IsConnected == false)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(16), cancellationToken);
                    continue;
                }
            }
            else
            {
                disconnectedLogged = false;
            }

            if (!current.HasSameInput(lastSent) || now >= nextHeartbeat)
            {
                var outbound = current with { Sequence = sequence++ };
                try
                {
                    dataChannel.send(JsonSerializer.Serialize(outbound, GamepadJsonOptions));
                    lastSent = outbound;
                    nextHeartbeat = now.AddMilliseconds(500);
                }
                catch (InvalidOperationException)
                {
                    sequence--;
                }
                catch (ApplicationException)
                {
                    sequence--;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(16), cancellationToken);
        }
    }

    /// <summary>
    /// Asks the server for a keyframe whenever decoded frames stop arriving. A lost keyframe
    /// otherwise leaves the picture broken until the encoder's next automatic one.
    /// </summary>
    private async Task WatchForStalledVideoAsync(CancellationToken cancellationToken)
    {
        var stallTimeout = TimeSpan.FromSeconds(2);
        var lastSeenFrameCount = -1;

        while (!cancellationToken.IsCancellationRequested)
        {
            var frameCountAtStart = Volatile.Read(ref _framesReceived);

            // The encoder already forces a keyframe on its first frame, so only intervene once the
            // count has stopped moving between checks.
            if (lastSeenFrameCount == frameCountAtStart)
            {
                TryRequestKeyFrame();
            }

            lastSeenFrameCount = frameCountAtStart;
            await Task.Delay(stallTimeout, cancellationToken);
        }
    }

    private void TryRequestKeyFrame()
    {
        if (_controlChannel is not { } channel)
        {
            return;
        }

        try
        {
            channel.send(JsonSerializer.Serialize(ControlMessage.RequestKeyFrame(), GamepadJsonOptions));
            Console.WriteLine("Requested a keyframe from the server.");
        }
        catch (InvalidOperationException)
        {
            // The channel is not open yet; the next check will retry.
        }
        catch (ApplicationException)
        {
            // Same as above: transient data channel state.
        }
    }

    private void ShowDecodedFrame(byte[] sample, uint width, uint height, int stride, VideoPixelFormatsEnum pixelFormat)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => ShowDecodedFrame(sample, width, height, stride, pixelFormat)));
            return;
        }

        if (pixelFormat != VideoPixelFormatsEnum.Bgr && pixelFormat != VideoPixelFormatsEnum.Rgb)
        {
            return;
        }

        _framesReceived++;
        SetVideoSize((int)width, (int)height);
        if (_framesReceived == 1 || _framesReceived % 30 == 0)
        {
            Console.WriteLine($"Received video frame #{_framesReceived}: {width}x{height}, {sample.Length} bytes");
        }

        var frameWidth = (int)width;
        var frameHeight = (int)height;
        var sourceStride = ResolveSourceStride(sample.Length, frameWidth, frameHeight, stride);

        if (_framesReceived == 1 || _framesReceived % 30 == 0)
        {
            Console.WriteLine(
                $"Decoded frame layout: {width}x{height}, sample={sample.Length}, stride={stride}, sourceStride={sourceStride}, rowBytes={frameWidth * 3}");
        }

        _videoPanel.UpdateFrame(sample, frameWidth, frameHeight, sourceStride);
    }

    /// <summary>
    /// Works out the real row pitch of a decoded frame.
    /// </summary>
    /// <remarks>
    /// The decoder under-reports its stride: for a 1090 pixel wide frame it reports 3270 while it
    /// actually pads each row to a four byte boundary and writes 3272. The reported value is
    /// small enough to pass a bounds check, so trusting it shears the image by two bytes per row.
    /// The buffer length is therefore treated as authoritative and the reported stride is only a
    /// fallback.
    /// </remarks>
    private static int ResolveSourceStride(int sampleLength, int width, int height, int decoderStride)
    {
        var rowBytes = width * 3;

        if (height > 0)
        {
            // Almost every decoder aligns rows to four bytes; prefer that when it explains the
            // buffer length exactly.
            var alignedStride = (rowBytes + 3) & ~3;
            if ((long)alignedStride * height == sampleLength)
            {
                return alignedStride;
            }

            if (sampleLength % height == 0)
            {
                var exactStride = sampleLength / height;
                if (exactStride >= rowBytes)
                {
                    return exactStride;
                }
            }
        }

        var sourceStride = decoderStride;

        if (sourceStride == width)
        {
            sourceStride *= 3;
        }

        if (sourceStride <= 0 ||
            sourceStride < rowBytes ||
            (long)sourceStride * (height - 1) + rowBytes > sampleLength)
        {
            var inferredStride = height > 0 ? sampleLength / height : 0;
            sourceStride = inferredStride >= rowBytes ? inferredStride : rowBytes;
        }

        if ((long)sourceStride * (height - 1) + rowBytes > sampleLength)
        {
            throw new InvalidOperationException(
                $"Decoded frame buffer is too small: sample={sampleLength}, width={width}, height={height}, stride={decoderStride}, sourceStride={sourceStride}.");
        }

        return sourceStride;
    }

    private void SetVideoSize(int width, int height)
    {
        var nextVideoSize = new Size(width, height);
        if (_videoSize == nextVideoSize)
        {
            return;
        }

        _videoSize = nextVideoSize;
        if (_sourceWindowSize.IsEmpty)
        {
            ClientSize = new Size(width, height + _statusLabel.Height);
            Console.WriteLine($"Client window resized for video: {width}x{height}");
        }
    }

    private void ResizeToSourceWindow(VideoMetadata metadata)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => ResizeToSourceWindow(metadata)));
            return;
        }

        var nextSourceWindowSize = new Size(metadata.DisplayWidth, metadata.DisplayHeight);
        if (_sourceWindowSize == nextSourceWindowSize)
        {
            return;
        }

        _sourceWindowSize = nextSourceWindowSize;

        // In Frame mode the client area matches the encoded frame, so a downscaled stream is shown
        // at its native size rather than being upscaled back to the source window's dimensions.
        var displayArea = _displayMode == VideoDisplayMode.Frame
            ? new Size(metadata.FrameWidth, metadata.FrameHeight)
            : new Size(metadata.DisplayWidth, metadata.DisplayHeight);

        ClientSize = new Size(displayArea.Width, displayArea.Height + _statusLabel.Height);
        Console.WriteLine(
            $"Client display area resized to source window: display={displayArea.Width}x{displayArea.Height}, source={metadata.SourceWidth}x{metadata.SourceHeight}, frame={metadata.FrameWidth}x{metadata.FrameHeight}, scale={metadata.Scale:0.###}, mode={_displayMode}");
    }

    private void SetStatus(string status)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => SetStatus(status)));
            return;
        }

        Console.WriteLine(status);
        _statusLabel.Text = status;
    }
}
