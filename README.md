# DSH Launcher

<p align="left">
  <a href="https://github.com/SimonMedy/DSH-Launcher/releases"><img src="https://img.shields.io/github/v/release/SimonMedy/DSH-Launcher?color=4D6BFE&style=flat-square" alt="GitHub Release" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-6799FE?style=flat-square" alt="License" /></a>
  <img src="https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-1A1D24?style=flat-square" alt="Platform" />
  <img src="https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square" alt=".NET" />
</p>

An unofficial community Windows system tray launcher for DeepSeek Harness. This project is not affiliated with or endorsed by DeepSeek.

It starts DeepSeek Harness in the background, opens the local web interface when ready, provides a WPF tray UI, and can maintain the global `@deepseek-ai/dsh` npm installation.

<p align="center">
  <img src="assets/dsh-launcher-preview.png" alt="DSH Launcher Demo" width="300" />
</p>

## Security model

DSH Launcher intentionally starts a developer-grade local process. The launcher therefore treats process execution and remote-access configuration as security boundaries:

- Harness is launched directly through `node.exe` with structured `ProcessStartInfo.ArgumentList` arguments. Settings are never interpolated into a `cmd.exe` command.
- Trusted authorities are validated as hostnames/IP authorities with optional ports.
- `--host`, `--trusted-host`, and `--port` cannot be overridden through Additional CLI Arguments.
- If TCP port `3080` is already occupied, the launcher fails safely. It never terminates a process merely because that process owns the port.
- Only process trees started by DSH Launcher are terminated by the launcher.
- Configuration writes are atomic and failures are visible. Invalid configuration files are preserved with a `.corrupt.<timestamp>.json` suffix instead of silently overwritten.
- Update checks resolve the npm `latest` tag first, validate the returned version, and install that exact version.
- Likely secret-bearing command-line values are redacted from launcher command logs.

See [SECURITY.md](SECURITY.md) for vulnerability reporting.

## Trusted authorities and remote access

`--trusted-host` is **not authentication** and does not, by itself, make Harness remotely reachable. It controls which Host/authority values DSH Web accepts at its browser-trust boundary.

Use remote access only behind a network boundary you trust, such as Tailscale, WireGuard, or an authenticated reverse proxy. Do not expose an unauthenticated Harness listener directly to the public Internet.

Examples of accepted authorities:

```text
my-pc.tailnet.ts.net
100.100.20.30
192.168.1.50
host.example:443
[fd00::1234]
[fd00::1234]:443
```

Schemes, paths, credentials, query strings, fragments, malformed ports, and shell metacharacters are rejected.

## Requirements

- Windows 10 or Windows 11 x64
- .NET 10 SDK when building from source
- Node.js/npm available on `PATH`

The repository pins the .NET 10 SDK feature band through `global.json` with latest-patch roll-forward.

## Install from source

Clone or download the repository and run:

```cmd
setup.cmd
```

The script publishes a self-contained `win-x64` build to `dist/` and creates a desktop shortcut. It does not request elevation and does not bypass PowerShell execution policy.

## Configuration

Per-user configuration is stored at:

```text
%LOCALAPPDATA%\DeepSeekHarness\config.json
```

The current schema is:

```json
{
  "trustedHosts": [],
  "customArgs": ""
}
```

Additional CLI Arguments are tokenized according to Windows command-line rules and passed as structured process arguments. Do not place secrets on command lines; although the launcher redacts common secret-like flags from its own command log, child-process output may still contain sensitive information.

## Logs

Logs are stored under:

```text
%LOCALAPPDATA%\DeepSeekHarness\logs\
```

including `launcher.log`, `harness.log`, and `harness-error.log`.

## Development and CI

GitHub Actions validates changes on Windows with restore, Release build, unit tests, and a self-contained publish smoke test. Test/publish artifacts are retained for five days.

Security-sensitive regression tests cover trusted-authority validation, structured CLI parsing/reserved flags, log redaction, and configuration corruption/recovery.

## License

MIT. See [LICENSE](LICENSE).
