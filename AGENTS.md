# AGENTS.md

Guidance for Codex or other AI/code agents working in this project.

<!-- agent-docs-sync:start -->
## Scope

A lightweight native Windows tray app using C# / WinForms, .NET 8, and `net8.0-windows`. Keep the native direction and small dependency footprint. The macOS app remains in its own repository.

Before changing quota rules, authentication, refresh scheduling, reminders or update checks, read `docs/NATIVE_PARITY.md` for the corresponding macOS behavior and intentional platform differences. Change Windows code directly; do not introduce a cross-platform runtime to synchronize the repositories.

## Code Map

- `Program.cs`: tray/menu owner, lifecycle, power/network events, notification queue.
- `Views.cs`: read-only summary, remaining-quota bar, asynchronous login form.
- `WindowsIntegration.cs`: dynamic icons, Credential Manager, per-user startup, assembly version.
- `Core/Models.cs`: models, error classification, quota policy, presentation projection.
- `Core/Client.cs`: config/token/preferences storage, account session, HTTP, IANA timezone conversion.
- `Core/QuotaStore.cs`: refresh state, cancellation, cooldown/backoff, reminder rules and deduplication.
- `Core/Updates.cs`: stable-release version comparison and independent GitHub checks.
- `Tests/CoreTests.cs`: behavioral regressions without network or real credentials.
- `Tests/NativeSmoke.cs`: production WinForms rendering and temporary test-credential round trip.

Keep disk and credential access outside menu rendering. Platform-independent logic belongs in `Core/` so its actual implementation can run in tests on macOS as well as Windows.

## Behavior

- Stay tray-only; both mouse buttons open the menu. Login and long-description windows are temporary.
- Use Chinese menu labels, `开机自启动`, and `版本：v<version>` near the bottom.
- Ring and progress bar mean remaining quota. Select the most constrained known cycle; missing usage is not zero or unlimited.
- Preserve independent subscription/profile snapshots and last successful timestamps on failure.
- Only authentication failures trigger session recovery. Share in-flight token renewal; late work cannot restore a logged-out session.
- All refresh entry points honor Retry-After. Pause while sleeping and merge wake/network recovery.
- Reminders and automatic updates default off. Seed existing reminder conditions quietly and deduplicate notices.
- Preserve the black/white icon outline for both taskbar appearances. Keep tooltip truncation (63 UTF-16 units without splitting surrogate pairs).
- Windows controls tray overflow visibility; the app cannot force visibility or add a macOS-style text title beside the icon.

## Service and Storage

Keep the real addresses `https://allincode.top/api/v1` and `https://allincode.top/subscriptions`. Endpoints are POST `/auth/login`, POST `/auth/refresh`, GET `/subscriptions?timezone=<IANA-zone>`, GET `/user/profile`, wrapped in `{ code, message, data }`.

- Runtime directory: `%APPDATA%\AllinAIQuotaTray\`.
- `config.json`: email and refresh interval only; separate preferences go in `preferences.json`.
- Passwords: Windows Credential Manager, target `local.allinai.quota.tray.windows:<email>`, UTF-16 blob. Preserve compatibility.
- `tokens.json`: local sensitive runtime data; never commit, log or put tokens/passwords in examples or fixtures.
- Test credentials must be generated at runtime and isolated from real accounts.
- Keep atomic file writes and legacy data compatibility. Preserve explicit sign-out across restarts.
- Startup stays under the current user's `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\AllinAIQuotaTray`; no administrator privileges.
- Update checks use this repository's GitHub Release independently of AllinAI cookies, password and tokens. Stable tags are `v<major>.<minor>.<patch>`; preserve daily opt-in scheduling and persisted GitHub cooldown.

## Validation

After changing business logic:

```sh
dotnet run --project Tests/CoreTests.csproj -c Release
```

On Windows, also run:

```powershell
dotnet build allinai-quota-tray-windows.csproj -c Release --warnaserror
dotnet run --project Tests/NativeSmoke.csproj -c Release -- publish/previews
.\build.ps1
```

On macOS/Linux, cross-compilation is possible with `-p:EnableWindowsTargeting=true`; Windows UI, Credential Manager, notifications, startup and publish scripts still require Windows validation. The GitHub workflow covers native compilation/rendering, temporary credentials, published metadata, process startup and single-instance behavior. A green workflow is not a manual taskbar, high-DPI, notification or sleep/wake acceptance test.

The native smoke test deletes its random test credential; it never touches a real account's credential or startup setting. Keep that property when extending it.

## Version and Publishing

Current version: 0.2.0. Update `Version`, `AssemblyVersion`, `FileVersion`, and `InformationalVersion` in the project together. The menu reads assembly metadata, strips build metadata, and must not carry a separate hardcoded version.

`build.ps1` publishes to a temporary directory and validates metadata before replacing `publish/win-x64/`. Keep failure-safe replacement. Default output is framework-dependent; `-SelfContained` is supported. Do not stop the user's running process to overwrite its exe; ask them to exit via the app when it blocks publishing.

The current app is unsigned. Publish stable Windows artifacts to this repository's Release only when release publication is requested; source push alone is not a release. Ignore `bin/`, `obj/`, `publish/` and runtime data.
<!-- agent-docs-sync:end -->
