# DSH Launcher

<p align="left">
  <a href="https://github.com/SimonMedy/DSH-Launcher/releases"><img src="https://img.shields.io/github/v/release/SimonMedy/DSH-Launcher?color=4D6BFE&style=flat-square" alt="GitHub Release" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-6799FE?style=flat-square" alt="License" /></a>
  <img src="https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-1A1D24?style=flat-square" alt="Platform" />
  <img src="https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square" alt=".NET" />
</p>

An unofficial community Windows system tray launcher for DeepSeek Harness. This project is not affiliated with or endorsed by DeepSeek.

DSH Launcher starts DeepSeek Harness in the background, waits for its local HTTP interface, and provides a WPF tray UI for opening, updating, restarting and configuring the service.

<p align="center">
  <img src="assets/dsh-launcher-preview.png" alt="DSH Launcher Demo" width="300" />
</p>

## Features

- DeepSea WPF tray interface
- Local `dsh web` lifecycle management
- Browser launch only after HTTP readiness is confirmed
- Trusted-authority configuration for DSH Web
- On-demand DeepSeek Harness update to an exact resolved npm version
- Automatic first-install when the global DSH package is missing
- Safe port-collision handling: unrelated processes are never terminated
- Structured process arguments: settings are not interpolated into a shell command
- Local launcher and Harness logs
- Single-instance protection

## Requirements

- Windows 10 or Windows 11 x64
- .NET 10 SDK for building from source
- Node.js with `npm` available in `PATH`

The repository pins the validated .NET SDK policy through `global.json`.

## Setup

1. Download or clone this repository.
2. Keep the folder in a permanent location.
3. Run `setup.cmd`.
4. The launcher builds into `dist/` and creates a **DeepSeek Harness** desktop shortcut.

`setup.cmd` does not request elevation and does not bypass the PowerShell execution policy.

## Usage

Launch **DeepSeek Harness** from the desktop shortcut. The launcher starts the globally installed `@deepseek-ai/dsh` package through its Node.js entrypoint, waits for `http://127.0.0.1:3080`, then keeps the tray controls available.

The tray popup provides:

- **Open DeepSeek Harness**
- **Open Harness Logs**
- **Open Launcher Logs**
- **Settings**
- **Update DeepSeek Harness**
- **Restart DeepSeek Harness**
- **Stop DeepSeek Harness**

If TCP port 3080 is already occupied, startup fails safely. DSH Launcher does **not** kill the process using the port.

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

For remote access, place Harness behind a trusted network boundary such as Tailscale, WireGuard, or an authenticated reverse proxy. **Do not expose an unauthenticated Harness listener directly to the public Internet.**

A typical Tailscale-style flow is:

1. make the Harness service reachable only through your trusted private network or proxy;
2. configure the authority that DSH Web will receive, such as your tailnet hostname;
3. add that authority in DSH Launcher Settings;
4. save and restart Harness.

Adding a trusted authority alone is not an access-control list and does not authenticate the connecting client.

## Additional CLI arguments

The optional additional-arguments field is tokenized and each token is passed directly to the DSH Node.js process through `ProcessStartInfo.ArgumentList`. It is never concatenated into the `cmd.exe` launch command.

Argument values are intentionally omitted from launcher startup logs. Even so, do not place credentials, API keys or other secrets in command-line arguments because command lines may be observable through the operating system or downstream tooling.

## Harness updates

When **Update DeepSeek Harness** is selected, DSH Launcher first queries npm for the current `@deepseek-ai/dsh` version, validates the returned version string, and then installs that exact version.

This avoids executing a mutable `@latest` target directly in the install command. npm remains the upstream package-distribution trust boundary.

## Configuration

Configuration is stored outside the Git repository at:

```text
%LOCALAPPDATA%\DeepSeekHarness\config.json
```

Writes use a temporary file, read-back validation and same-directory replacement. Save failures are surfaced to the user instead of being ignored.

If existing JSON is corrupt, DSH Launcher preserves a timestamped copy and falls back to safe defaults rather than silently overwriting the original configuration.

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

## Development and CI

Security-sensitive parsing and configuration behavior has automated regression tests under `tests/DSHLauncher.Tests`.

GitHub Actions runs on Windows and performs:

```text
dotnet restore
dotnet build
dotnet test
dotnet publish
CodeQL for C#
```

The CI token is read-only for repository contents except for the `security-events: write` permission required by the CodeQL job.

## Security

See [SECURITY.md](SECURITY.md) for the vulnerability-reporting process and security model.

Important invariants:

- user-controlled settings are never interpolated into the Harness shell command;
- only launcher-owned process trees are terminated;
- an unrelated process listening on port 3080 is left untouched;
- trusted authorities are validated as authorities, not arbitrary command text;
- configuration write failures are visible;
- corrupt configuration is preserved;
- update installs target an exact validated npm version.

## License

MIT. See [LICENSE](LICENSE).
