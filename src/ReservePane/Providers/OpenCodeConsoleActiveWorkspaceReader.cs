using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace ReservePane.Providers;

internal sealed record OpenCodeConsoleActiveWorkspace(
    string AccountId,
    string AccessToken,
    string OrganizationId,
    DateTimeOffset? ExpiresAt);

internal enum OpenCodeConsoleActiveWorkspaceReadOutcome
{
    Success,
    TransientFailure,
    InvalidResponse,
}

internal sealed record OpenCodeConsoleActiveWorkspaceReadResult(
    OpenCodeConsoleActiveWorkspaceReadOutcome Outcome,
    OpenCodeConsoleActiveWorkspace? Workspace = null);

internal interface IOpenCodeConsoleActiveWorkspaceReader
{
    Task<OpenCodeConsoleActiveWorkspaceReadResult> ReadAsync(CancellationToken cancellationToken);
}

internal sealed class OpenCodeConsoleActiveWorkspaceReader : IOpenCodeConsoleActiveWorkspaceReader
{
    private const string ActiveWorkspaceQuery =
        "select a.id as account_id, a.access_token, a.token_expiry, s.active_org_id " +
        "from account_state s join account a on a.id = s.active_account_id " +
        "where s.id = 1 and a.url = 'https://opencode.ai/console' " +
        "and s.active_org_id is not null limit 2;";

    private readonly Func<string, CancellationToken, Task<byte[]?>> _runQuery;

    public OpenCodeConsoleActiveWorkspaceReader()
        : this(OpenCodeConsoleQueryRunner.RunQueryAsync)
    {
    }

    internal OpenCodeConsoleActiveWorkspaceReader(
        Func<string, CancellationToken, Task<byte[]?>> runQuery)
    {
        _runQuery = runQuery;
    }

    public async Task<OpenCodeConsoleActiveWorkspaceReadResult> ReadAsync(
        CancellationToken cancellationToken)
    {
        byte[]? output = await _runQuery(ActiveWorkspaceQuery, cancellationToken)
            .ConfigureAwait(false);
        if (output is null)
        {
            return new(OpenCodeConsoleActiveWorkspaceReadOutcome.TransientFailure);
        }

        if (output.Length > ProviderHttpSafety.MaximumJsonBytes)
        {
            return new(OpenCodeConsoleActiveWorkspaceReadOutcome.InvalidResponse);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(output);
        }
        catch (JsonException)
        {
            return new(OpenCodeConsoleActiveWorkspaceReadOutcome.InvalidResponse);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new(OpenCodeConsoleActiveWorkspaceReadOutcome.InvalidResponse);
            }

            int count = document.RootElement.GetArrayLength();
            if (count == 0)
            {
                return new(OpenCodeConsoleActiveWorkspaceReadOutcome.Success);
            }

            if (count != 1)
            {
                return new(OpenCodeConsoleActiveWorkspaceReadOutcome.InvalidResponse);
            }

            JsonElement row = document.RootElement[0];
            return TryReadWorkspace(row, out OpenCodeConsoleActiveWorkspace? workspace)
                ? new(OpenCodeConsoleActiveWorkspaceReadOutcome.Success, workspace)
                : new(OpenCodeConsoleActiveWorkspaceReadOutcome.InvalidResponse);
        }
    }

    private static bool TryReadWorkspace(
        JsonElement row,
        [NotNullWhen(true)] out OpenCodeConsoleActiveWorkspace? workspace)
    {
        workspace = null;
        if (row.ValueKind != JsonValueKind.Object ||
            !TryReadString(row, "account_id", out string? accountId) ||
            !TryReadString(row, "access_token", out string? accessToken) ||
            !TryReadString(row, "active_org_id", out string? organizationId))
        {
            return false;
        }

        DateTimeOffset? expiresAt = null;
        if (row.TryGetProperty("token_expiry", out JsonElement expiry) &&
            expiry.ValueKind != JsonValueKind.Null)
        {
            if (expiry.ValueKind != JsonValueKind.Number ||
                !expiry.TryGetInt64(out long milliseconds))
            {
                return false;
            }

            try
            {
                expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        workspace = new OpenCodeConsoleActiveWorkspace(
            accountId,
            accessToken,
            organizationId,
            expiresAt);
        return true;
    }

    private static bool TryReadString(
        JsonElement row,
        string propertyName,
        [NotNullWhen(true)] out string? value)
    {
        value = null;
        return row.TryGetProperty(propertyName, out JsonElement element) &&
            element.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value = element.GetString());
    }
}
