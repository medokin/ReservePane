# QuotaGlass Overlay Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a tray-resident Windows application that reads existing Claude and Codex credentials, polls Claude, Codex, and Ollama status, and shows quota state in a popup and optional corner overlay.

**Architecture:** Keep provider adapters independent from scheduling and UI. Providers return immutable snapshots, `StatusPoller` owns the current report, and pure policies derive severity, alerts, tray state, and display text. WPF windows share one `ProviderCard` control while small Win32 wrappers supply the tray icon, no-activate overlay, hotkey, toast, idle detection, and autostart behavior.

**Tech Stack:** C# 14, .NET 10 WPF, xUnit, `System.Text.Json`, `System.Collections.Immutable`, Windows Forms `NotifyIcon`, Win32 P/Invoke, and `CommunityToolkit.WinUI.Notifications` 7.1.2.

**Spec:** `docs/superpowers/specs/2026-08-25-quotaglass-overlay-design.md`

## Global Constraints

- Target `net10.0-windows10.0.19041.0`; Windows x64 is the only supported platform.
- Use the installed .NET SDK 10.0.302.
- Poll every 60 seconds normally and every 5 minutes while locked or both on battery and idle.
- Give every provider a 10 second timeout and dispatch available providers in parallel.
- Use one configurable threshold pair, defaulting to warning 80 and critical 95, for severity, bar color, tray color, and alerts.
- Compile the three providers into the application. Do not add discovery, assembly scanning, or config-driven provider types.
- Read Claude and Codex credential files only. Never read or use refresh tokens and never write either credential file.
- Never log HTTP bodies, HTTP headers, access tokens, account identifiers, user identifiers, or email addresses.
- Store settings and the capped rolling log under `%APPDATA%\QuotaGlass`.
- Keep the corner overlay disabled by default and ensure it cannot activate or steal focus.
- Treat money as an `InfoLine` only. Never render spend as a percentage bar or infer a spending ceiling.
- Use conventional commit messages with lowercase descriptions and append `Created with Codex` to commit messages as change metadata.

## File Map

- `QuotaGlass.slnx` - solution entry point.
- `Directory.Build.props` - nullable, implicit usings, deterministic build, warnings policy.
- `src/QuotaGlass/QuotaGlass.csproj` - Windows desktop executable and toast dependency.
- `src/QuotaGlass/Model/*` - immutable provider/report/alert data and pure severity policy.
- `src/QuotaGlass/Providers/*` - credential readers, HTTP mapping, and static provider registry.
- `src/QuotaGlass/Core/SettingsStore.cs` - defaults, atomic JSON persistence, and reload-on-change.
- `src/QuotaGlass/Core/RollingFileLog.cs` - body-free 1 MB log with one rotation.
- `src/QuotaGlass/Core/ThresholdWatcher.cs` - crossing detection and cycle-key deduplication.
- `src/QuotaGlass/Core/StatusPoller.cs` - parallel fetch, timeout, failure retention, and cadence.
- `src/QuotaGlass/Platform/*` - registry autostart, toast, hotkey, tray icon drawing, and activity state.
- `src/QuotaGlass/Ui/*` - shared provider card, popup, overlay, converters, and tray host.
- `tests/QuotaGlass.Tests/*` - provider, core, and pure UI-policy tests plus sanitized fixtures.
- `docs/manual-test-checklist.md` - Windows shell, multi-monitor, DPI, lock, RDP, and autostart checks.

---

### Task 1: Solution, Domain Model, and Severity Policy

**Files:**
- Create: `QuotaGlass.slnx`
- Create: `Directory.Build.props`
- Create: `src/QuotaGlass/QuotaGlass.csproj`
- Create: `src/QuotaGlass/App.xaml`
- Create: `src/QuotaGlass/App.xaml.cs`
- Create: `src/QuotaGlass/Model/Enums.cs`
- Create: `src/QuotaGlass/Model/UsageWindow.cs`
- Create: `src/QuotaGlass/Model/ProviderSnapshot.cs`
- Create: `src/QuotaGlass/Model/StatusReport.cs`
- Create: `src/QuotaGlass/Model/StatusAlert.cs`
- Create: `src/QuotaGlass/Model/SeverityPolicy.cs`
- Create: `tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj`
- Test: `tests/QuotaGlass.Tests/Model/SeverityPolicyTests.cs`

**Interfaces:**
- Produces: `HealthState`, `Severity`, `AlertKind`, `UsageWindow`, `InfoLine`, `ProviderSnapshot`, `StatusReport`, `StatusAlert`, and `SeverityPolicy.FromPercent(double?, double, double)`.
- Consumes: none.

- [ ] **Step 1: Scaffold the solution and projects**

Run:

```powershell
dotnet new sln -n QuotaGlass --format slnx
dotnet new wpf -n QuotaGlass -o src/QuotaGlass -f net10.0
dotnet new xunit -n QuotaGlass.Tests -o tests/QuotaGlass.Tests -f net10.0
dotnet sln QuotaGlass.slnx add src/QuotaGlass/QuotaGlass.csproj tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj
dotnet add tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj reference src/QuotaGlass/QuotaGlass.csproj
dotnet add src/QuotaGlass/QuotaGlass.csproj package CommunityToolkit.WinUI.Notifications --version 7.1.2
```

Set `TargetFramework` in both projects to `net10.0-windows10.0.19041.0`. In the application project set `UseWPF`, `UseWindowsForms`, and `EnableWindowsTargeting` to `true`; set `OutputType` to `WinExe`. In the test project set `EnableWindowsTargeting` to `true`.

Create `Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>14</LangVersion>
    <Deterministic>true</Deterministic>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Write the failing severity tests**

```csharp
using QuotaGlass.Model;

namespace QuotaGlass.Tests.Model;

public sealed class SeverityPolicyTests
{
    [Theory]
    [InlineData(null, Severity.Normal)]
    [InlineData(79.99, Severity.Normal)]
    [InlineData(80, Severity.Warning)]
    [InlineData(94.99, Severity.Warning)]
    [InlineData(95, Severity.Critical)]
    public void FromPercent_UsesConfiguredThresholds(double? percent, Severity expected)
    {
        Assert.Equal(expected, SeverityPolicy.FromPercent(percent, 80, 95));
    }

}
```

- [ ] **Step 3: Run the focused tests and verify failure**

Run: `dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj --filter FullyQualifiedName~SeverityPolicyTests`

Expected: compilation fails because `QuotaGlass.Model` does not exist.

- [ ] **Step 4: Add the immutable model and minimal severity implementation**

Use these exact public shapes:

```csharp
public enum HealthState { Ok, Degraded, AuthExpired, Unreachable }
public enum Severity { Normal, Warning, Critical }
public enum AlertKind { Warning, Critical, LimitReached, AuthExpired }

public sealed record UsageWindow(
    string Label, double? Percent, DateTimeOffset? ResetsAt, Severity Severity);

public sealed record InfoLine(string Label, string Value);

public sealed record ProviderSnapshot(
    string Id,
    string Label,
    HealthState Health,
    string? PlanLabel,
    ImmutableArray<UsageWindow> Windows,
    ImmutableArray<InfoLine> Info,
    string? Error,
    DateTimeOffset FetchedAt,
    int ConsecutiveFailures);

public sealed record StatusReport(
    DateTimeOffset FetchedAt,
    ImmutableArray<ProviderSnapshot> Providers)
{
    public static StatusReport Empty(DateTimeOffset now) =>
        new(now, ImmutableArray<ProviderSnapshot>.Empty);
}

public sealed record StatusAlert(
    string ProviderId,
    string ProviderLabel,
    string? WindowLabel,
    AlertKind Kind,
    double? Percent,
    DateTimeOffset? CycleResetsAt,
    string Message);
```

Implement `SeverityPolicy.FromPercent` to validate `0 <= warning < critical <= 100`, return `Normal` for `null`, and compare the vendor percentage against the supplied thresholds. Do not clamp or reject a vendor value above 100, because it still unambiguously maps to critical.

- [ ] **Step 5: Run the model tests and full build**

Run:

```powershell
dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj --filter FullyQualifiedName~SeverityPolicyTests
dotnet build QuotaGlass.slnx
```

Expected: all tests pass and the solution builds with zero warnings.

- [ ] **Step 6: Commit the foundation**

```powershell
git add QuotaGlass.slnx Directory.Build.props src tests
git commit -m "feat(core): add status domain model`n`nCreated with Codex"
```

---

### Task 2: Claude Provider and Fixture Safety

**Files:**
- Create: `src/QuotaGlass/Providers/IStatusProvider.cs`
- Create: `src/QuotaGlass/Providers/ClaudeProvider.cs`
- Create: `tests/QuotaGlass.Tests/Support/StubHttpMessageHandler.cs`
- Create: `tests/QuotaGlass.Tests/Support/TemporaryDirectory.cs`
- Create: `tests/QuotaGlass.Tests/Providers/ClaudeProviderTests.cs`
- Create: `tests/QuotaGlass.Tests/Fixtures/FixtureSecurityTests.cs`
- Create: `tests/QuotaGlass.Tests/fixtures/claude-usage.json`
- Create: `tests/QuotaGlass.Tests/fixtures/claude-profile.json`

**Interfaces:**
- Consumes: model records and `SeverityPolicy.FromPercent` from Task 1.
- Produces: `IStatusProvider` and `ClaudeProvider(string credentialPath, HttpMessageHandler handler, Func<double?, Severity> severityFromPercent, TimeProvider? timeProvider = null)`.

- [ ] **Step 1: Add sanitized Claude fixtures and their security guard**

Use fixture values from the approved spec with all identity fields removed. `claude-usage.json` must include null top-level windows, `limits` entries for session 2 and weekly 95, and spend amount minor 32252 EUR with a null limit. `claude-profile.json` must contain only:

```json
{ "organization": { "seat_tier": "team_standard" } }
```

Write a test that enumerates `tests/QuotaGlass.Tests/fixtures/*`, rejects case-insensitive matches for `Bearer\s+[A-Za-z0-9._-]{20,}`, JWT shape `[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}`, and email shape `\b[^\s@]+@[^\s@]+\.[^\s@]+\b`.

- [ ] **Step 2: Write failing Claude mapping and expiry tests**

Cover these assertions in `ClaudeProviderTests`:

```csharp
[Fact]
public async Task FetchAsync_MapsLimitsAndUncappedSpend()
{
    ProviderSnapshot snapshot = await CreateProviderWithFixtures().FetchAsync(TestContext.Current.CancellationToken);

    Assert.Equal(HealthState.Ok, snapshot.Health);
    Assert.Equal("team_standard", snapshot.PlanLabel);
    Assert.Collection(snapshot.Windows,
        session =>
        {
            Assert.Equal("session", session.Label);
            Assert.Equal(2d, session.Percent);
            Assert.Equal(Severity.Normal, session.Severity);
        },
        weekly =>
        {
            Assert.Equal("weekly", weekly.Label);
            Assert.Equal(95d, weekly.Percent);
            Assert.Equal(Severity.Critical, weekly.Severity);
        });
    Assert.Collection(snapshot.Info,
        line => Assert.Equal(new InfoLine("Spend", "EUR 322.52"), line),
        line => Assert.Equal(new InfoLine("Budget", "no cap set"), line));
}

[Fact]
public async Task FetchAsync_VendorSeverityOverridesDerivedSeverity()
{
    ProviderSnapshot snapshot = await CreateProviderWithFixtures(percent =>
            SeverityPolicy.FromPercent(percent, 50, 60))
        .FetchAsync(TestContext.Current.CancellationToken);
    Assert.Equal(Severity.Normal, snapshot.Windows[0].Severity);
}

[Fact]
public async Task FetchAsync_ExpiredCredentialSkipsHttpAndReturnsAuthExpired()
{
    var handler = new StubHttpMessageHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must not run"));
    ClaudeProvider provider = CreateProvider(handler, expiresAtUnixMilliseconds: 0);

    ProviderSnapshot snapshot = await provider.FetchAsync(TestContext.Current.CancellationToken);

    Assert.Equal(HealthState.AuthExpired, snapshot.Health);
    Assert.Equal("re-auth: run claude login", snapshot.Error);
    Assert.Equal(0, handler.RequestCount);
}
```

Also cover missing optional fields, HTTP 401 mapping to `AuthExpired`, and profile caching for one hour.

- [ ] **Step 3: Run Claude tests and verify failure**

Run: `dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj --filter "FullyQualifiedName~ClaudeProviderTests|FullyQualifiedName~FixtureSecurityTests"`

Expected: compilation fails because `ClaudeProvider` and support helpers are absent.

- [ ] **Step 4: Implement credential-safe Claude mapping**

Define:

```csharp
public interface IStatusProvider
{
    string Id { get; }
    string Label { get; }
    Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken);
}
```

`ClaudeProvider` must:

- Parse only `claudeAiOauth.accessToken` and `claudeAiOauth.expiresAt` from the credential JSON.
- Check expiry against `TimeProvider.GetUtcNow()` before creating an HTTP request.
- Send usage requests to `https://api.anthropic.com/api/oauth/usage` with bearer auth and `anthropic-beta: oauth-2025-04-20`.
- Send profile requests to `/api/oauth/profile` and cache `organization.seat_tier` for one hour.
- Require a JSON content type before parsing.
- Build windows only from `limits[]`, preserving vendor `normal`, `warning`, and `critical` severity values.
- Read every usage field defensively with nullable JSON properties.
- Render spend from `spend.used` by dividing `amount_minor` by `10^exponent`; when `spend.limit` is null, use the exact no-cap text from the test.
- Return `AuthExpired` on 401 or 403 and never expose a token in `Error` or an exception message.

Use `HttpRequestMessage` per call and clear no shared default authorization header.

- [ ] **Step 5: Run provider tests and fixture scan**

Run:

```powershell
dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj --filter "FullyQualifiedName~ClaudeProviderTests|FullyQualifiedName~FixtureSecurityTests"
dotnet test QuotaGlass.slnx
```

Expected: all tests pass.

- [ ] **Step 6: Commit the Claude provider**

```powershell
git add src/QuotaGlass/Providers tests/QuotaGlass.Tests
git commit -m "feat(providers): add claude usage provider`n`nCreated with Codex"
```

---

### Task 3: Codex Provider

**Files:**
- Create: `src/QuotaGlass/Providers/CodexProvider.cs`
- Create: `tests/QuotaGlass.Tests/Providers/CodexProviderTests.cs`
- Create: `tests/QuotaGlass.Tests/fixtures/codex-wham.json`
- Create: `tests/QuotaGlass.Tests/fixtures/codex-html-200.html`

**Interfaces:**
- Consumes: `IStatusProvider`, model records, and severity policy.
- Produces: `CodexProvider(string credentialPath, HttpMessageHandler handler, Func<double?, Severity> severityFromPercent, TimeProvider? timeProvider = null)`.

- [ ] **Step 1: Add sanitized Codex fixtures**

Copy the approved spec payload into `codex-wham.json`, retaining `plan_type`, primary 7 day window, and both additional windows, while omitting identity data. Put a minimal `<html><body>not json</body></html>` document in `codex-html-200.html`; the fixture security test from Task 2 must include both files automatically.

- [ ] **Step 2: Write failing Codex tests**

```csharp
[Fact]
public async Task FetchAsync_MapsUnixResetAndAdditionalWindows()
{
    ProviderSnapshot snapshot = await CreateProvider("codex-wham.json", "application/json")
        .FetchAsync(TestContext.Current.CancellationToken);

    Assert.Equal("prolite", snapshot.PlanLabel);
    Assert.Equal("7d", snapshot.Windows[0].Label);
    Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1788272097), snapshot.Windows[0].ResetsAt);
    Assert.Contains(snapshot.Windows, w => w.Label == "GPT-5.3-Codex-Spark 5h");
    Assert.Contains(snapshot.Windows, w => w.Label == "GPT-5.3-Codex-Spark 7d");
}

[Fact]
public async Task FetchAsync_HtmlUnder200ReturnsDegradedSnapshot()
{
    ProviderSnapshot snapshot = await CreateProvider("codex-html-200.html", "text/html")
        .FetchAsync(TestContext.Current.CancellationToken);

    Assert.Equal(HealthState.Degraded, snapshot.Health);
    Assert.Equal("Codex usage endpoint returned non-JSON content", snapshot.Error);
    Assert.Empty(snapshot.Windows);
}
```

Also assert the outgoing request has bearer auth and the exact `chatgpt-account-id` value loaded from `/tokens/account_id`, HTTP 401 maps to `AuthExpired` with `re-auth: run codex login`, a null secondary window is ignored, and seconds map to `5h`, `7d`, or an invariant compact duration.

- [ ] **Step 3: Run Codex tests and verify failure**

Run: `dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj --filter FullyQualifiedName~CodexProviderTests`

Expected: compilation fails because `CodexProvider` is absent.

- [ ] **Step 4: Implement Codex request and mapping**

`CodexProvider` must read only `/tokens/access_token` and `/tokens/account_id`, call `https://chatgpt.com/backend-api/wham/usage`, add both required headers per request, and reject a response unless `Content-Type.MediaType` equals `application/json` case-insensitively. Map `reset_at` with `DateTimeOffset.FromUnixTimeSeconds`. Map `limit_window_seconds` with:

```csharp
private static string FormatWindow(int seconds) => seconds switch
{
    18_000 => "5h",
    604_800 => "7d",
    _ when seconds % 86_400 == 0 => $"{seconds / 86_400}d",
    _ when seconds % 3_600 == 0 => $"{seconds / 3_600}h",
    _ => $"{seconds}s"
};
```

Label the top-level primary and secondary windows with only the compact duration. Prefix additional windows with `${limit_name} `. Derive severity with the configured thresholds and preserve `rate_limit.limit_reached` as critical.

- [ ] **Step 5: Run Codex and fixture tests**

Run:

```powershell
dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj --filter "FullyQualifiedName~CodexProviderTests|FullyQualifiedName~FixtureSecurityTests"
dotnet test QuotaGlass.slnx
```

Expected: all tests pass.

- [ ] **Step 6: Commit the Codex provider**

```powershell
git add src/QuotaGlass/Providers/CodexProvider.cs tests/QuotaGlass.Tests
git commit -m "feat(providers): add codex usage provider`n`nCreated with Codex"
```

---

### Task 4: Ollama Provider

**Files:**
- Create: `src/QuotaGlass/Providers/OllamaProvider.cs`
- Create: `tests/QuotaGlass.Tests/Providers/OllamaProviderTests.cs`
- Create: `tests/QuotaGlass.Tests/fixtures/ollama-version.json`
- Create: `tests/QuotaGlass.Tests/fixtures/ollama-ps.json`

**Interfaces:**
- Consumes: `IStatusProvider` and model records.
- Produces: `OllamaProvider(HttpMessageHandler handler, TimeProvider? timeProvider = null)`.

- [ ] **Step 1: Add Ollama fixtures and failing tests**

Use `{ "version": "0.32.15" }` for version and `{ "models": [] }` for process state. Write:

```csharp
[Fact]
public async Task FetchAsync_ProducesInfoWithoutUsageWindows()
{
    ProviderSnapshot snapshot = await CreateFixtureProvider()
        .FetchAsync(TestContext.Current.CancellationToken);

    Assert.Equal(HealthState.Ok, snapshot.Health);
    Assert.Empty(snapshot.Windows);
    Assert.Contains(new InfoLine("Version", "0.32.15"), snapshot.Info);
    Assert.Contains(new InfoLine("Loaded models", "0"), snapshot.Info);
}

[Fact]
public async Task FetchAsync_ConnectionRefusedIsQuietlyUnreachable()
{
    var handler = new StubHttpMessageHandler(_ =>
        throw new HttpRequestException("refused", null, HttpStatusCode.ServiceUnavailable));

    ProviderSnapshot snapshot = await new OllamaProvider(handler)
        .FetchAsync(TestContext.Current.CancellationToken);

    Assert.Equal(HealthState.Unreachable, snapshot.Health);
    Assert.Null(snapshot.Error);
    Assert.Empty(snapshot.Windows);
}
```

- [ ] **Step 2: Run Ollama tests and verify failure**

Run: `dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj --filter FullyQualifiedName~OllamaProviderTests`

Expected: compilation fails because `OllamaProvider` is absent.

- [ ] **Step 3: Implement Ollama mapping**

Call `http://localhost:11434/api/version` and `/api/ps`. Return version and loaded model count as `InfoLine` values, no windows, and `Ok`. Catch `HttpRequestException` and return `Unreachable` with null `Error`; do not catch cancellation requested by the caller.

- [ ] **Step 4: Run provider and full tests**

Run:

```powershell
dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj --filter FullyQualifiedName~OllamaProviderTests
dotnet test QuotaGlass.slnx
```

Expected: all tests pass.

- [ ] **Step 5: Commit the Ollama provider**

```powershell
git add src/QuotaGlass/Providers/OllamaProvider.cs tests/QuotaGlass.Tests
git commit -m "feat(providers): add ollama status provider`n`nCreated with Codex"
```

---

### Task 5: Settings, Paths, and Safe Rolling Log

**Files:**
- Create: `src/QuotaGlass/Core/AppPaths.cs`
- Create: `src/QuotaGlass/Core/AppSettings.cs`
- Create: `src/QuotaGlass/Core/SettingsStore.cs`
- Create: `src/QuotaGlass/Core/RollingFileLog.cs`
- Create: `tests/QuotaGlass.Tests/Core/SettingsStoreTests.cs`
- Create: `tests/QuotaGlass.Tests/Core/RollingFileLogTests.cs`

**Interfaces:**
- Consumes: provider IDs `claude`, `codex`, and `ollama`.
- Produces: `AppPaths`, `AppSettings.Default`, `SettingsStore.LoadAsync`, `SettingsStore.SaveAsync`, `SettingsStore.Changed`, and `RollingFileLog.Write(string area, string outcome, int? statusCode = null, Exception? exception = null)`.

- [ ] **Step 1: Write failing settings tests**

Cover:

```csharp
[Fact]
public async Task LoadAsync_MissingFileReturnsDocumentedDefaults()
{
    AppSettings settings = await CreateStoreAtMissingPath().LoadAsync(CancellationToken.None);
    Assert.Equal(TimeSpan.FromSeconds(60), settings.PollInterval);
    Assert.Equal(TimeSpan.FromMinutes(5), settings.IdleInterval);
    Assert.False(settings.OverlayVisible);
    Assert.Equal(80, settings.WarningPercent);
    Assert.Equal(95, settings.CriticalPercent);
    Assert.Equal("Ctrl+Alt+A", settings.Hotkey);
    Assert.Equal(["claude", "codex", "ollama"], settings.Providers.Keys.Order());
}

[Fact]
public async Task LoadAsync_MalformedFileFallsBackWithoutThrowing()
{
    await File.WriteAllTextAsync(_path, "{broken", CancellationToken.None);
    Assert.Equal(AppSettings.Default, await _store.LoadAsync(CancellationToken.None));
}

[Fact]
public async Task SaveAsync_RoundTripsAndLeavesNoTemporaryFile()
{
    AppSettings expected = AppSettings.Default with { OverlayVisible = true, WarningPercent = 75 };
    await _store.SaveAsync(expected, CancellationToken.None);
    AppSettings actual = await _store.LoadAsync(CancellationToken.None);
    Assert.Equal(expected.OverlayVisible, actual.OverlayVisible);
    Assert.Equal(expected.WarningPercent, actual.WarningPercent);
    Assert.Equal(expected.CriticalPercent, actual.CriticalPercent);
    Assert.Equal(expected.Providers.OrderBy(pair => pair.Key), actual.Providers.OrderBy(pair => pair.Key));
    Assert.False(File.Exists(_path + ".tmp"));
}
```

Add validation tests that reset the complete settings object to defaults when thresholds do not satisfy `0 <= warning < critical <= 100`, intervals are non-positive, or the providers map omits a compiled provider.

- [ ] **Step 2: Write failing rolling log tests**

Write one test that exceeds 1,048,576 bytes and asserts `log.txt` and `log.1.txt` are each at most that size, and one test that passes an exception containing `Bearer secret@example.com` and asserts neither the bearer value nor email is present. The logger may record only exception type, not exception message or stack trace.

- [ ] **Step 3: Run focused tests and verify failure**

Run: `dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj --filter "FullyQualifiedName~SettingsStoreTests|FullyQualifiedName~RollingFileLogTests"`

Expected: compilation fails because settings and logging types are absent.

- [ ] **Step 4: Implement paths and settings**

Use these types:

```csharp
public enum OverlayCorner { TopLeft, TopRight, BottomLeft, BottomRight, Custom }
public sealed record ProviderSettings;
public sealed record OverlayPosition(double X, double Y);

public sealed record AppSettings(
    TimeSpan PollInterval,
    TimeSpan IdleInterval,
    ImmutableDictionary<string, ProviderSettings> Providers,
    bool OverlayVisible,
    OverlayCorner OverlayCorner,
    string? OverlayMonitorId,
    OverlayPosition? OverlayPosition,
    string Hotkey,
    double WarningPercent,
    double CriticalPercent,
    bool Autostart)
{
    public static AppSettings Default { get; } = new(
        TimeSpan.FromSeconds(60),
        TimeSpan.FromMinutes(5),
        ImmutableDictionary<string, ProviderSettings>.Empty
            .Add("claude", new())
            .Add("codex", new())
            .Add("ollama", new()),
        false,
        OverlayCorner.BottomRight,
        null,
        null,
        "Ctrl+Alt+A",
        80,
        95,
        false);
}
```

These defaults are the only fallback values: 60 seconds, 5 minutes, all three built-in providers registered for discovery, overlay hidden, bottom-right corner, null monitor and position, `Ctrl+Alt+A`, 80, 95, and autostart false.

`AppPaths.FromEnvironment()` must resolve:

- `%USERPROFILE%\.claude\.credentials.json`
- `%USERPROFILE%\.codex\auth.json`
- `%APPDATA%\QuotaGlass\settings.json`
- `%APPDATA%\QuotaGlass\log.txt`

`SettingsStore.SaveAsync` writes UTF-8 JSON to a same-directory `.tmp`, flushes it, then calls `File.Move(temp, path, true)`. `FileSystemWatcher` observes the exact settings filename, debounces duplicate notifications for 250 ms, reloads, and raises `Changed` only with a valid value. Malformed external writes keep the prior in-memory settings while initial malformed loads return defaults.

- [ ] **Step 5: Implement the rolling log**

Before appending, rotate `log.txt` to `log.1.txt` when the next UTF-8 line would exceed 1,048,576 bytes. Format lines as ISO timestamp, area, outcome, optional numeric HTTP status, and optional exception type name. Do not accept request objects, response objects, headers, or bodies in the logging API.

- [ ] **Step 6: Run tests and commit**

Run:

```powershell
dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj --filter "FullyQualifiedName~SettingsStoreTests|FullyQualifiedName~RollingFileLogTests"
dotnet test QuotaGlass.slnx
git add src/QuotaGlass/Core tests/QuotaGlass.Tests/Core
git commit -m "feat(core): add settings and safe logging`n`nCreated with Codex"
```

Expected: all tests pass before the commit.

---

### Task 6: Threshold Watcher

**Files:**
- Create: `src/QuotaGlass/Core/ThresholdWatcher.cs`
- Create: `tests/QuotaGlass.Tests/Core/ThresholdWatcherTests.cs`
- Create: `tests/QuotaGlass.Tests/Support/SnapshotFactory.cs`

**Interfaces:**
- Consumes: `StatusReport`, `StatusAlert`, thresholds from settings.
- Produces: `ThresholdWatcher(double warningPercent, double criticalPercent)` and `ImmutableArray<StatusAlert> Evaluate(StatusReport? previous, StatusReport next)`.

- [ ] **Step 1: Write table-driven crossing tests**

Use `SnapshotFactory.Report(percent, resetsAt, health)` and cover the complete sequence:

```csharp
[Fact]
public void Evaluate_FiresEachThresholdOncePerCycle()
{
    var watcher = new ThresholdWatcher(80, 95);
    DateTimeOffset cycle = DateTimeOffset.Parse("2026-08-29T01:59:59Z");

    Assert.Empty(watcher.Evaluate(Report(79, cycle), Report(79, cycle)));
    Assert.Equal(AlertKind.Warning, Assert.Single(watcher.Evaluate(Report(79, cycle), Report(80, cycle))).Kind);
    Assert.Empty(watcher.Evaluate(Report(80, cycle), Report(94, cycle)));
    Assert.Equal(AlertKind.Critical, Assert.Single(watcher.Evaluate(Report(94, cycle), Report(95, cycle))).Kind);
    Assert.Empty(watcher.Evaluate(Report(95, cycle), Report(96, cycle)));
    Assert.Empty(watcher.Evaluate(Report(96, cycle), Report(81, cycle)));
}

[Fact]
public void Evaluate_NewResetTimestampRearmsThreshold()
{
    var watcher = new ThresholdWatcher(80, 95);
    DateTimeOffset first = DateTimeOffset.Parse("2026-08-29T01:59:59Z");
    DateTimeOffset second = first.AddDays(7);
    watcher.Evaluate(Report(79, first), Report(80, first));

    Assert.Equal(AlertKind.Warning,
        Assert.Single(watcher.Evaluate(Report(10, second), Report(80, second))).Kind);
}
```

Also test first-run 95 percent emits a single critical alert, `Percent >= 100` emits `LimitReached` only once per cycle, transition to `AuthExpired` emits once with the exact re-auth message, an already-expired initial snapshot emits once, and `Unreachable` Ollama never emits an alert.

- [ ] **Step 2: Run watcher tests and verify failure**

Run: `dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj --filter FullyQualifiedName~ThresholdWatcherTests`

Expected: compilation fails because `ThresholdWatcher` is absent.

- [ ] **Step 3: Implement cycle-keyed alert deduplication**

Use a private key with exact fields:

```csharp
private readonly record struct FiredKey(
    string ProviderId,
    string? WindowLabel,
    AlertKind Kind,
    DateTimeOffset? CycleResetsAt);
```

For each next window, locate the previous window by provider ID plus exact label. A threshold crosses when the previous percentage is below the threshold and next is at or above it. On first report, treat the previous value as zero and emit only the highest crossed state, so an initial 95 percent snapshot produces one critical alert rather than warning plus critical. A jump from below warning directly to the critical range follows the same highest-state rule. A newly reached 100 percent emits only `LimitReached` for that evaluation. Keep fired keys for current and future cycle reset timestamps and remove keys whose provider/window no longer exists or whose cycle timestamp is older than the current matching window. Auth expiry keys use null window and null cycle, and are removed when health leaves `AuthExpired` so a later expiry can alert again.

- [ ] **Step 4: Run tests and commit**

Run:

```powershell
dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj --filter FullyQualifiedName~ThresholdWatcherTests
dotnet test QuotaGlass.slnx
git add src/QuotaGlass/Core/ThresholdWatcher.cs tests/QuotaGlass.Tests
git commit -m "feat(core): add threshold crossing alerts`n`nCreated with Codex"
```

Expected: all tests pass.

---

### Task 7: Parallel Status Poller and Backoff

**Files:**
- Create: `src/QuotaGlass/Core/StatusPoller.cs`
- Create: `tests/QuotaGlass.Tests/Core/StatusPollerTests.cs`
- Create: `tests/QuotaGlass.Tests/Support/FakeStatusProvider.cs`

**Interfaces:**
- Consumes: `IStatusProvider`, `ProviderSnapshot`, `StatusReport`, `AppSettings`, `RollingFileLog`, and `TimeProvider`.
- Produces: `StatusPoller`, `StatusPoller.Current`, `StatusPoller.ReportUpdated`, `PollOnceAsync`, `RunAsync`, `RequestRefresh`, and `SetReducedCadence(bool)`.

- [ ] **Step 1: Write failing concurrency and timeout tests**

Use controllable fake providers. Assert:

```csharp
[Fact]
public async Task PollOnceAsync_StartsProvidersBeforeEitherCompletes()
{
    var first = FakeStatusProvider.Blocking("first");
    var second = FakeStatusProvider.Blocking("second");
    StatusPoller poller = CreatePoller(first, second);

    Task<StatusReport> poll = poller.PollOnceAsync(CancellationToken.None);
    await Task.WhenAll(first.Started.Task, second.Started.Task).WaitAsync(TimeSpan.FromSeconds(1));
    first.CompleteOk();
    second.CompleteOk();

    Assert.Equal(2, (await poll).Providers.Length);
}
```

Pass a 20 millisecond timeout through the constructor's test-only timeout parameter and assert a blocking provider times out while production defaults to 10 seconds. Also test that unavailable providers are never fetched and `RequestRefresh` wakes the timer without waiting for the next interval.

- [ ] **Step 2: Write failing retention and cadence tests**

Start from one successful snapshot and throw on subsequent fetches. Assert failures one and two retain the old data with `HealthState.Ok` and failure counts 1 and 2; failure three retains data, sets `Degraded`, and count 3; a later success replaces the snapshot and resets the count to zero. Assert `SetReducedCadence(true)` selects 5 minutes and false restores 60 seconds from current settings.

- [ ] **Step 3: Run poller tests and verify failure**

Run: `dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj --filter FullyQualifiedName~StatusPollerTests`

Expected: compilation fails because `StatusPoller` is absent.

- [ ] **Step 4: Implement poll-once behavior**

Constructor shape:

```csharp
public StatusPoller(
    IReadOnlyList<IStatusProvider> providers,
    Func<AppSettings> settings,
    RollingFileLog log,
    TimeProvider? timeProvider = null,
    TimeSpan? providerTimeout = null)
```

For each available provider, start `FetchAsync` immediately and apply a linked cancellation source plus a timeout source constructed as `new CancellationTokenSource(providerTimeout ?? TimeSpan.FromSeconds(10), timeProvider)`. Use `Task.WhenAll`. Provider-returned health states are valid results. Only thrown exceptions or timeout cancellation enter the consecutive-failure retention path. Compute staleness from the retained snapshot's `FetchedAt`; do not add mutable state to model records.

- [ ] **Step 5: Implement the run loop and immediate refresh**

Use one `PeriodicTimer` at a time plus an async auto-reset signal implemented with a replaceable `TaskCompletionSource`. Wait for either the timer tick or refresh signal. Recreate the timer when cadence or settings change. Raise `ReportUpdated` on the captured synchronization context after atomically assigning `Current`. Dispose timers and cancel outstanding waits during shutdown.

- [ ] **Step 6: Run tests and commit**

Run:

```powershell
dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj --filter FullyQualifiedName~StatusPollerTests
dotnet test QuotaGlass.slnx
git add src/QuotaGlass/Core/StatusPoller.cs tests/QuotaGlass.Tests
git commit -m "feat(core): add parallel status polling`n`nCreated with Codex"
```

Expected: all tests pass.

---

### Task 8: Windows Platform Services

**Files:**
- Create: `src/QuotaGlass/Platform/AutostartService.cs`
- Create: `src/QuotaGlass/Platform/ActivityStateMonitor.cs`
- Create: `src/QuotaGlass/Platform/GlobalHotkey.cs`
- Create: `src/QuotaGlass/Platform/ToastNotifier.cs`
- Create: `src/QuotaGlass/Platform/TrayIconRenderer.cs`
- Create: `src/QuotaGlass/Platform/TrayStatusPolicy.cs`
- Create: `tests/QuotaGlass.Tests/Platform/AutostartServiceTests.cs`
- Create: `tests/QuotaGlass.Tests/Platform/TrayStatusPolicyTests.cs`

**Interfaces:**
- Consumes: settings, status reports, and status alerts.
- Produces: registry-backed autostart, reduced-cadence signal, `GlobalHotkey`, `ToastNotifier.Show(StatusAlert)`, runtime tray icons, and `TrayStatusPolicy.GetState(StatusReport, thresholds)`.

- [ ] **Step 1: Write failing pure tray policy tests**

Define `TrayState { Green, Amber, Red, Grey }`. Test that the worst percentage wins across providers, 80 maps amber, 95 maps red, all unreachable maps grey, and a mix of unreachable plus a healthy provider below 80 maps green. `AuthExpired` and `Degraded` map red even with no windows.

- [ ] **Step 2: Write failing autostart tests against an abstraction**

Introduce internal `IRunKey` with `GetValue`, `SetValue`, and `DeleteValue`. Test `AutostartService.IsEnabled` reads the current value every time, `SetEnabled(true)` writes the quoted executable path under value name `QuotaGlass`, and false deletes only that value. Production `RegistryRunKey` targets `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

- [ ] **Step 3: Run focused tests and verify failure**

Run: `dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj --filter "FullyQualifiedName~TrayStatusPolicyTests|FullyQualifiedName~AutostartServiceTests"`

Expected: compilation fails because platform services are absent.

- [ ] **Step 4: Implement tray state and icon drawing**

`TrayIconRenderer.Create(TrayState state, int size = 32)` creates a transparent `System.Drawing.Bitmap`, draws an anti-aliased filled circle with colors `#35C46A`, `#F0A43A`, `#E24B4B`, or `#8B9098`, obtains an `HICON`, clones it into a managed `Icon`, and destroys the native handle with `DestroyIcon`. Cache one icon per state and dispose all cached icons on application shutdown.

- [ ] **Step 5: Implement activity, hotkey, toast, and autostart**

- `ActivityStateMonitor` subscribes to `SystemEvents.SessionSwitch` and `SystemEvents.PowerModeChanged`. It reports reduced cadence while locked, or while `SystemInformation.PowerStatus.PowerLineStatus == Offline` and `GetLastInputInfo` shows at least 5 minutes idle. It unsubscribes on dispose.
- `GlobalHotkey` parses only `Ctrl+Alt+A` initially, registers with `RegisterHotKey`, listens through `HwndSource.AddHook`, raises `Pressed`, and unregisters on dispose. Registration failure is logged and does not terminate the app.
- `ToastNotifier` uses `ToastContentBuilder` with provider name, alert message, and reset time when present. It is never called for unreachable Ollama because `ThresholdWatcher` produces no such alert.
- `AutostartService` always reads registry state for the context menu check mark.

- [ ] **Step 6: Run tests and commit**

Run:

```powershell
dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj --filter "FullyQualifiedName~TrayStatusPolicyTests|FullyQualifiedName~AutostartServiceTests"
dotnet build QuotaGlass.slnx
git add src/QuotaGlass/Platform tests/QuotaGlass.Tests/Platform
git commit -m "feat(platform): add windows desktop services`n`nCreated with Codex"
```

Expected: tests pass and the WPF project builds with zero warnings.

---

### Task 9: Shared Provider Card, Popup, and Overlay

**Files:**
- Create: `src/QuotaGlass/Ui/Controls/ProviderCard.xaml`
- Create: `src/QuotaGlass/Ui/Controls/ProviderCard.xaml.cs`
- Create: `src/QuotaGlass/Ui/Converters/SeverityToBrushConverter.cs`
- Create: `src/QuotaGlass/Ui/Converters/PercentToGridLengthConverter.cs`
- Create: `src/QuotaGlass/Ui/Converters/ResetTimeConverter.cs`
- Create: `src/QuotaGlass/Ui/PopupWindow.xaml`
- Create: `src/QuotaGlass/Ui/PopupWindow.xaml.cs`
- Create: `src/QuotaGlass/Ui/OverlayWindow.xaml`
- Create: `src/QuotaGlass/Ui/OverlayWindow.xaml.cs`
- Create: `src/QuotaGlass/Ui/WindowPlacementService.cs`
- Create: `tests/QuotaGlass.Tests/Ui/DisplayConverterTests.cs`

**Interfaces:**
- Consumes: provider snapshots, settings store, and monitor working areas.
- Produces: reusable `ProviderCard`, popup and overlay windows, reset text conversion, and persisted overlay position.

- [ ] **Step 1: Write failing converter tests**

Test reset formatting against a fixed `TimeProvider`: 2 hours 47 minutes becomes `in 2h47`, exactly 24 hours becomes the invariant local abbreviated day plus 24-hour time such as `Wed 14:30`, null becomes an empty string, and past time becomes `now`. Test a null percentage produces no filled bar width and percentages clamp visually to 0 through 100 without altering the model value.

- [ ] **Step 2: Run converter tests and verify failure**

Run: `dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj --filter FullyQualifiedName~DisplayConverterTests`

Expected: compilation fails because converters are absent.

- [ ] **Step 3: Implement the shared provider card**

Create one card with provider label, optional plan label, health/error text, zero or more usage rows, and zero or more info rows. Each usage row has label and percentage on one line, a 6 pixel rounded bar beneath it, and reset text below. Use a two-column grid for background and percentage fill so width is data-driven without code-behind. Hide the windows section when empty so Ollama renders only information lines. Derive stale text as `Updated {relative time}` when `ConsecutiveFailures > 0` or age exceeds twice the active poll interval.

Use resource colors matching the runtime tray icons and a neutral dark surface suitable for both popup and overlay. Keep all user-visible strings in XAML resources or one display helper so later localization does not require changing provider code.

- [ ] **Step 4: Implement popup behavior**

`PopupWindow` is borderless, size-to-content, absent from the taskbar, and deactivates by hiding rather than closing. Position it inside the monitor working area nearest the taskbar notification area. Clamp both axes so the entire window remains visible with taskbars on any edge.

- [ ] **Step 5: Implement the no-activate overlay**

Set `Topmost=true`, `AllowsTransparency=true`, `WindowStyle=None`, `ShowInTaskbar=false`, and add `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` in `SourceInitialized` using `GetWindowLongPtr` and `SetWindowLongPtr`. On show, resolve the configured monitor device ID, fall back to the primary working area, and snap to the configured corner with a 12 device-independent-pixel margin. A drag gesture temporarily handles mouse input, persists custom X/Y plus monitor ID through `SettingsStore.SaveAsync`, and never calls `Activate` or focuses a child control.

- [ ] **Step 6: Run tests, build, and commit**

Run:

```powershell
dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj --filter FullyQualifiedName~DisplayConverterTests
dotnet build QuotaGlass.slnx
git add src/QuotaGlass/Ui tests/QuotaGlass.Tests/Ui
git commit -m "feat(ui): add popup and corner overlay`n`nCreated with Codex"
```

Expected: tests pass and XAML compiles.

---

### Task 10: Tray Host, Provider Registry, and Application Composition

**Files:**
- Create: `src/QuotaGlass/Providers/ProviderRegistry.cs`
- Create: `src/QuotaGlass/Ui/TrayIconHost.cs`
- Modify: `src/QuotaGlass/App.xaml`
- Modify: `src/QuotaGlass/App.xaml.cs`
- Create: `tests/QuotaGlass.Tests/Ui/TrayTooltipTests.cs`
- Create: `tests/QuotaGlass.Tests/Providers/ProviderRegistryTests.cs`

**Interfaces:**
- Consumes: all providers, core services, platform services, popup, overlay, and settings.
- Produces: the runnable tray application and its static three-provider registration.

- [ ] **Step 1: Write failing registry and tooltip tests**

Assert the registry always returns exactly `claude`, `codex`, and `ollama` in that order; availability remains each provider's responsibility so settings reloads do not rebuild HTTP handlers. Test tooltip formatting produces one line per provider containing provider label plus worst percentage only, omits plans and reset text, and never exceeds 127 UTF-16 characters including line breaks. Truncation must end with three ASCII dots.

- [ ] **Step 2: Run focused tests and verify failure**

Run: `dotnet test tests/QuotaGlass.Tests/QuotaGlass.Tests.csproj --filter "FullyQualifiedName~ProviderRegistryTests|FullyQualifiedName~TrayTooltipTests"`

Expected: compilation fails because registry and tray host are absent.

- [ ] **Step 3: Implement the static provider registry**

`ProviderRegistry.Create(Func<AppSettings> settings, AppPaths paths)` constructs exactly three reusable `SocketsHttpHandler` instances, one per host, each with automatic decompression and `PooledConnectionLifetime` of 15 minutes. Pass Claude and Codex a severity delegate that reads the current settings and calls `SeverityPolicy.FromPercent`, so threshold reloads take effect without rebuilding providers. Providers add `Cache-Control: no-store` to every request. The registry owns and disposes handlers with the application; there is no reflection or dynamic loading. Provider constructors remain public/internal injection points for tests.

- [ ] **Step 4: Implement tray interactions and menu**

`TrayIconHost` owns one `NotifyIcon`. Left-click toggles `PopupWindow`; right-click shows a context menu containing these exact items in order:

1. `Show corner overlay` - checkable, persists `OverlayVisible`.
2. `Start with Windows` - checkable, reads actual registry state and updates both registry and settings.
3. `Refresh now` - calls `StatusPoller.RequestRefresh()`.
4. `Settings file` - creates defaults if missing and opens the file with `ProcessStartInfo.UseShellExecute=true`.
5. `Exit` - begins orderly application shutdown.

On each report, marshal to the UI dispatcher, update both windows' provider collections, recompute the tray state and icon, update the capped tooltip, evaluate alerts, and send returned alerts to `ToastNotifier`.

- [ ] **Step 5: Compose startup and shutdown**

Remove `StartupUri`. In `App.OnStartup`:

1. Resolve paths and load settings.
2. Create logging, provider registry, watcher, poller, windows, toast, hotkey, activity monitor, and tray host in that order.
3. Apply saved overlay visibility without activating it.
4. Wire settings changes to thresholds, hotkey re-registration, overlay placement, and poll cadence. Recreate stateful threshold watcher only when threshold values change.
5. Wire hotkey to overlay visibility and activity state to `SetReducedCadence`.
6. Start `StatusPoller.RunAsync` and request the first poll immediately.

In shutdown, cancel the application token, await the poll loop, hide and dispose `NotifyIcon`, unregister hotkey, unsubscribe system events, dispose handlers and settings watcher, then close windows. Catch top-level exceptions, log only type/outcome metadata, and keep tokens and response bodies out of error dialogs.

- [ ] **Step 6: Run all automated checks and commit**

Run:

```powershell
dotnet test QuotaGlass.slnx
dotnet build QuotaGlass.slnx -c Release
git add src tests
git commit -m "feat(app): compose tray status application`n`nCreated with Codex"
```

Expected: all tests pass and Release build has zero warnings.

---

### Task 11: Publish Profile, Manual Verification, and Documentation

**Files:**
- Create: `src/QuotaGlass/Properties/PublishProfiles/win-x64.pubxml`
- Create: `docs/manual-test-checklist.md`
- Modify: `.gitignore`

**Interfaces:**
- Consumes: complete application.
- Produces: reproducible single-file framework-dependent Windows publish and a release acceptance checklist.

- [ ] **Step 1: Add the publish profile**

Use:

```xml
<Project>
  <PropertyGroup>
    <Configuration>Release</Configuration>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>false</SelfContained>
    <PublishSingleFile>true</PublishSingleFile>
    <DebugType>none</DebugType>
    <DebugSymbols>false</DebugSymbols>
  </PropertyGroup>
</Project>
```

Ignore `bin/`, `obj/`, `TestResults/`, and local `*.user` files without ignoring fixtures or docs.

- [ ] **Step 2: Write the manual acceptance checklist**

The checklist must record pass/fail and evidence for:

1. Initial live values: Claude plan and windows, Codex plan/windows, and Ollama version/model count.
2. Tray states: green below 80, amber at 80, red at 95, grey when every provider is unreachable.
3. One first-run 95 percent alert and no repeat on refresh; re-arm after a fixture or clock-controlled cycle rollover.
4. Popup placement with Windows taskbar on bottom, top, left, and right.
5. Overlay on two monitors with mixed DPI, each configured corner, custom dragged position, and restart persistence.
6. Overlay focus safety while typing in another application.
7. `Ctrl+Alt+A` toggle and graceful behavior when another process owns the hotkey.
8. Start-with-Windows registry value added, reflected after external registry change, and removed.
9. Refresh-now response, 60 second cadence, lock backoff, unlock recovery, battery-and-idle backoff, and RDP reconnect.
10. Claude/Codex expired-token messages with one toast and no credential-file modification.
11. Ollama stopped state as silent unreachable and recovery after daemon start.
12. Log rotation at 1 MB and absence of headers, bodies, tokens, account data, and emails.

- [ ] **Step 3: Run release verification**

Run:

```powershell
dotnet test QuotaGlass.slnx -c Release
dotnet publish src/QuotaGlass/QuotaGlass.csproj -p:PublishProfile=win-x64
Get-ChildItem src/QuotaGlass/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish
```

Expected: tests pass, publish succeeds, and the publish directory contains `QuotaGlass.exe` plus only required framework-dependent support files.

- [ ] **Step 4: Execute the manual checklist**

Run the published executable, complete every checklist row, and attach screenshot paths or exact observed values. Any failed row becomes a separate failing test or narrowly scoped fix before continuing.

- [ ] **Step 5: Inspect credential and settings files for unintended changes**

Before and after the manual run, compare file hashes:

```powershell
Get-FileHash "$env:USERPROFILE\.claude\.credentials.json"
Get-FileHash "$env:USERPROFILE\.codex\auth.json"
```

Expected: both hashes remain identical. Confirm `%APPDATA%\QuotaGlass\settings.json` contains no access token, refresh token, account ID, email, or response body.

- [ ] **Step 6: Commit release assets**

```powershell
git add .gitignore src/QuotaGlass/Properties docs/manual-test-checklist.md
git commit -m "docs(release): add publish and verification checklist`n`nCreated with Codex"
```

---

## Final Verification

- [ ] Run `dotnet test QuotaGlass.slnx -c Release` and confirm zero failures.
- [ ] Run `dotnet build QuotaGlass.slnx -c Release` and confirm zero warnings.
- [ ] Run the fixture security test separately and confirm every fixture is scanned.
- [ ] Run `git status --short` and confirm only intended plan/checklist evidence remains.
- [ ] Compare the completed manual checklist with every item in sections 2, 6, 7, and 8 of the design spec.
