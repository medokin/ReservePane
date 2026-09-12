using System.Diagnostics;
using System.IO;

namespace ReservePane.Providers;

internal static class OpenCodeConsoleQueryRunner
{
    private const int MaximumDatabaseBusyAttempts = 3;

    internal static async Task<byte[]?> RunQueryAsync(
        string query,
        CancellationToken cancellationToken) => await RunQueryAsync(
            query,
            EffectiveCommandEnvironment.Capture(),
            cancellationToken).ConfigureAwait(false);

    internal static async Task<byte[]?> RunQueryAsync(
        string query,
        EffectiveCommandEnvironment environment,
        CancellationToken cancellationToken) => await RunWithDatabaseBusyRetryAsync(
            (currentQuery, token) => RunQueryOnceAsync(currentQuery, environment, cancellationToken),
            query,
            cancellationToken).ConfigureAwait(false);

    internal static async Task<byte[]?> RunWithDatabaseBusyRetryAsync(
        Func<string, CancellationToken, Task<byte[]?>> runOnce,
        string query,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await runOnce(query, cancellationToken).ConfigureAwait(false);
            }
            catch (OpenCodeCommandException exception)
                when (exception.Failure == OpenCodeCommandFailure.DatabaseBusy &&
                    attempt < MaximumDatabaseBusyAttempts &&
                    !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task<byte[]?> RunQueryOnceAsync(
        string query,
        EffectiveCommandEnvironment environment,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = CreateStartInfo(query, environment) };

        try
        {
            if (!process.Start())
            {
                return null;
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return null;
        }

        Task<byte[]> stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, cancellationToken);
        Task<byte[]> stderr = ReadBoundedAsync(process.StandardError.BaseStream, cancellationToken);
        Task exit = process.WaitForExitAsync(cancellationToken);

        try
        {
            var pending = new List<Task> { stdout, stderr, exit };
            while (pending.Count > 0)
            {
                Task completed = await Task.WhenAny(pending).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
                pending.Remove(completed);
            }

            if (process.ExitCode != 0)
            {
                throw OpenCodeCommandException.FromStderr(
                    process.ExitCode,
                    System.Text.Encoding.UTF8.GetString(stderr.Result));
            }

            return stdout.Result;
        }
        catch
        {
            TryKill(process);
            await ObserveCleanupAsync(process, stdout, stderr).ConfigureAwait(false);
            throw;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string query) =>
        CreateStartInfo(query, EffectiveCommandEnvironment.Capture());

    internal static ProcessStartInfo CreateStartInfo(
        string query,
        EffectiveCommandEnvironment environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(environment);
        string commandInterpreter = Environment.GetEnvironmentVariable("ComSpec")
            ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = commandInterpreter,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        environment.ApplyTo(startInfo);
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("opencode");
        startInfo.ArgumentList.Add("db");
        startInfo.ArgumentList.Add(query);
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add("json");
        return startInfo;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[81920];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read <= ProviderHttpSafety.MaximumJsonBytes)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                throw InvalidOutput();
            }
        }

        return output.ToArray();
    }

    private static async Task ObserveCleanupAsync(
        Process process,
        Task stdout,
        Task stderr)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
        {
        }

        await ObserveAsync(stdout).ConfigureAwait(false);
        await ObserveAsync(stderr).ConfigureAwait(false);
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is
            OperationCanceledException or
            InvalidDataException or
            IOException or
            InvalidOperationException or
            TimeoutException)
        {
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static InvalidDataException InvalidOutput() =>
        new("OpenCode Console query returned invalid data.");
}
