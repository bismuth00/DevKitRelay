using System.Globalization;

namespace DevKitRelay;

internal enum AppMode
{
    Help,
    ListWindows,
    Server,
    Client
}

internal enum VideoDisplayMode
{
    /// <summary>Size the window to the original source window, upscaling a downscaled stream.</summary>
    Source,

    /// <summary>Size the window to the encoded frame, so the image is never upscaled.</summary>
    Frame
}

internal enum VideoFilterMode
{
    Bicubic,
    Nearest
}

internal sealed record CommandLineOptions
{
    public AppMode Mode { get; init; } = AppMode.Help;
    public string WindowQuery { get; init; } = "";
    public string ListenUrl { get; init; } = "http://127.0.0.1:5080";
    public Uri ServerUri { get; init; } = new("ws://127.0.0.1:5080/signal");
    public int FramesPerSecond { get; init; } = 30;
    public uint? VideoBitrateKbps { get; init; }
    public double VideoScale { get; init; } = 1.0;

    /// <summary>
    /// libvpx VP8 cpu-used: negative favours quality, positive favours speed. Lower it if the
    /// measured frame rate keeps up; raise it if encoding is the bottleneck.
    /// </summary>
    public int EncoderCpuUsed { get; init; } = -6;

    public int ClientDurationSeconds { get; init; }
    public VideoDisplayMode DisplayMode { get; init; } = VideoDisplayMode.Source;
    public VideoFilterMode FilterMode { get; init; } = VideoFilterMode.Bicubic;

    public static CommandLineOptions Parse(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            return new CommandLineOptions();
        }

        var mode = args[0].ToLowerInvariant();
        var values = ParseNamedArgs(args.Skip(1).ToArray());

        return mode switch
        {
            "list-windows" => new CommandLineOptions { Mode = AppMode.ListWindows },
            "server" => new CommandLineOptions
            {
                Mode = AppMode.Server,
                WindowQuery = Get(values, "window", required: true),
                ListenUrl = Get(values, "listen", "http://127.0.0.1:5080"),
                FramesPerSecond = GetInt(values, "fps", 30, 1, 60),
                VideoBitrateKbps = GetOptionalUInt(values, "bitrate-kbps", 1, 100000),
                VideoScale = GetDouble(values, "scale", 1.0, 0.1, 1.0),
                EncoderCpuUsed = GetInt(values, "cpu-used", -6, -16, 16)
            },
            "client" => new CommandLineOptions
            {
                Mode = AppMode.Client,
                ServerUri = new Uri(Get(values, "server", "ws://127.0.0.1:5080/signal")),
                ClientDurationSeconds = GetInt(values, "duration", 0, 0, 86400),
                DisplayMode = GetEnum(values, "display", VideoDisplayMode.Source),
                FilterMode = GetEnum(values, "filter", VideoFilterMode.Bicubic)
            },
            _ => throw new ArgumentException($"Unknown mode: {args[0]}")
        };
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
        DevKitRelay

        Usage:
          DevKitRelay list-windows
          DevKitRelay server --window <title-part> [--listen http://127.0.0.1:5080] [--fps 30] [--bitrate-kbps 2500] [--scale 1.0] [--cpu-used -6]
          DevKitRelay client [--server ws://127.0.0.1:5080/signal] [--duration 0] [--display source|frame] [--filter bicubic|nearest]
        """);
    }

    private static Dictionary<string, string> ParseNamedArgs(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument: {args[i]}");
            }

            var key = args[i][2..];
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Missing value for --{key}");
            }

            result[key] = args[++i];
        }

        return result;
    }

    private static string Get(Dictionary<string, string> values, string key, string? defaultValue = null, bool required = false)
    {
        if (values.TryGetValue(key, out var value))
        {
            return value;
        }

        if (required)
        {
            throw new ArgumentException($"--{key} is required.");
        }

        return defaultValue ?? "";
    }

    private static int GetInt(Dictionary<string, string> values, string key, int defaultValue, int min, int max)
    {
        if (!values.TryGetValue(key, out var value))
        {
            return defaultValue;
        }

        if (!int.TryParse(value, out var parsed) || parsed < min || parsed > max)
        {
            throw new ArgumentException($"--{key} must be between {min} and {max}.");
        }

        return parsed;
    }

    private static uint? GetOptionalUInt(Dictionary<string, string> values, string key, uint min, uint max)
    {
        if (!values.TryGetValue(key, out var value))
        {
            return null;
        }

        if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
            parsed < min ||
            parsed > max)
        {
            throw new ArgumentException($"--{key} must be between {min} and {max}.");
        }

        return parsed;
    }

    private static double GetDouble(Dictionary<string, string> values, string key, double defaultValue, double min, double max)
    {
        if (!values.TryGetValue(key, out var value))
        {
            return defaultValue;
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ||
            parsed < min ||
            parsed > max)
        {
            throw new ArgumentException($"--{key} must be between {min} and {max}.");
        }

        return parsed;
    }

    private static TEnum GetEnum<TEnum>(Dictionary<string, string> values, string key, TEnum defaultValue)
        where TEnum : struct, Enum
    {
        if (!values.TryGetValue(key, out var value))
        {
            return defaultValue;
        }

        if (!Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
        {
            throw new ArgumentException(
                $"--{key} must be one of: {string.Join(", ", Enum.GetNames<TEnum>()).ToLowerInvariant()}.");
        }

        return parsed;
    }

    private static bool IsHelp(string value) => value is "-h" or "--help" or "help";
}
