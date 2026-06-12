using System.Diagnostics;
using System.Text;

namespace DevKitRelay.Tests;

internal sealed class ProcessRunner : IAsyncDisposable
{
    private readonly StringBuilder _standardOutput = new();
    private readonly StringBuilder _standardError = new();
    private readonly TaskCompletionSource _exitCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Process Process { get; }

    public string StandardOutput => _standardOutput.ToString();
    public string StandardError => _standardError.ToString();

    private ProcessRunner(Process process)
    {
        Process = process;
        Process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (_standardOutput)
                {
                    _standardOutput.AppendLine(e.Data);
                }
            }
        };
        Process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (_standardError)
                {
                    _standardError.AppendLine(e.Data);
                }
            }
        };
        Process.Exited += (_, _) => _exitCompletion.TrySetResult();
    }

    public static ProcessRunner Start(string fileName, string arguments, string workingDirectory)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            },
            EnableRaisingEvents = true
        };

        var runner = new ProcessRunner(process);
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process: {fileName}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return runner;
    }

    public async Task<bool> WaitForOutputAsync(string text, TimeSpan timeout)
    {
        var timeoutAt = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < timeoutAt)
        {
            if (StandardOutput.Contains(text, StringComparison.Ordinal) ||
                StandardError.Contains(text, StringComparison.Ordinal))
            {
                return true;
            }

            if (Process.HasExited)
            {
                return false;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        return false;
    }

    public async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        var completed = await Task.WhenAny(_exitCompletion.Task, Task.Delay(timeout)).ConfigureAwait(false);
        return completed == _exitCompletion.Task;
    }

    public async ValueTask DisposeAsync()
    {
        if (!Process.HasExited)
        {
            try
            {
                Process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process can exit between HasExited and Kill.
            }

            await WaitForExitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        Process.Dispose();
    }
}
