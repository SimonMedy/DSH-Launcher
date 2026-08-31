# DSH Launcher

<p align="left">
  <a href="https://github.com/SimonMedy/DSH-Launcher/releases"><img src="https://img.shields.io/github/v/release/SimonMedy/DSH-Launcher?color=4D6BFE&style=flat-square" alt="GitHub Release" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-6799FE?style=flat-square" alt="License" /></a>
  <img src="https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-1A1D24?style=flat-square" alt="Platform" />
  <img src="https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square" alt=".NET" />
</p>

An unofficial community Windows system tray launcher for DeepSeek Harness. This project is not affiliated with or endorsed by DeepSeek.

DSH Launcher starts DeepSeek Harness in the background, waits for a validated local startup announcement and HTTP readiness, and provides a WPF tray UI for opening, updating, restarting and configuring the service.

<p align="center">
  <img src="assets/dsh-launcher-preview.png" alt="DSH Launcher Demo" width="300" />
</p>

## Features

- DeepSea WPF tray interface
- Local `dsh web` lifecycle management
- Loopback-only DSH Web binding (`127.0.0.1`)
- Browser launch only after validated startup + HTTP readiness
- Safe fallback to an OS-assigned free port when 3080 is occupied
- Trusted-authority configuration for DSH Web
- On-demand DeepSeek Harness update to an exact resolved npm version
- No-op update detection when the installed DSH version is already current
- Automatic first-install when the global DSH package is missing
- Safe process ownership: unrelated processes are never terminated
- Structured process arguments: settings are not interpolated into a shell command
- Bounded, rotated local logs with token-like value redaction
- Single-instance protection

## Requirements

- Windows 10 or Windows 11 x64
- .NET 10 SDK for building from source
- Node.js with `npm` available in `PATH`

The repository pins its .NET 10 SDK policy through `global.json`.

## Setup

1. Download or clone this repository.
2. Keep the folder in a permanent location.
3. Run `setup.cmd`.
4. The launcher builds into `dist/` and creates a **DeepSeek Harness** desktop shortcut.

`setup.cmd` does not request elevation and does not bypass the PowerShell execution policy.

## Usage

Launch **DeepSeek Harness** from the desktop shortcut. The launcher resolves the globally installed `@deepseek-ai/dsh` Node.js entrypoint and starts DSH directly without placing settings in a shell command.

DSH Launcher owns the browser handoff and passes `--no-open` to DSH. It waits for the process-specific `dsh web:` startup announcement, validates that the announced URL is loopback-only, and then verifies HTTP readiness before enabling **Open DeepSeek Harness**.

If TCP port 3080 is already occupied, the existing listener is left untouched. DSH is started with `--port 0`, allowing Windows to allocate a free loopback port. The tray status shows the actual port when it differs from 3080.

## Trusted authorities and remote access

The `--trusted-host` option is a DSH Web browser-trust / authority validation setting. It is **not authentication** and it does **not** by itself expose Harness to another device.

Use trusted authorities only for hostnames or IP authorities that you expect DSH Web to receive, for example:

```text
my-pc.tailnet.ts.net
100.100.20.30
192.168.1.50
host.example:443
[fd00::1234]:443
```

DSH Launcher validates these values before saving and again before launching Harness. Schemes, paths, credentials, malformed ports and shell metacharacters are rejected.

The launcher itself forces the DSH Web bind host to `127.0.0.1`. Free-form additional arguments cannot override `--host` or `--trusted-host`; trusted authorities must be configured through the dedicated Settings field.

For remote access, place the loopback Harness endpoint behind a trusted network boundary such as Tailscale, WireGuard, or an authenticated reverse proxy. **Do not expose an unauthenticated Harness listener directly to the public Internet.**

## Additional CLI arguments

Additional arguments are tokenized and each token is passed directly to the DSH Node.js process through `ProcessStartInfo.ArgumentList`.

Launcher-owned options are handled specially:

- `--host` is rejected and remains fixed to `127.0.0.1`;
- `--trusted-host` is rejected in free-form arguments and must use the trusted-authority UI;
- `--port` / `--port=...` is parsed and validated by the launcher, including `--port 0`;
- `--no-open` is redundant because DSH Launcher always owns browser handoff.

Argument values are intentionally omitted from launcher startup logs. Even so, do not place credentials, API keys or other secrets in command-line arguments because command lines may be observable through the operating system or downstream tooling.

## Authenticated browser handoff

Some newer DeepSeek Harness builds can announce a browser URL containing an authentication/bootstrap token. DSH Launcher treats that URL as sensitive runtime state:

- the full handoff URL is kept in memory only;
- the token-bearing URL is never written to `runtime.json`;
- startup log lines are sanitized before persistence;
- generic token-like query values are redacted from logs;
- readiness probes use the clean loopback origin, so they do not consume or replay the browser token;
- the in-memory authenticated URL is used only when the user asks the running launcher to open DSH.

A second launcher invocation does not persist or reconstruct authenticated handoff tokens. If an authenticated handoff is required, use the already-running tray instance to open DSH safely.

## Harness updates

When **Update DeepSeek Harness** is selected, DSH Launcher first queries npm for `@deepseek-ai/dsh`'s `dist-tags.latest`, validates the returned version, and compares it with the installed package version.

If both versions are identical, the update is skipped and the running Harness process is left untouched. Otherwise the launcher installs the exact resolved version, for example:

```text
npm install -g @deepseek-ai/dsh@0.1.1-rc.2
```

The mutable `@latest` target is never placed directly in the install command. npm remains the upstream package-distribution trust boundary.

## Configuration

Configuration is stored outside the Git repository at:

```text
%LOCALAPPDATA%\DeepSeekHarness\config.json
```

Writes use a temporary file, read-back validation and same-directory replacement. Save failures are surfaced to the user instead of being ignored.

If existing JSON is corrupt, DSH Launcher preserves a timestamped copy, uses safe defaults and displays a recovery warning rather than silently overwriting the original configuration.

## Logs

```text
%LOCALAPPDATA%\DeepSeekHarness\logs\
```

Files:

```text
harness.log
harness-error.log
launcher.log
```

Each active log is capped at approximately 5 MiB and rotates to a single `.1` backup. Token-like values are redacted on write, but logs can still contain ordinary upstream DSH output; review them before sharing publicly.

## Development and CI

Security-sensitive parsing, configuration, update-version handling and runtime-endpoint behavior have automated regression tests under `tests/DSHLauncher.Tests`.

GitHub Actions runs on Windows and performs restore, build, tests, self-contained `win-x64` publish, and CodeQL analysis. Third-party Actions are pinned to immutable commit SHAs, and Dependabot proposes grouped updates for Actions and NuGet test dependencies.

## Security

See [SECURITY.md](SECURITY.md) for the vulnerability-reporting process and security model.

Important invariants:

- user-controlled settings are never interpolated into the Harness shell command;
- DSH Web is bound to loopback by the launcher;
- trusted authorities are validated and cannot be bypassed through free-form `--trusted-host` arguments;
- only launcher-owned process trees are terminated;
- unrelated port owners are left untouched;
- authenticated startup tokens are kept out of persistent launcher state and logs;
- configuration write failures and corruption recovery are visible;
- npm installs target an exact validated version and are skipped when already current;
- CI Actions are pinned to immutable SHAs.

## License

MIT. See [LICENSE](LICENSE).
