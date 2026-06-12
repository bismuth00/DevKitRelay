namespace DevKitRelay;

internal enum AppMode
{
    Help,
    ListWindows,
    Server,
    Client
}

internal sealed record CommandLineOptions
{
    public AppMode Mode { get; init; } = AppMode.Help;
    public string WindowQuery { get; init; } = "";
    public string ListenUrl { get; init; } = "http://127.0.0.1:5080";
    public Uri ServerUri { get; init; } = new("ws://127.0.0.1:5080/signal");
    public int FramesPerSecond { get; init; } = 10;
    public int ClientDurationSeconds { get; init; }

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
                FramesPerSecond = GetInt(values, "fps", 10, 1, 30)
            },
            "client" => new CommandLineOptions
            {
                Mode = AppMode.Client,
                ServerUri = new Uri(Get(values, "server", "ws://127.0.0.1:5080/signal")),
                ClientDurationSeconds = GetInt(values, "duration", 0, 0, 86400)
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
          DevKitRelay server --window <title-part> [--listen http://127.0.0.1:5080] [--fps 10]
          DevKitRelay client [--server ws://127.0.0.1:5080/signal] [--duration 0]
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

    private static bool IsHelp(string value) => value is "-h" or "--help" or "help";
}
