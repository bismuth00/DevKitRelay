using System.Runtime.InteropServices;
using vpxmd;

namespace DevKitRelay;

/// <summary>
/// A VP8 encoder driven directly against libvpx, so that the settings that matter for window
/// streaming can be configured. SIPSorcery's <c>VpxVideoEncoder</c> only exposes a target
/// bitrate and leaves everything else at the libvpx defaults, which are tuned for camera
/// content: no screen-content mode, a static-block threshold that smears moving text, and a
/// quantizer floor that prevents a still window from ever becoming sharp.
/// </summary>
internal sealed class ScreenVp8Encoder : IDisposable
{
    private const int VpxEncoderAbiVersion = 23;
    private const int VpxDeadlineRealtime = 1;
    private const int VpxEflagForceKeyFrame = 1;
    private const uint ImagePlaneAlignment = 32;

    // libvpx plane indices into vpx_image.stride.
    private const int PlaneYIndex = 0;
    private const int PlaneUIndex = 1;
    private const int PlaneVIndex = 2;

    // Control ids from Vp8eEncControlId, passed through to vpx_codec_control.
    private const int SetCpuUsed = 13;
    private const int SetStaticThreshold = 17;
    private const int SetTokenPartitions = 18;
    private const int GetLastQuantizer64 = 20;
    private const int SetMaxIntraBitratePct = 26;
    private const int SetScreenContentMode = 31;

    private readonly VpxCodecCtx _context = new();
    private readonly VpxImage _image = new();
    private readonly int _width;
    private readonly int _height;
    private long _presentationTimestamp;
    private bool _disposed;

    public ScreenVp8Encoder(int width, int height, uint targetKbps, int cpuUsed)
    {
        if (width <= 0 || height <= 0 || width % 2 != 0 || height % 2 != 0)
        {
            throw new ArgumentException($"I420 requires even, positive dimensions but got {width}x{height}.");
        }

        _width = width;
        _height = height;

        Initialise(targetKbps, cpuUsed);
    }

    public int Width => _width;

    public int Height => _height;

    /// <summary>
    /// The quantizer libvpx settled on for the most recent frame, on the 0-63 scale. A value
    /// pinned near the top means the target bitrate is too low for the content.
    /// </summary>
    public int LastQuantizer
    {
        get
        {
            if (_disposed)
            {
                return -1;
            }

            var quantizer = 0;
            return GetControl(_context.__Instance, GetLastQuantizer64, ref quantizer) == VpxCodecErrT.VPX_CODEC_OK
                ? quantizer
                : -1;
        }
    }

    /// <summary>
    /// Encodes one I420 frame. Returns the compressed frame, or null when libvpx produced no
    /// output for this input (for example a dropped frame).
    /// </summary>
    public byte[]? Encode(byte[] i420, uint durationMilliseconds, bool forceKeyFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var expectedLength = _width * _height * 3 / 2;
        if (i420.Length < expectedLength)
        {
            throw new ArgumentException(
                $"I420 buffer is too small for {_width}x{_height}: expected {expectedLength}, got {i420.Length}.",
                nameof(i420));
        }

        WriteImagePlanes(i420);

        var duration = Math.Max(1u, durationMilliseconds);
        var flags = forceKeyFrame ? VpxEflagForceKeyFrame : 0;
        var result = vpx_encoder.VpxCodecEncode(
            _context,
            _image,
            _presentationTimestamp,
            duration,
            flags,
            VpxDeadlineRealtime);

        if (result != VpxCodecErrT.VPX_CODEC_OK)
        {
            throw new InvalidOperationException($"vpx_codec_encode failed: {vpx_codec.VpxCodecErrToString(result)}");
        }

        _presentationTimestamp += duration;
        return ReadEncodedFrame();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        vpx_codec.VpxCodecDestroy(_context);
        VpxImage.VpxImgFree(_image);
        _image.Dispose();
        _context.Dispose();
    }

    private void Initialise(uint targetKbps, int cpuUsed)
    {
        // Use vpx_codec_vp8_cx() rather than the vpx_codec_vp8_cx_algo global. The generated
        // binding for the global resolves to an address libvpx will not accept, and passing it
        // faults inside vpx_codec_enc_config_default.
        var codecInterface = vp8cx.VpxCodecVp8Cx();

        using var config = new VpxCodecEncCfg();
        Check(
            vpx_encoder.VpxCodecEncConfigDefault(codecInterface, config, 0),
            "vpx_codec_enc_config_default");

        config.GW = (uint)_width;
        config.GH = (uint)_height;
        config.GPass = VpxEncPass.VPX_RC_ONE_PASS;

        // Encode in millisecond units so the caller can hand us real measured frame durations.
        using (var timebase = new VpxRational { Num = 1, Den = 1000 })
        {
            config.GTimebase = timebase;
        }

        // Realtime streaming: never buffer future frames, and stay decodable after packet loss.
        config.GLagInFrames = 0;
        config.GErrorResilient = 1;
        config.GThreads = (uint)Math.Clamp(Environment.ProcessorCount, 1, 8);

        config.RcTargetBitrate = targetKbps;
        config.RcEndUsage = VpxRcMode.VPX_CBR;

        // A low quantizer floor is what lets a static window settle to a sharp image; the libvpx
        // default floor keeps it permanently soft.
        config.RcMinQuantizer = 4;
        config.RcMaxQuantizer = 52;
        config.RcUndershootPct = 50;
        config.RcOvershootPct = 50;

        // Small rate-control buffer: we would rather lose quality briefly than accumulate latency.
        config.RcBufSz = 1000;
        config.RcBufInitialSz = 500;
        config.RcBufOptimalSz = 600;
        config.RcDropframeThresh = 0;
        config.RcResizeAllowed = 0;

        // Keyframes are expensive and mostly unnecessary on a LAN; the receiver asks for one when
        // it actually needs it. See RelayServer's keyframe request handling.
        config.KfMode = VpxKfMode.VPX_KF_AUTO;
        config.KfMinDist = 0;
        config.KfMaxDist = 300;

        Check(
            vpx_encoder.VpxCodecEncInitVer(_context, codecInterface, config, 0, VpxEncoderAbiVersion),
            "vpx_codec_enc_init");

        if (VpxImage.VpxImgAlloc(_image, VpxImgFmt.VPX_IMG_FMT_I420, (uint)_width, (uint)_height, ImagePlaneAlignment)
            is null)
        {
            throw new InvalidOperationException($"vpx_img_alloc failed for {_width}x{_height}.");
        }

        ApplyScreenContentControls(cpuUsed);
    }

    private void ApplyScreenContentControls(int cpuUsed)
    {
        var context = _context.__Instance;

        // The single most valuable setting for this application: it biases VP8 towards the sharp
        // edges and flat regions that windows are made of instead of camera-like gradients.
        SetControl(context, SetScreenContentMode, 1, "VP8E_SET_SCREEN_CONTENT_MODE");

        // The default threshold skips blocks that changed only slightly, which leaves visible
        // trails behind scrolling text. Screen content wants exactness over speed here.
        SetControl(context, SetStaticThreshold, 0, "VP8E_SET_STATIC_THRESHOLD");

        SetControl(context, SetCpuUsed, cpuUsed, "VP8E_SET_CPUUSED");

        // VP8 multi-threaded encoding works across token partitions, so g_threads only helps if
        // the bitstream is partitioned.
        SetControl(
            context,
            SetTokenPartitions,
            (int)Vp8eTokenPartitions.VP8FOUR_TOKENPARTITION,
            "VP8E_SET_TOKEN_PARTITIONS");

        // Stop keyframes from bursting far above the target and stalling the link.
        SetControl(context, SetMaxIntraBitratePct, 300, "VP8E_SET_MAX_INTRA_BITRATE_PCT");
    }

    private static void SetControl(IntPtr context, int controlId, int value, string operation) =>
        Check(SetControl(context, controlId, value), operation);

    // vpx_codec_control_ is variadic, so the generated bindings for the per-control macros have no
    // entry point to bind to and throw at call time. Declare the real exported function instead.
    [DllImport("vpxmd", EntryPoint = "vpx_codec_control_", CallingConvention = CallingConvention.Cdecl)]
    private static extern VpxCodecErrT SetControl(IntPtr context, int controlId, int value);

    [DllImport("vpxmd", EntryPoint = "vpx_codec_control_", CallingConvention = CallingConvention.Cdecl)]
    private static extern VpxCodecErrT GetControl(IntPtr context, int controlId, ref int value);

    private void WriteImagePlanes(byte[] i420)
    {
        var stride = _image.Stride;
        var chromaWidth = _width / 2;
        var chromaHeight = _height / 2;
        var uOffset = _width * _height;
        var vOffset = uOffset + chromaWidth * chromaHeight;

        CopyPlane(i420, 0, _width, _width, _height, _image.PlaneY, stride[PlaneYIndex]);
        CopyPlane(i420, uOffset, chromaWidth, chromaWidth, chromaHeight, _image.PlaneU, stride[PlaneUIndex]);
        CopyPlane(i420, vOffset, chromaWidth, chromaWidth, chromaHeight, _image.PlaneV, stride[PlaneVIndex]);
    }

    private static void CopyPlane(
        byte[] source,
        int sourceOffset,
        int sourceStride,
        int width,
        int height,
        IntPtr destination,
        int destinationStride)
    {
        for (var y = 0; y < height; y++)
        {
            Marshal.Copy(
                source,
                sourceOffset + (y * sourceStride),
                IntPtr.Add(destination, y * destinationStride),
                width);
        }
    }

    private unsafe byte[]? ReadEncodedFrame()
    {
        byte[]? encoded = null;
        void* iterator = null;

        while (true)
        {
            var packet = vpx_encoder.VpxCodecGetCxData(_context, &iterator);
            if (packet is null)
            {
                break;
            }

            using (packet)
            {
                if (packet.Kind != VpxCodecCxPktKind.VPX_CODEC_CX_FRAME_PKT)
                {
                    continue;
                }

                var frame = packet.data.frame;
                var length = checked((int)frame.Sz);
                if (length <= 0)
                {
                    continue;
                }

                // libvpx emits a single frame packet per encode call unless output partitioning is
                // requested at init time, which we do not do. Concatenate defensively anyway.
                if (encoded is null)
                {
                    encoded = new byte[length];
                    Marshal.Copy(frame.Buf, encoded, 0, length);
                }
                else
                {
                    var combined = new byte[encoded.Length + length];
                    Buffer.BlockCopy(encoded, 0, combined, 0, encoded.Length);
                    Marshal.Copy(frame.Buf, combined, encoded.Length, length);
                    encoded = combined;
                }
            }
        }

        return encoded;
    }

    private static void Check(VpxCodecErrT result, string operation)
    {
        if (result != VpxCodecErrT.VPX_CODEC_OK)
        {
            throw new InvalidOperationException($"{operation} failed: {vpx_codec.VpxCodecErrToString(result)}");
        }
    }
}
