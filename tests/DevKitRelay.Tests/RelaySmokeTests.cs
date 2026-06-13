using System.Diagnostics;
using Xunit;

namespace DevKitRelay.Tests;

public sealed class RelaySmokeTests
{
    [Fact]
    public async Task Client_receives_video_stream_and_resizes_to_frame()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = FindRepositoryRoot();
        var appDll = GetApplicationDll(root);
        var port = GetEphemeralPort();
        var notepadFile = Path.Combine(Path.GetTempPath(), $"devkitrelay-smoke-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(notepadFile, "DevKitRelay smoke test window.");

        var notepad = Process.Start(new ProcessStartInfo
        {
            FileName = "notepad.exe",
            Arguments = Quote(notepadFile),
            UseShellExecute = true
        }) ?? throw new InvalidOperationException("Failed to start notepad.");

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1));

            await using var server = ProcessRunner.Start(
                "dotnet",
                Quote(appDll) + $" server --window \"{Path.GetFileName(notepadFile)}\" --listen http://127.0.0.1:{port} --fps 5 --bitrate-kbps 1000 --scale 0.75",
                root);

            Assert.True(
                await server.WaitForOutputAsync($"Listening: http://127.0.0.1:{port}", TimeSpan.FromSeconds(15)),
                FormatFailure("Server did not start listening.", server));

            await using var client = ProcessRunner.Start(
                "dotnet",
                Quote(appDll) + $" client --server ws://127.0.0.1:{port}/signal --duration 6",
                root);

            Assert.True(
                await client.WaitForOutputAsync("Receiving video stream.", TimeSpan.FromSeconds(20)),
                FormatFailure("Client did not reach video receiving state.", client, server));

            Assert.True(
                await client.WaitForOutputAsync("Input DataChannel created.", TimeSpan.FromSeconds(10)),
                FormatFailure("Client did not receive the input data channel.", client, server));

            Assert.True(
                await server.WaitForOutputAsync("Input DataChannel open.", TimeSpan.FromSeconds(10)),
                FormatFailure("Server did not open the input data channel.", server, client));

            Assert.True(
                await client.WaitForOutputAsync("Client window resized for video:", TimeSpan.FromSeconds(10)),
                FormatFailure("Client did not resize to the received video frame.", client, server));

            Assert.True(
                await client.WaitForOutputAsync("Received video frame #1:", TimeSpan.FromSeconds(10)),
                FormatFailure("Client did not decode a video frame.", client, server));
        }
        finally
        {
            await CloseProcessAsync(notepad);
            File.Delete(notepadFile);
        }
    }

    private static async Task CloseProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.CloseMainWindow();
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await process.WaitForExitAsync(closeTimeout.Token);
                }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync();
                    }
                }
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DevKitRelay.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate DevKitRelay.csproj from test output directory.");
    }

    private static string GetApplicationDll(string root)
    {
        var candidates = Directory.GetFiles(
            Path.Combine(root, "bin"),
            "DevKitRelay.dll",
            SearchOption.AllDirectories);

        return candidates
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault(path => path.Contains("net10.0-windows10.0.17763", StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("Could not locate built DevKitRelay.dll. Build the solution before running tests.");
    }

    private static int GetEphemeralPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string Quote(string value) => '"' + value + '"';

    private static string FormatFailure(string message, params ProcessRunner[] runners)
    {
        return message + Environment.NewLine + string.Join(
            Environment.NewLine,
            runners.Select(runner =>
                $"--- stdout ---{Environment.NewLine}{runner.StandardOutput}{Environment.NewLine}--- stderr ---{Environment.NewLine}{runner.StandardError}"));
    }
}
