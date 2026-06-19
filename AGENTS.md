# AGENTS.md

Guidance for Codex or other AI/code agents working in this project.

## Scope

This repository contains a lightweight native Windows tray app for AllinAI quota status. The implementation intentionally avoids Electron or a package-heavy desktop stack. It is a WinForms app targeting `.NET 8` and `net8.0-windows`.

The source is currently concentrated in one file:

```text
Program.cs
```

Keep changes focused and conservative unless the user asks for a larger refactor.

## Important Paths

- Source: `Program.cs`
- Project/version metadata: `allinai-quota-tray-windows.csproj`
- Manifest: `app.manifest`
- Publish script: `build.ps1`
- Publish output: `publish/win-x64/AllinAIQuotaTray.exe`
- Runtime config/token directory: `%APPDATA%\AllinAIQuotaTray\`
- Credential Manager target prefix: `local.allinai.quota.tray.windows`
- Startup registry value: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\AllinAIQuotaTray`

## Code Map

- `Program`: app entry point.
- `TrayApplication`: `ApplicationContext` owner for the tray icon, menu, timer, refresh flow, tooltip text, and app shutdown.
- `AllinClient`: config files, token cache, auth/login/refresh, subscription API, profile API.
- `LoginForm`: small modal WinForms login dialog.
- `ProgressIcon`: dynamic tray icon renderer. It draws a high-contrast icon that remains visible on both dark and light taskbars.
- `StartupManager`: current-user startup toggle through the Windows `Run` registry key.
- `AppInfo`: reads assembly informational version for the tray menu.
- `CredentialVault`: Windows Credential Manager P/Invoke wrapper.
- `TimeZoneNames`: small Windows-timezone-to-IANA mapping for the API query.
- Model classes: config, tokens, API envelope, login response, user profile, subscription, group, quota metric, fetch state.

## Service/API Rules

Preserve these URLs unless the backend has really changed:

```text
https://allincode.top/api/v1
https://allincode.top/subscriptions
```

Used endpoints:

```text
POST /auth/login
POST /auth/refresh
GET  /subscriptions?timezone=<local-timezone>
GET  /user/profile
```

The app expects API responses wrapped in:

```text
{ code, message, data }
```

## Security And Storage

- Do not store passwords in source, `config.json`, `tokens.json`, logs, README examples, or test fixtures.
- Passwords belong in Windows Credential Manager through `CredentialVault`.
- `tokens.json` may contain access/refresh tokens and must remain local runtime data only.
- `config.json` stores email and refresh interval only.
- Startup is per-user and should not require administrator privileges.

## UI/UX Rules

- Keep the app tray-only. Do not introduce a main window unless explicitly requested.
- Left-click and right-click should both open the tray menu.
- Keep tray tooltip text short. `NotifyIcon.Text` has a small Windows length limit; this project truncates tooltip text to avoid runtime errors.
- The app cannot reliably force Windows to show the tray icon outside the `^` overflow area. Do not promise that behavior.
- The icon should remain visible on dark and light taskbars. Preserve the black/white high-contrast outline unless replacing it with an equally robust design.
- Keep Chinese user-facing menu labels consistent with the current app.

## Versioning

Current version: `0.1.0`

Release version metadata lives in `allinai-quota-tray-windows.csproj`:

```xml
<Version>0.1.0</Version>
<AssemblyVersion>0.1.0.0</AssemblyVersion>
<FileVersion>0.1.0.0</FileVersion>
<InformationalVersion>0.1.0</InformationalVersion>
```

When bumping versions, update all four fields together and keep the tray menu version sourced from assembly metadata.

## Build And Validation

Build:

```powershell
dotnet build -c Release
```

Publish:

```powershell
.\build.ps1
```

Verify published version metadata:

```powershell
(Get-Item .\publish\win-x64\AllinAIQuotaTray.exe).VersionInfo |
  Select-Object ProductName,ProductVersion,FileVersion,CompanyName
```

Smoke test:

```powershell
$p = Start-Process .\publish\win-x64\AllinAIQuotaTray.exe -PassThru
Start-Sleep -Seconds 2
if ($p.HasExited) { throw "App exited early: $($p.ExitCode)" }
```

Before publishing over an existing exe, stop the running process:

```powershell
Get-Process AllinAIQuotaTray -ErrorAction SilentlyContinue | Stop-Process -Force
```

## Common Tasks

### Change Refresh Intervals

Update the interval list in `TrayApplication.RefreshIntervalMenu()`. Keep values reasonable; this app should stay lightweight and avoid aggressive polling.

### Change Tooltip Content

Update `TrayApplication.UpdateTooltip()`, `LoadedTooltip()`, or `BalanceTooltip()`. Preserve `TrimTooltip()` or another guard for Windows tooltip length.

### Change Icon Appearance

Update `ProgressIcon.Make()`. Verify both states:

- `progress == null`: idle/loading/error dot-style state.
- `progress.HasValue`: quota progress ring.

Keep the icon readable at small tray sizes. Windows may downscale the icon.

### Change Startup Behavior

Update `StartupManager`. Keep startup under the current user's `Run` key unless the user explicitly asks for a machine-wide installer/service behavior.

### Add Packaging

Prefer extending `build.ps1` or adding a separate packaging script. Keep publish artifacts under `publish/` or another clearly ignored output directory.

## Git/Generated Files

Ignored generated paths:

```text
bin/
obj/
publish/
```

Do not commit runtime data from `%APPDATA%\AllinAIQuotaTray`.

## Known Constraints

- The app is unsigned; Windows SmartScreen may warn when sharing the executable.
- The app currently publishes framework-dependent by default. Users need the .NET 8 Windows Desktop Runtime unless publishing self-contained.
- Tray icon visibility outside the Windows overflow menu is controlled by Windows/user settings.
- Timezone conversion uses a small hand-maintained Windows-to-IANA map, with fallback to `TimeZoneInfo.Local.Id`.
