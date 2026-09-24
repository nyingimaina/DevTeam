# Create Installer — Business Requirements Specification

| | |
|---|---|
| **Feature** | `create-installer` |
| **Status** | Draft — complete; ready for signoff |
| **Audience** | Any developer who builds this. You should not need to ask anyone anything. |
| **Scope** | Produce a Windows InnoSetup (`*.iss`) installer that packages the DevTeam broker (back-end) + static web UI (front-end) + a new Avalonia/WebView2 desktop shell, plus the shell project the install delivers. |
| **Pattern reference** | `D:\work\nyingi\code\systems\ShipRight` — Avalonia 12.0.4, WebView2, InnoSetup (`shipright.iss`, verified). |
| **Related** | `HANDOFF.md` (Todo 9 Avalonia shell, Todo 10 InnoSetup packaging — this feature delivers both). |

---

## 0. How to use this document

1. Every requirement has a **REQ-N** id and one Given/When/Then acceptance criterion.
2. Test command: `dotnet test DevTeam.slnx`; the installed artifact is additionally validated by the automated E2E in REQ-9.

---

## 1. Executive summary & scope

### 1.1 Business objective

Today DevTeam ships as raw artifacts: `build.ps1` publishes `back-end/DevTeam.Broker` to `publish/broker` (framework-dependent `win-x64`), with the static Next.js export copied into `wwwroot`. A non-technical user cannot install or run this. This feature produces a single Windows installer (`DevTeam-Setup-<version>-win-x64.exe`) that installs the broker, the web UI, and a desktop shell, then launches the shell — which spawns the broker, waits for `/healthz`, and shows the UI in a WebView2 window.

### 1.2 Quantified KPIs

- Install completes unattended (silent `/VERYSILENT`) in under 5 minutes on a clean Windows 11 x64 machine.
- Installed app reaches a usable UI (broker `/healthz` = ok + WebView2 page load) within 90 seconds of launch.
- One installer artifact per build: `installer/DevTeam-Setup-<version>-win-x64.exe`.
- No administrator rights required at any point.

### 1.3 System context & boundaries

- **In scope:** InnoSetup script + installer build pipeline (`packaging/`); Avalonia/WebView2 desktop shell project (`DevTeam.Desktop` — spawn broker → health check → WebView2); shell tests; the installed-app E2E test.
- **Out of scope (do not build):** changing the broker's HTTP API or the frontend UI; code-signing certificate setup; auto-update / migration of an existing install; the `SemaNami` OS-service slot; non-Windows targets; a tray icon or login auto-start.
- **Existing behaviour preserved:** `RuntimeIdentity` (port 5202, `~/.devteam`, `%LOCALAPPDATA%\DevTeam`, mutex `DevTeam.Desktop`) already models the shell's needs — reuse it, don't reimplement. The broker stays independently startable for developers.

### 1.4 Actor matrix

| Actor | Interaction |
|---|---|
| End user | Runs the installer, launches the shell from the Start Menu, uses the web UI, uninstalls. |
| Developer | Runs `packaging/build-installer.ps1` to produce the artifact; runs `dotnet test DevTeam.slnx`. |
| Installer (InnoSetup) | Detects/installs prerequisites, copies files, creates shortcuts, prompts on uninstall. |
| Shell (`DevTeam.Desktop`) | Spawns/terminates the broker, hosts the WebView2 window. |
| Broker (`DevTeam.Broker`) | Serves the UI on loopback; unchanged by this feature. |

## 2. Agreed decisions (recorded)

| # | Decision | Value |
|---|---|---|
| D1 | App type | Hybrid: .NET broker (`back-end/`) + statically-exported Next.js UI (`front-end/`) + **new** Avalonia/WebView2 desktop shell delivered by this installer. |
| D2 | Feature code hierarchy | `packaging/` (top-level: `.iss` + build script), shell project beside it (`DevTeam.Desktop` + `DevTeam.Desktop.Tests`), broker/frontend untouched under `back-end/`/`front-end/`. |
| D3 | Solution wiring | `DevTeam.slnx` gains entries for `DevTeam.Desktop` and `DevTeam.Desktop.Tests` so `dotnet test DevTeam.slnx` covers them. |
| D4 | Install target & elevation | **Per-user only** — `{localappdata}\DevTeam`, **no admin / no elevation**. |
| D5 | Runtime packaging | **Framework-dependent**: broker and shell publish framework-dependent `win-x64`; the installer **detects .NET 10 and installs it if missing**. |
| D6 | WebView2 handling | **ShipRight pattern**: check the WebView2 registry key; when missing, offer to download and install the Evergreen runtime. |
| D7 | Shell lifecycle | **On-demand only**: launched from the Start Menu; shell spawns the broker as a child and stops it when the shell exits. No auto-start, no tray. |
| D8 | Version source | **`Directory.Build.props` `InformationalVersion`** — one source of truth with the assemblies. |
| D9 | Uninstall & data | **Ask the user** whether to also delete DevTeam user data (`~/.devteam`, `%LOCALAPPDATA%\DevTeam`) after removing program files. |
| D10 | Verification depth | **Full UI automation**: automated test drives the installed app's UI through a real workflow end-to-end. |
| D11 | Shortcuts | **Start Menu only** — no desktop icon, no launch-on-login option. |
| D12 | Shell startup flow | **Straight into the web UI** — shell spawns the broker and loads the UI; workspace selection stays in the web UI (no shell folder picker). |
| D13 | Build invocation | **Standalone `packaging/build-installer.ps1`** — builds the frontend, publishes the broker and shell, then runs `iscc`; separate from the root `build.ps1`. |
| D14 | opencode dependency | **Detect and guide** — the shell checks for `opencode` (via `RuntimeIdentity.ResolveOpenCodePath`) and, if missing, shows a plain message with install instructions; do not bundle it. |

## 3. Requirements list

Every requirement below is a `## REQ-N: <Title>` section with a Given/When/Then example.

| ID | Title | Priority |
|---|---|---|
| REQ-1 | One installer artifact packages broker + web UI + shell | MUST |
| REQ-2 | Per-user install, no elevation | MUST |
| REQ-3 | .NET 10 runtime is present or installed | MUST |
| REQ-4 | WebView2 runtime detected and offered if missing | MUST |
| REQ-5 | The shell launches the broker and shows the UI | MUST |
| REQ-6 | On-demand lifecycle, broker stops with the shell | MUST |
| REQ-7 | Installer version matches the build | MUST |
| REQ-8 | Uninstall removes the app and offers to remove user data | MUST |
| REQ-9 | Automated UI verification of the installed app | MUST |
| REQ-10 | Start Menu entry only | MUST |
| REQ-11 | The shell shows the web UI without its own picker | MUST |
| REQ-12 | Installer build is a standalone script | MUST |
| REQ-13 | Existing developer workflows still work | MUST |
| REQ-14 | Missing opencode is detected and explained | MUST |
| REQ-15 | Loopback only, no new exposure (SYS-NFR-SEC-1) | MUST |
| REQ-16 | No secrets in the artifact (SYS-NFR-SEC-2) | MUST |
| REQ-17 | One version source and reused runtime identity (SYS-NFR-DRY-1) | MUST |
| REQ-18 | Plain-language installer and errors (SYS-NFR-UX-1) | MUST |
| REQ-19 | Bounded install time and size (SYS-NFR-RES-1) | MUST |

## 4. Functional requirements

## REQ-1: One installer artifact packages broker + web UI + shell

The feature SHALL produce a single InnoSetup artifact, `installer/DevTeam-Setup-<version>-win-x64.exe`, that installs the broker, the statically-exported web UI, and the Avalonia/WebView2 desktop shell together.

```gherkin
Given a clean Windows 11 x64 machine
When the produced DevTeam-Setup-<version>-win-x64.exe is run
Then it installs the broker, the web UI, and the desktop shell as one product
And exactly one installer artifact exists under packaging output for that version
```

## REQ-2: Per-user install, no elevation

The installer SHALL install into the current user's profile (`{localappdata}\DevTeam`) and SHALL NOT require administrator rights.

```gherkin
Given a user without administrator rights
When they run the installer with default options
Then the app is installed under {localappdata}\DevTeam
And no UAC elevation prompt is shown
```

## REQ-3: .NET 10 runtime is present or installed

The installer SHALL detect whether the .NET 10 runtime required by the broker and shell is installed and, when absent, SHALL offer to download and install it before the app is launched; the installed app SHALL NOT fail at launch solely because the runtime was missing.

```gherkin
Given a Windows machine without the .NET 10 runtime
When the user runs the installer
Then it detects the missing runtime
And offers to install it (silently if the user accepts)
And after installation the broker and shell launch successfully
```

```gherkin
Given a Windows machine that already has the .NET 10 runtime
When the user runs the installer
Then no runtime download is performed
And installation proceeds directly
```

## REQ-4: WebView2 runtime detected and offered if missing

The installer SHALL detect the WebView2 Evergreen runtime via the registry and, when absent, SHALL offer to download and install it; when the user declines, the shell SHALL fall back to opening the UI in the default browser.

```gherkin
Given a machine without the WebView2 runtime
When the user runs the installer
Then a WebView2 page is shown offering to install it
And if accepted the runtime is installed
And if declined the shell opens the UI in the default browser instead
```

```gherkin
Given a machine with the WebView2 runtime installed
When the user runs the installer
Then no WebView2 page is shown
```

## REQ-5: The shell launches the broker and shows the UI

The desktop shell SHALL, on launch: spawn the broker (using `RuntimeIdentity`'s port/data-dir resolution), poll `http://localhost:<port>/healthz` until ok, then show the DevTeam UI directly in a WebView2 window; if the broker cannot be reached within the timeout, it SHALL show a plain-language error with a Retry action.

```gherkin
Given the app is installed and the shell is launched
When the shell starts
Then it spawns the broker
And it waits until /healthz returns ok
And it displays the DevTeam UI in a WebView2 window
```

```gherkin
Given the broker fails to start
When the health check times out
Then the shell shows a plain-language error
And offers a Retry action
```

## REQ-6: On-demand lifecycle, broker stops with the shell

The shell SHALL run only when launched from the Start Menu; it SHALL NOT register auto-start or a tray icon, and it SHALL terminate the broker it spawned when the shell exits.

```gherkin
Given the app is installed
When the machine starts
Then nothing from DevTeam runs automatically
```

```gherkin
Given the shell is running with its broker child
When the user closes the shell window
Then the spawned broker process is terminated
```

## REQ-7: Installer version matches the build

The installer build SHALL read the version from `Directory.Build.props` `InformationalVersion` and SHALL name the artifact `DevTeam-Setup-<version>-win-x64.exe` and report the same version in Add/Remove Programs.

```gherkin
Given Directory.Build.props sets InformationalVersion to "0.1.0"
When the installer is built
Then the artifact is named DevTeam-Setup-0.1.0-win-x64.exe
And Add/Remove Programs shows version 0.1.0
```

## REQ-8: Uninstall removes the app and offers to remove user data

Uninstalling SHALL remove the installed program files and SHALL ask the user, via a single prompt, whether to also delete DevTeam user data; if declined, the data SHALL be left intact.

```gherkin
Given DevTeam is installed with existing user data
When the user uninstalls it
Then the program files under {localappdata}\DevTeam are removed
And they are asked whether to delete their DevTeam data
And if they decline, ~/.devteam and %LOCALAPPDATA%\DevTeam remain
```

## REQ-9: Automated UI verification of the installed app

An automated test SHALL exercise the installed application end-to-end: install silently, launch, and drive the real UI through a workflow (e.g. create a release and see it appear), then uninstall; the test SHALL fail if any step fails. Browser automation connects to the installed app's UI (WebView2 is Chromium; Playwright/CDP or equivalent).

```gherkin
Given a clean Windows x64 machine
When the installed-app E2E test runs
Then it installs the app silently
And launches it and waits for the UI
And drives a real workflow through the UI successfully
And uninstalls the app
And reports success only if every step passed
```

## REQ-10: Start Menu entry only

The installer SHALL create a Start Menu shortcut that launches the shell, and SHALL NOT create a desktop shortcut or any launch-on-login entry.

```gherkin
Given the app is installed
When the user opens the Start Menu
Then a DevTeam entry is present that launches the shell
And no desktop icon and no login auto-start entry were created
```

## REQ-11: The shell shows the web UI without its own picker

The shell SHALL load the existing web UI as-is and SHALL NOT implement its own workspace/folder picker; workspace selection remains the web UI's responsibility.

```gherkin
Given the shell is launched
When the broker is healthy
Then the web UI is shown directly (the workspace picker is the app's own)
And the shell presents no separate folder-picker step
```

## REQ-12: Installer build is a standalone script

A standalone `packaging/build-installer.ps1` SHALL build the frontend, publish the broker and the shell (framework-dependent `win-x64`), and run `iscc` to produce the installer, without requiring the root `build.ps1`; it SHALL fail loudly (non-zero exit) if any step fails.

```gherkin
Given a clean checkout with the .NET SDK and Node available
When packaging/build-installer.ps1 is run
Then it builds the frontend, publishes the broker and shell, and runs iscc
And it exits non-zero if any step fails
And on success installer/DevTeam-Setup-<version>-win-x64.exe exists
```

## REQ-13: Existing developer workflows still work

After this feature, `build.ps1`, the broker's standalone start, `scripts/smoke-test.ps1`, and the existing backend/frontend test suites SHALL continue to work unchanged.

```gherkin
Given the feature is merged
When a developer runs build.ps1 or starts the broker directly
Then behaviour is unchanged
And the existing backend and frontend test suites pass
```

## REQ-14: Missing opencode is detected and explained

The shell SHALL detect whether the `opencode` executable can be resolved (via `RuntimeIdentity.ResolveOpenCodePath`) and, when it cannot, SHALL show a plain-language message explaining that DevTeam needs opencode and how to install it; it SHALL NOT bundle or silently download opencode.

```gherkin
Given a machine without opencode installed
When the shell launches
Then it shows a plain message that opencode is required
And it explains how to install it
And no opencode binary was bundled or downloaded by DevTeam
```

```gherkin
Given opencode is installed
When the shell launches
Then no opencode warning is shown
```

## 5. Non-functional requirements

## REQ-15: Loopback only, no new exposure

The broker SHALL keep binding to loopback only; the installer and shell SHALL NOT add firewall rules, open inbound ports, or elevate privileges.

```gherkin
Given the app is installed and running
When the network surface is inspected
Then the broker listens on loopback only
And no firewall rule was added by the installer
```

## REQ-16: No secrets in the artifact

The installer, its scripts, and the shell SHALL contain no credentials, tokens, or private keys; the git credential store (`git-credentials.dat`) SHALL NOT be packaged.

```gherkin
Given the built installer and its inputs
When they are scanned for secrets
Then no credential, token, or private key is present
```

## REQ-17: One version source and reused runtime identity

The version SHALL have exactly one source (`Directory.Build.props`), and the shell SHALL reuse `DevTeam.Shared.RuntimeIdentity` for port, data dir, app home, and mutex rather than reimplementing them.

```gherkin
Given the shell and the installer build
When version and runtime paths are resolved
Then both read the single version source and RuntimeIdentity
And no duplicated port/path constants exist in the shell
```

## REQ-18: Plain-language installer and errors

Installer pages, the prerequisite prompts, and the shell's error/Retry messages SHALL use plain language and SHALL NOT show stack traces or internal identifiers.

```gherkin
Given a prerequisite is missing or the broker fails to start
When the installer or shell reports it
Then the message is a plain sentence with a clear next action
And no stack trace or internal id is shown
```

## REQ-19: Bounded install time and size

A silent install SHALL complete in under 5 minutes and the artifact SHALL stay under 150 MB (framework-dependent, so the .NET runtime is not bundled).

```gherkin
Given a clean Windows 11 x64 machine
When the installer runs silently
Then it finishes in under 5 minutes
And the artifact is under 150 MB
```

## 6. Data & integration specifications

| Concern | Value |
|---|---|
| Install directory | `{localappdata}\DevTeam` (per-user, REQ-2) |
| Broker port | `RuntimeIdentity.DefaultPort` = 5202 (loopback) |
| Broker data dir | `~/.devteam` (SQLite `devteam.db`, `logs/`) |
| App home | `%LOCALAPPDATA%\DevTeam` (WebView2 profile, shell logs) |
| Single-instance mutex | `DevTeam.Desktop` (`RuntimeIdentity.MutexName`) |
| Artifact | `installer/DevTeam-Setup-<version>-win-x64.exe` |
| Prerequisites | .NET 10 runtime (REQ-3); WebView2 Evergreen (REQ-4) |
| WebView2 registry key | `HKLM\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}` `pv` (ShipRight) |
| opencode CLI | Detected by the shell (REQ-14); resolved by `RuntimeIdentity.ResolveOpenCodePath`; not bundled |

## 7. Traceability & verification

| ID | Priority | Verification method |
|---|---|---|
| REQ-1 | MUST | Inspect installer output; silent install on clean VM |
| REQ-2 | MUST | Silent install as a non-admin user; assert `{localappdata}\DevTeam` populated, no UAC |
| REQ-3 | MUST | Install on a VM without .NET 10 → runtime installed and app launches; repeat with it → no download |
| REQ-4 | MUST | Install without WebView2 → page shown, runtime installed; decline → browser fallback |
| REQ-5 | MUST | Launch shell on a clean VM; UI loads within 90s; force broker failure → error + Retry |
| REQ-6 | MUST | Reboot after install → no DevTeam process; close shell → broker PID gone |
| REQ-7 | MUST | Change InformationalVersion → artifact name and Add/Remove version follow |
| REQ-8 | MUST | Uninstall → prompt shown; decline → data kept; accept → data removed |
| REQ-9 | MUST | Automated E2E on a clean VM is green; inject a UI failure → run is red |
| REQ-10 | MUST | Inspect installed Start Menu / desktop / Run key |
| REQ-11 | MUST | Launch shell → UI appears with no extra picker step |
| REQ-12 | MUST | Run packaging/build-installer.ps1; force a step to fail → non-zero exit |
| REQ-13 | MUST | Run build.ps1 + existing test suites |
| REQ-14 | MUST | Launch shell without opencode → message shown; with opencode → no warning |
| REQ-15 | MUST | `netstat` on the installed app; inspect installer for firewall changes |
| REQ-16 | MUST | Secret scan of artifact and packaging inputs |
| REQ-17 | MUST | Code review: single version read; shell references `RuntimeIdentity` |
| REQ-18 | MUST | Review installer/shell copy for banned internals |
| REQ-19 | MUST | Time a silent install; check artifact size |

## 8. Open decisions

None — all intake questions are resolved (see §2).
