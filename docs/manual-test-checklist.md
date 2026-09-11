# ReservePane Release Acceptance Checklist

Use this checklist for every Windows x64 release candidate. Record `PASS`, `FAIL`, or `NOT EXERCISED` in every Status cell. Evidence must name the exact command, observation, or screenshot path. `NOT EXERCISED` requires a concrete environmental reason and should cite automated coverage when available. Never paste credentials, tokens, headers, response bodies, account identifiers, user identifiers, email addresses, or live usage data into this document or screenshots committed to the repository.

## Execution record

| Field | Value |
|---|---|
| Date |  |
| Tester |  |
| Build |  |
| Windows session |  |
| Display topology |  |

## Acceptance results

| ID | Check | Status | Evidence | Notes |
|---|---|---|---|---|
| LIVE-01 | Initial Claude card shows the live plan label and every returned usage window. |  |  |  |
| LIVE-02 | Initial Codex card shows the live plan label and every returned usage window. |  |  |  |
| LIVE-03 | Ollama Cloud uses the existing CLI sign-in; supported session/weekly quotas appear and ambiguous monthly values remain explicitly unavailable. |  |  |  |
| LIVE-04 | Initial Grok card shows the live plan label and every returned usage window. |  |  |  |
| TRAY-01 | Tray is green when every reachable provider is below the warning threshold. |  |  |  |
| TRAY-02 | Tray is amber at the warning threshold. |  |  |  |
| TRAY-03 | Tray is red at the critical threshold. |  |  |  |
| TRAY-04 | Tray is grey when every visible provider is unreachable. |  |  |  |
| TRAY-05 | Every successful application launch displays one notification stating that ReservePane is running in the system tray. |  |  |  |
| ALERT-01 | A first-run value at 95 percent raises exactly one critical toast. |  |  |  |
| ALERT-02 | Refreshing an unchanged 95 percent cycle does not repeat the toast. |  |  |  |
| ALERT-03 | Moving the reset timestamp to a later fixture or clock-controlled cycle re-arms the alert. |  |  |  |
| POPUP-01 | Popup remains fully visible beside a bottom-edge taskbar. |  |  |  |
| POPUP-02 | Popup remains fully visible beside a top-edge taskbar. |  |  |  |
| POPUP-03 | Popup remains fully visible beside a left-edge taskbar. |  |  |  |
| POPUP-04 | Popup remains fully visible beside a right-edge taskbar. |  |  |  |
| POPUP-05 | Header displays the installed version; Refresh starts a poll and shows `Refreshing...` until it finishes; Close hides the popup and leaves the tray running. |  |  |  |
| OVERLAY-01 | Overlay can be shown on each of two monitors with different DPI scaling. |  |  |  |
| OVERLAY-02 | Top-left corner placement stays inside the selected monitor working area. |  |  |  |
| OVERLAY-03 | Top-right corner placement stays inside the selected monitor working area. |  |  |  |
| OVERLAY-04 | Bottom-left corner placement stays inside the selected monitor working area. |  |  |  |
| OVERLAY-05 | Bottom-right corner placement stays inside the selected monitor working area. |  |  |  |
| OVERLAY-06 | A custom dragged position is saved and restored after restart. |  |  |  |
| OVERLAY-07 | While typing in another application, showing or updating the overlay does not move focus or interrupt input. |  |  |  |
| OVERLAY-08 | Header Refresh polls without moving the overlay; both windows disable Refresh while busy and enable it afterward. |  |  |  |
| OVERLAY-09 | Header Close hides the overlay, clears the tray menu check, and saves `OverlayVisible=false`; the overlay remains hidden after restart. |  |  |  |
| OVERLAY-10 | Clicking either header button, including Refresh while disabled, never begins a drag. |  |  |  |
| HOTKEY-01 | `Ctrl+Alt+A` toggles the overlay. |  |  |  |
| HOTKEY-02 | If another process owns the hotkey, registration failure is graceful and the application remains usable. |  |  |  |
| AUTOSTART-01 | Enabling Start with Windows adds only the `ReservePane` value with the quoted executable path. |  |  |  |
| AUTOSTART-02 | An external change to the `ReservePane` Run value is reflected by the menu state. |  |  |  |
| AUTOSTART-03 | Disabling Start with Windows removes only the `ReservePane` value. |  |  |  |
| INSTALL-01 | Interactive MSI install completes without an elevation prompt and installs under `%LOCALAPPDATA%\Programs\ReservePane`. |  |  |  |
| INSTALL-02 | Silent install with `msiexec /i <msi> /qn /norestart` succeeds without scheduling a reboot. |  |  |  |
| INSTALL-03 | Installation creates one Start Menu shortcut and no desktop shortcut or autostart value. |  |  |  |
| INSTALL-04 | Installing the newer MSI while ReservePane is running upgrades in place without a reboot. |  |  |  |
| INSTALL-05 | Interactive and silent uninstall remove the app, shortcut, Apps & Features entry, and stale `ReservePane` Run value. |  |  |  |
| INSTALL-06 | Uninstall preserves `%APPDATA%\ReservePane`, including existing settings and logs. |  |  |  |
| INSTALL-07 | A successful fresh full-UI install starts ReservePane, while silent or basic-UI installs, upgrades, and repairs do not. |  |  |  |
| POLL-01 | Refresh now starts a new poll without waiting for the normal timer. |  |  |  |
| POLL-02 | Normal polling cadence is 60 seconds. |  |  |  |
| POLL-03 | Session lock changes polling cadence to the five-minute backoff. |  |  |  |
| POLL-04 | Unlock restores the normal 60-second cadence. |  |  |  |
| POLL-05 | Battery plus at least five minutes of idle time changes polling cadence to five minutes. |  |  |  |
| POLL-06 | An RDP reconnect keeps the application responsive and polling. |  |  |  |
| AUTH-01 | An expired Claude token shows `re-auth: run claude login`, raises one toast, then remains silent until state changes. |  |  |  |
| AUTH-02 | An expired Codex token shows `re-auth: run codex login`, raises one toast, then remains silent until state changes. |  |  |  |
| AUTH-03 | Claude, Codex, Grok, and Ollama CLI credential files have identical before-and-after SHA-256 digests. |  |  |  |
| AUTH-04 | An expired Grok token shows `re-auth: run grok login`, raises one toast, then remains silent until state changes. |  |  |  |
| OLLAMA-01 | Ollama Cloud polling works without a local model server and sends only a signed, bodyless cloud usage request. |  |  |  |
| OLLAMA-02 | A rejected Ollama CLI identity asks for `ollama signin`; refreshing after sign-in retries with the current identity. |  |  |  |
| LOG-01 | `log.txt` rotates once at 1,048,576 bytes and both retained files stay within the cap. |  |  |  |
| LOG-02 | Logs contain no request or response headers. |  |  |  |
| LOG-03 | Logs contain no request or response bodies. |  |  |  |
| LOG-04 | Logs contain no access tokens or refresh tokens. |  |  |  |
| LOG-05 | Logs contain no account or user identifiers. |  |  |  |
| LOG-06 | Logs contain no email addresses. |  |  |  |
| SETTINGS-01 | `%APPDATA%\ReservePane\settings.json` contains no access token, refresh token, account identifier, email address, or response body. |  |  |  |

## Release verification

| Check | Status | Evidence | Notes |
|---|---|---|---|
| `dotnet test ReservePane.slnx -c Release` |  |  |  |
| `dotnet publish src/ReservePane/ReservePane.csproj -p:PublishProfile=win-x64` |  |  |  |
| Published artifact inventory |  |  |  |
| Fixture security test |  |  |  |
| `dotnet build ReservePane.slnx -c Release` |  |  |  |
| `git diff --check` |  |  |  |
| `eng/installer/Test-MsiMetadata.ps1` |  |  |  |
| `eng/installer/Test-MsiLifecycle.ps1` |  |  |  |

## Sign-off

- Release decision:
- Residual risks:
