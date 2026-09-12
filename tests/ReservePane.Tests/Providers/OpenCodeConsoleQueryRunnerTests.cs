using System.Text;
using ReservePane.Core;
using ReservePane.Providers;
using ReservePane.Tests.Support;

namespace ReservePane.Tests.Providers;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class OpenCodeConsoleQueryRunnerTests
{
    [Fact]
    public void CreateStartInfo_UsesTheWindowsCommandShimWithFixedArgumentOrder()
    {
        // Catches CreateProcess failing to resolve Volta's extensionless and .cmd OpenCode shims.
        const string query = "select 1 as ok;";

        System.Diagnostics.ProcessStartInfo startInfo =
            OpenCodeConsoleQueryRunner.CreateStartInfo(query);

        Assert.EndsWith("cmd.exe", startInfo.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal(
            ["/d", "/c", "opencode", "db", query, "--format", "json"],
            startInfo.ArgumentList);
    }

    [Fact]
    public void CreateStartInfo_CapturesCommandSearchEnvironmentForTheFetchAttempt()
    {
        // Catches cmd.exe inheriting a later or stale process PATH instead of the fetch snapshot.
        const string refreshedPath = @"C:\refreshed-tools;C:\windows-tools";
        const string refreshedPathExtensions = ".EXE;.CMD";
        string? originalPath = Environment.GetEnvironmentVariable("PATH");
        string? originalPathExtensions = Environment.GetEnvironmentVariable("PATHEXT");
        System.Diagnostics.ProcessStartInfo startInfo;

        try
        {
            Environment.SetEnvironmentVariable("PATH", refreshedPath);
            Environment.SetEnvironmentVariable("PATHEXT", refreshedPathExtensions);
            startInfo = OpenCodeConsoleQueryRunner.CreateStartInfo("select 1;");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("PATHEXT", originalPathExtensions);
        }

        Assert.StartsWith(
            refreshedPath,
            startInfo.Environment["PATH"],
            StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(
            refreshedPathExtensions,
            startInfo.Environment["PATHEXT"],
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateStartInfo_AppliesTheRefreshedEffectiveCommandEnvironment()
    {
        // Catches discovery and cmd.exe execution using different PATH or PATHEXT values.
        string? ReadVariable(string name, EnvironmentVariableTarget target) => (name, target) switch
        {
            ("PATH", EnvironmentVariableTarget.Process) => @"C:\process-tools",
            ("PATH", EnvironmentVariableTarget.User) => @"C:\user-tools",
            ("PATH", EnvironmentVariableTarget.Machine) => @"C:\machine-tools",
            ("PATHEXT", EnvironmentVariableTarget.Process) => ".EXE",
            ("PATHEXT", EnvironmentVariableTarget.User) => ".CMD",
            ("PATHEXT", EnvironmentVariableTarget.Machine) => ".BAT",
            _ => null,
        };
        EffectiveCommandEnvironment environment = EffectiveCommandEnvironment.Capture(ReadVariable);

        System.Diagnostics.ProcessStartInfo startInfo =
            OpenCodeConsoleQueryRunner.CreateStartInfo("select 1;", environment);

        Assert.Equal(
            @"C:\process-tools;C:\user-tools;C:\machine-tools",
            startInfo.Environment["PATH"]);
        Assert.Equal(".EXE;.CMD;.BAT", startInfo.Environment["PATHEXT"]);
    }

    [Fact]
    public async Task RunWithDatabaseBusyRetryAsync_CallerCancellationPropagates()
    {
        // Catches the child command outliving the provider timeout or application shutdown.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<byte[]?> RunOnce(string query, CancellationToken cancellationToken)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        }

        using var cancellation = new CancellationTokenSource();

        Task<byte[]?> read = OpenCodeConsoleQueryRunner.RunWithDatabaseBusyRetryAsync(
            RunOnce,
            "select 1;",
            cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    }

    [Fact]
    public async Task RunQueryAsync_NonZeroExitReportsSanitizedFailureMetadata()
    {
        // Break caught: a local OpenCode command failure is collapsed to an unexplained transient result.
        using var directory = new TemporaryDirectory();
        string commandPath = Path.Combine(directory.Path, "opencode.cmd");
        await File.WriteAllTextAsync(
            commandPath,
            "@echo database is locked: sensitive-account-id 1>&2\r\n@exit /b 17\r\n");
        var environment = new EffectiveCommandEnvironment(directory.Path, ".CMD");

        OpenCodeCommandException exception = await Assert.ThrowsAsync<OpenCodeCommandException>(
            () => OpenCodeConsoleQueryRunner.RunQueryAsync(
                "select 1;",
                environment,
                CancellationToken.None));

        Assert.Equal(OpenCodeCommandFailure.DatabaseBusy, exception.Failure);
        Assert.Equal(17, exception.ExitCode);
        Assert.DoesNotContain("sensitive-account-id", exception.Message, StringComparison.Ordinal);

        string logPath = Path.Combine(directory.Path, "diagnostic.log");
        var log = new RollingFileLog(logPath);
        log.Write(
            LogArea.Provider,
            LogOutcome.Failed,
            exception: exception,
            providerId: "opencode-company-seat",
            providerOutcome: ProviderFetchOutcome.TransientFailure);
        string contents = await File.ReadAllTextAsync(logPath);

        Assert.Contains("command-failure=database-busy", contents, StringComparison.Ordinal);
        Assert.Contains("process-exit-code=17", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-account-id", contents, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("database is busy", "DatabaseBusy")]
    [InlineData("operation timed out", "TimedOut")]
    [InlineData("opencode is not recognized", "CommandNotFound")]
    [InlineData("unexpected local failure", "Failed")]
    public void FromStderr_ClassifiesWithoutRetainingRawOutput(
        string stderr,
        string expected)
    {
        // Break caught: a known failure is misclassified or raw command output survives classification.
        OpenCodeCommandException exception = OpenCodeCommandException.FromStderr(23, stderr);

        Assert.Equal(expected, exception.Failure.ToString());
        Assert.Equal(23, exception.ExitCode);
        Assert.DoesNotContain(stderr, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunWithDatabaseBusyRetryAsync_DatabaseBusyIsRetriedUntilItSucceeds()
    {
        // Break caught: a SQLite lock held by a running opencode instance discards valid quota data.
        int attempts = 0;

        byte[]? output = await OpenCodeConsoleQueryRunner.RunWithDatabaseBusyRetryAsync(
            (_, _) =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw OpenCodeCommandException.FromStderr(126, "database is locked");
                }

                return Task.FromResult<byte[]?>(Encoding.UTF8.GetBytes("[]"));
            },
            "select 1;",
            CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.Equal("[]", Encoding.UTF8.GetString(output!));
    }

    [Fact]
    public async Task RunWithDatabaseBusyRetryAsync_GivesUpAfterMaximumAttempts()
    {
        // Break caught: an eternal lock retrying forever or a non-busy failure being retried.
        int attempts = 0;

        OpenCodeCommandException exception = await Assert.ThrowsAsync<OpenCodeCommandException>(
            () => OpenCodeConsoleQueryRunner.RunWithDatabaseBusyRetryAsync(
                (_, _) =>
                {
                    attempts++;
                    throw OpenCodeCommandException.FromStderr(126, "database is locked");
                },
                "select 1;",
                CancellationToken.None));

        Assert.Equal(OpenCodeCommandFailure.DatabaseBusy, exception.Failure);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task RunWithDatabaseBusyRetryAsync_OtherFailuresAreNotRetried()
    {
        // Break caught: a non-busy failure being retried and duplicating side effects.
        int attempts = 0;

        OpenCodeCommandException exception = await Assert.ThrowsAsync<OpenCodeCommandException>(
            () => OpenCodeConsoleQueryRunner.RunWithDatabaseBusyRetryAsync(
                (_, _) =>
                {
                    attempts++;
                    throw OpenCodeCommandException.FromStderr(1, "unexpected local failure");
                },
                "select 1;",
                CancellationToken.None));

        Assert.Equal(OpenCodeCommandFailure.Failed, exception.Failure);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task RunWithDatabaseBusyRetryAsync_CancellationStopsTheRetry()
    {
        // Break caught: a canceled poll continuing to spawn locked-database command attempts.
        int attempts = 0;
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => OpenCodeConsoleQueryRunner.RunWithDatabaseBusyRetryAsync(
                (_, token) =>
                {
                    attempts++;
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult<byte[]?>(null);
                },
                "select 1;",
                cancellation.Token));
    }
}
