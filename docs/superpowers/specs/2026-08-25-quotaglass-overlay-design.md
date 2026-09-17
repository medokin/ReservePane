# QuotaGlass Overlay - Design

Date: 2026-08-25
Status: approved, ready for implementation planning

## 1. Purpose

A tray-resident Windows widget that answers one question at a glance: how much of
my AI subscription quota is used, and how long until it resets.

It covers Claude Code, OpenAI Codex CLI, and a local Ollama daemon. It reads the
credentials those CLIs already store on this machine, calls each vendor's usage
endpoint, and renders the result as coloured bars in a tray popup and an optional
always-on-top corner panel.

## 2. Scope

### In scope

- Claude, Codex and Ollama status, polled on a timer.
- A tray icon whose colour tracks the worst window across all providers.
- A detail popup opened by left-clicking the tray icon.
- An optional always-on-top corner overlay, off by default, toggled from the tray
  menu or a global hotkey.
- Windows toast notifications on threshold crossings.
- Start-with-Windows toggle.

### Out of scope

- Any write to vendor credential files, and any use of refresh tokens. See
  section 7.
- Money remaining, budgets and cost forecasting. See section 4.1 and decision D4.
- Historical charting or usage logging over time.
- Cross-platform support. This is Windows-only by construction.
- A plugin discovery mechanism. Providers are compiled in. See section 6.4.

## 3. Decisions

Recorded so implementation does not relitigate them.

| # | Decision | Rationale |
|---|---|---|
| D1 | C# / WPF on .NET 10 | .NET 10.0.302 already installed. Native always-on-top, transparency, per-monitor DPI, tray and autostart are all first-class. Single self-contained publish. |
| D2 | Providers compiled in, no plugin layer | No dynamic discovery or DLL loading. An internal `IStatusProvider` keeps a fourth provider to one class plus one registration line, without plugin/host version compatibility to maintain. |
| D3 | Tray always present, corner overlay optional | Reconciles "little overlay in a corner" with "nothing permanently on screen". Overlay defaults off and shares the popup's rendering. |
| D4 | No money math | Neither API exposes a ceiling, so "remaining euros" cannot be computed. Spend is shown as a running total with no denominator. |
| D5 | Toasts on threshold crossings | Tray colour is silent and always live. Toasts fire at 80%, 95%, limit reached, and auth expired, at most once per threshold per window cycle. |
| D6 | Credentials read-only, never refreshed | Refreshing could rotate a token and break the CLI that owns it. |

## 4. What the vendors actually return

All three endpoints were verified working on 2026-08-25 from this machine.
Payloads below are real responses with tokens, account ids and email addresses
removed.

### 4.1 Claude

Token: `%USERPROFILE%\.claude\.credentials.json`, JSON pointer
`/claudeAiOauth/accessToken`. Expiry at `/claudeAiOauth/expiresAt`.

```
GET https://api.anthropic.com/api/oauth/usage
Authorization: Bearer <token>
anthropic-beta: oauth-2025-04-20
```

```json
{
  "five_hour":  { "utilization": 2.0,  "resets_at": "2026-08-25T19:39:59Z",
                  "limit_dollars": null, "used_dollars": null, "remaining_dollars": null },
  "seven_day":  { "utilization": 95.0, "resets_at": "2026-08-29T01:59:59Z",
                  "limit_dollars": null, "used_dollars": null, "remaining_dollars": null },
  "seven_day_opus": null,
  "extra_usage": { "is_enabled": true, "monthly_limit": null, "used_credits": 32252.0,
                   "utilization": null, "currency": "EUR", "decimal_places": 2,
                   "spend_limit_reached": false },
  "limits": [
    { "kind": "session",    "group": "session", "percent": 2,
      "severity": "normal",   "resets_at": "2026-08-25T19:39:59Z", "is_active": false },
    { "kind": "weekly_all", "group": "weekly",  "percent": 95,
      "severity": "critical", "resets_at": "2026-08-29T01:59:59Z", "is_active": true }
  ],
  "spend": { "used": { "amount_minor": 32252, "currency": "EUR", "exponent": 2 },
             "limit": null, "percent": 0, "severity": "normal", "enabled": true }
}
```

Notes that drive the mapping:

- Most top-level window fields are `null` on this account. The parser must treat
  every one of them as optional.
- `limits[]` is the source of truth for windows, not `five_hour` / `seven_day`.
  It carries `severity` directly and absorbs new window kinds without a code
  change.
- `spend.limit` and `extra_usage.monthly_limit` are both `null`. This is the
  reason for D4.
- Plan label comes from `GET https://api.anthropic.com/api/oauth/profile`,
  field `organization.seat_tier` (here `team_standard`). Cached for one hour
  because it does not change.

### 4.2 Codex

Token: `%USERPROFILE%\.codex\auth.json`, pointer `/tokens/access_token`. Account
id at `/tokens/account_id`.

```
GET https://chatgpt.com/backend-api/wham/usage
Authorization: Bearer <token>
chatgpt-account-id: <account_id>
```

```json
{
  "plan_type": "prolite",
  "rate_limit": {
    "allowed": true, "limit_reached": false,
    "primary_window": { "used_percent": 2, "limit_window_seconds": 604800,
                        "reset_after_seconds": 602566, "reset_at": 1788272097 },
    "secondary_window": null
  },
  "additional_rate_limits": [
    { "limit_name": "GPT-5.3-Codex-Spark", "metered_feature": "codex_bengalfox",
      "rate_limit": { "allowed": true, "limit_reached": false,
        "primary_window":   { "used_percent": 0, "limit_window_seconds": 18000,
                              "reset_at": 1787687531 },
        "secondary_window": { "used_percent": 0, "limit_window_seconds": 604800,
                              "reset_at": 1788274331 } } }
  ],
  "credits": { "has_credits": false, "unlimited": false, "balance": "0" }
}
```

Notes that drive the mapping:

- `reset_at` is a unix timestamp in seconds, not an ISO string as with Claude.
- Window labels derive from `limit_window_seconds`: 18000 renders as `5h`,
  604800 as `7d`.
- Each entry in `additional_rate_limits[]` contributes one or two windows,
  labelled by `limit_name`.
- **Hazard.** This host answers unknown paths with an HTML page under HTTP 200.
  Two wrong paths were probed during design and both returned a styled HTML shell
  with status 200. The provider must therefore require a `content-type` of
  `application/json` and report `Degraded` when it is absent, rather than raising
  a JSON parse error.

### 4.3 Ollama

No authentication, no quota concept.

```
GET http://localhost:11434/api/version   ->  { "version": "0.32.15" }
GET http://localhost:11434/api/ps        ->  { "models": [ ... ] }
```

Contributes zero `UsageWindow` entries and one or two `InfoLine` entries. A
refused connection maps to `Unreachable` and raises no toast, because a local
daemon being stopped is a normal state.

## 5. Data model

```csharp
enum HealthState { Ok, Degraded, AuthExpired, Unreachable }
enum Severity    { Normal, Warning, Critical }

record UsageWindow(
    string Label,                 // "session", "weekly", "GPT-5.3-Codex-Spark 5h"
    double? Percent,              // null when the vendor omits it
    DateTimeOffset? ResetsAt,
    Severity Severity);

record InfoLine(string Label, string Value);

record ProviderSnapshot(
    string Id, string Label,
    HealthState Health,
    string? PlanLabel,
    ImmutableArray<UsageWindow> Windows,
    ImmutableArray<InfoLine> Info,
    string? Error,
    DateTimeOffset FetchedAt,
    int ConsecutiveFailures);

record StatusReport(
    DateTimeOffset FetchedAt,
    ImmutableArray<ProviderSnapshot> Providers);
```

Two invariants the UI must respect:

1. `Windows` may be empty. Ollama always is.
2. Money appears only as an `InfoLine`, never as a `UsageWindow`, because it has
   no denominator. Rendered as `Spend: EUR 322.52` and `Budget: no cap set`.

`Severity` derives from `Percent` unless the vendor supplies its own severity, in
which case the vendor's value wins.

There is exactly **one** configurable threshold pair, `warningPercent` and
`criticalPercent`, defaulting to 80 and 95. It drives severity, bar colour, tray
colour and toast firing alike. The literals 80 and 95 elsewhere in this document
are those defaults, not separate constants.

## 6. Components

### 6.1 `Providers\`

```csharp
interface IStatusProvider
{
    string Id { get; }
    string Label { get; }
    Task<ProviderSnapshot> FetchAsync(CancellationToken ct);
}
```

`ClaudeProvider`, `CodexProvider`, `OllamaProvider`. Each takes its credential
file path and an `HttpMessageHandler` by constructor injection, which is what
makes them testable against fixtures. A provider knows nothing about timers,
settings or UI.

### 6.2 `Core\StatusPoller`

Owns a `PeriodicTimer`. Each tick fans out to every available provider in parallel
with a 10 second per-provider timeout, assembles an immutable `StatusReport`, and
raises an event. It is the single source of truth for current state.

Failure handling: a failed fetch keeps the previous snapshot for that provider,
increments `ConsecutiveFailures`, and marks it stale by age. Health only flips to
`Degraded` at three consecutive failures, so one dropped request does not turn
the tray red.

Cadence: 60 seconds by default. Backs off to 5 minutes on session lock or
battery-and-idle, via `SystemEvents.SessionSwitch` and `PowerModeChanged`.
"Refresh now" in the tray menu forces an immediate tick.

HTTP: one `SocketsHttpHandler` reused per host, `Cache-Control: no-store`.

### 6.3 `Core\ThresholdWatcher`

A pure function of `(previous StatusReport, next StatusReport)` returning the
alerts to raise. Its only state is the set of already-fired keys, each key being
`(providerId, windowLabel, threshold, cycleResetsAt)`. Including the reset
timestamp in the key is what makes an alert re-arm automatically when a window
rolls over, and what stops a window parked at 96 percent from notifying on every
poll.

Alerts fire on: first crossing of 80, first crossing of 95, limit reached, and
transition into `AuthExpired`.

### 6.4 `Providers\ProviderRegistry`

One static list constructing the three providers. Adding a fourth is one new
class plus one line here. There is deliberately no scanning, no config-driven
provider definition and no assembly loading.

### 6.5 `Ui\`

- `TrayIconHost` - icon, tooltip, context menu. The icon is **drawn at runtime**
  as a coloured dot rather than shipped as assets: green below 80, amber 80 to
  94, red 95 and above, grey when every provider is unreachable. Tooltip is one
  line per provider, truncated to fit the 127 character `NOTIFYICONDATA.szTip`
  limit: provider label, worst percentage, and nothing else. Full detail lives in
  the popup, not the tooltip.
- `PopupWindow` - borderless, opened by left-click near the tray, closes on
  deactivate.
- `OverlayWindow` - the same content with `Topmost`, `AllowsTransparency`,
  `ShowInTaskbar=false` and the `WS_EX_NOACTIVATE` extended style so it can never
  steal focus. Snapped to a configured corner of a configured monitor's
  `WorkingArea`, draggable, position persisted.
- `Controls\ProviderCard` - the single rendering of one provider, used by both
  windows. Bars are percentage-width and coloured by severity. Resets render as
  `in 2h47` below 24 hours and `Sat 02:00` beyond.
- Global hotkey `Ctrl+Alt+A` via `RegisterHotKey`, toggling the overlay.

Context menu: Show corner overlay (checkable), Start with Windows (checkable),
Refresh now, Settings file, Exit.

### 6.6 `Core\Settings`

JSON at `%APPDATA%\QuotaGlass\settings.json`, written atomically, reloaded on
change. Fields: poll interval, idle interval, overlay
visible, overlay corner, overlay monitor id, overlay position, hotkey, warning
and critical thresholds, autostart. A missing file yields documented defaults; a
malformed file falls back to defaults without throwing.

### 6.7 `Core\Autostart`

Adds or removes a value under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. Reads the current registry
state so the menu item reflects reality rather than a cached setting.

## 7. Security and credential handling

- The app **reads** `~/.claude/.credentials.json` and `~/.codex/auth.json` and
  never writes them.
- Refresh tokens are never read into memory and never used. Refreshing could
  rotate the access token and break the CLI that owns the file. An expired token
  therefore surfaces as `AuthExpired` with the message
  `re-auth: run claude / codex login`, one toast, then silence until the state
  changes.
- Claude's `expiresAt` is checked before any request is made, so an expired token
  costs no network call.
- Access tokens live only in memory, are never logged, and are never written to
  settings or to disk.
- Logging: a single rolling text log at `%APPDATA%\QuotaGlass\log.txt`, capped at
  1 MB with one rotation, recording poll outcomes, HTTP status codes and
  exceptions. Request and response **bodies are never logged**, because both
  vendors' payloads carry account identifiers and one carries an email address.
  Headers are never logged at all.
- Test fixtures are captured real responses with tokens, account ids, user ids
  and email addresses replaced by placeholders. A test asserts that no fixture
  contains a string matching a bearer-token or email shape, so a future careless
  capture fails the build.

## 8. Testing strategy

Test-driven: tests precede implementation for everything below.

**Unit, provider mapping.** Each provider against recorded fixtures. Cases that
must be covered explicitly:

- Claude's null-heavy payload, including `spend.limit: null` and every optional
  window field absent.
- Claude windows sourced from `limits[]`, including vendor-supplied `severity`
  overriding the derived value.
- Codex unix `reset_at` conversion, and `limit_window_seconds` to label mapping.
- Codex `additional_rate_limits[]` producing correctly labelled windows.
- Codex HTML-under-HTTP-200 producing `Degraded`, not an exception.
- Ollama connection refused producing `Unreachable` with no error text.
- Expired Claude token producing `AuthExpired` with zero HTTP requests issued.

**Unit, threshold logic.** Table-driven over `ThresholdWatcher`:

- fires once on crossing 80, not again at 81 through 94
- fires again on crossing 95
- silent on a fall from 96 to 81
- re-arms after `resets_at` moves forward
- fires on transition into `AuthExpired`, once

**Unit, settings.** Round-trip, missing file defaults, malformed file falls back
to defaults without throwing.

**Unit, poller.** Fake providers and a fake clock: parallel dispatch,
per-provider timeout, the three-consecutive-failures rule, stale marking, idle
back-off.

**Manual checklist.** The WPF surface is not unit tested. A written checklist
covers: tray colour transitions across all four states, popup positioning near
the tray with the taskbar on each edge, overlay across two monitors at mixed DPI,
overlay never taking focus, hotkey toggle, autostart entry added and removed, and
behaviour across a lock and an RDP reconnect.

## 9. Project layout

```
E:\QuotaGlass\
  QuotaGlass.sln
  src\QuotaGlass\                    net10.0-windows, WPF
    App.xaml, App.xaml.cs
    Core\      Settings.cs  StatusPoller.cs  ThresholdWatcher.cs  Autostart.cs
    Model\     StatusReport.cs  ProviderSnapshot.cs  UsageWindow.cs  Enums.cs
    Providers\ IStatusProvider.cs  ProviderRegistry.cs
               ClaudeProvider.cs  CodexProvider.cs  OllamaProvider.cs
    Ui\        TrayIconHost.cs  PopupWindow.xaml  OverlayWindow.xaml
               Controls\ProviderCard.xaml  Converters\
  tests\QuotaGlass.Tests\
    Providers\  ClaudeProviderTests.cs  CodexProviderTests.cs  OllamaProviderTests.cs
    Core\       ThresholdWatcherTests.cs  SettingsTests.cs  StatusPollerTests.cs
    fixtures\   claude-usage.json  claude-profile.json  codex-wham.json
                codex-html-200.html  ollama-version.json  ollama-ps.json
  docs\superpowers\specs\
```

Publish: `dotnet publish -r win-x64 --self-contained false -p:PublishSingleFile=true`.

## 10. Known state at design time

Recorded because it makes the first run verifiable rather than a guess.

- Claude: Team plan, seat `team_standard`, rate limit tier `default_raven`, extra
  usage enabled. Session window 2 percent, weekly window 95 percent and
  `critical`, resetting 2026-08-29 01:59 UTC. Extra usage consumed EUR 322.52.
- Codex: plan `prolite`, primary 7 day window 2 percent, one additional limit
  `GPT-5.3-Codex-Spark` at 0 percent, no credits.
- Ollama: 0.32.15, running, no models loaded.

On first run the tray icon should therefore be **red**, driven by the Claude
weekly window, and a single 95 percent toast should fire.

---

Created with Claude
