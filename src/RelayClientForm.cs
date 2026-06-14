using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using System.Drawing.Imaging;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DevKitRelay;

internal sealed class RelayClientForm : Form
{
    private static readonly JsonSerializerOptions GamepadJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Uri _serverUri;
    private readonly int _durationSeconds;
    private readonly PictureBox _pictureBox;
    private readonly Label _statusLabel;
    private readonly CancellationTokenSource _closingCts = new();
    private int _framesReceived;
    private volatile bool _sendGamepadInput;
    private Size _videoSize = Size.Empty;

    public RelayClientForm(Uri serverUri, int durationSeconds)
    {
        _serverUri = serverUri;
        _durationSeconds = durationSeconds;
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

        _pictureBox = new PictureBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black,
            SizeMode = PictureBoxSizeMode.Zoom
        };

        Controls.Add(_pictureBox);
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
                if (!string.Equals(dataChannel.label, "input", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                SetStatus("Input DataChannel created.");
                _ = Task.Run(
                    () => SendGamepadInputAsync(dataChannel, gamepadReader, _closingCts.Token),
                    _closingCts.Token);
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
        ResizeToVideo((int)width, (int)height);
        if (_framesReceived == 1 || _framesReceived % 30 == 0)
        {
            Console.WriteLine($"Received video frame #{_framesReceived}: {width}x{height}, {sample.Length} bytes");
        }

        var next = new Bitmap((int)width, (int)height, PixelFormat.Format24bppRgb);
        var area = new Rectangle(0, 0, next.Width, next.Height);
        var bitmapData = next.LockBits(area, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

        try
        {
            var rowBytes = next.Width * 3;

            for (var y = 0; y < next.Height; y++)
            {
                var destination = IntPtr.Add(bitmapData.Scan0, y * bitmapData.Stride);
                Marshal.Copy(sample, y * stride, destination, rowBytes);
            }
        }
        finally
        {
            next.UnlockBits(bitmapData);
        }

        var previous = _pictureBox.Image;
        _pictureBox.Image = next;
        previous?.Dispose();
    }

    private void ResizeToVideo(int width, int height)
    {
        var nextVideoSize = new Size(width, height);
        if (_videoSize == nextVideoSize)
        {
            return;
        }

        _videoSize = nextVideoSize;
        ClientSize = new Size(width, height + _statusLabel.Height);
        Console.WriteLine($"Client window resized for video: {width}x{height}");
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
