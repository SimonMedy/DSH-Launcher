# Security Policy

## Supported versions

Security fixes are developed against the latest source on the default branch. Users should update to the newest published release when a security fix is announced.

## Reporting a vulnerability

Please do not disclose exploitable security issues in a public GitHub issue before a fix is available. Use GitHub's private vulnerability reporting feature for this repository when available, or contact the repository owner privately through the contact information on their GitHub profile.

Include the affected version or commit, reproduction steps, expected impact, and any relevant logs with secrets removed.

## Security model

DSH Launcher runs DeepSeek Harness with the permissions of the current Windows user. It does not request elevation.

The launcher owns the DSH Web process boundary. It starts DSH directly through `node.exe` with structured `ProcessStartInfo.ArgumentList` arguments, forces the Web listener to `127.0.0.1`, and does not allow free-form arguments to override `--host` or `--trusted-host`. Port selection remains configurable; when the preferred port is occupied, DSH is asked to let Windows assign a free loopback port.

Trusted authorities are browser-trust / authority validation inputs for DSH Web. They are not client authentication and do not make a Harness listener safe to expose directly to the public Internet. Use a trusted network boundary such as Tailscale, WireGuard, or an authenticated reverse proxy for remote access.

Additional CLI arguments are tokenized and passed directly to DSH. They are never interpolated into the Harness shell command. `--host` and `--trusted-host` are reserved launcher-owned options; `--port` is parsed and validated by the launcher. Do not place secrets in command-line arguments.

Newer DSH versions may print an authenticated browser-handoff URL containing a short-lived token. DSH Launcher keeps that URL in memory only, never persists it to `runtime.json`, and redacts token-like values before writing child-process output to logs. A second launcher instance will not attempt to reconstruct or persist an authenticated handoff URL.

The launcher never terminates a process merely because it owns a TCP port. It stops only process trees that it launched and tracks.

Configuration writes use a temporary file and read-back validation. Corrupt configuration is preserved and safe defaults are used; the launcher surfaces a recovery warning to the user.

Logs are bounded and rotated to prevent unbounded disk growth. Logging failures do not terminate the supervised DSH process.

The update flow resolves npm's `dist-tags.latest` to a strictly validated exact version before installation. If the installed package already matches that exact version, the install is skipped. npm and the published `@deepseek-ai/dsh` package remain upstream supply-chain trust boundaries.

GitHub Actions used by this repository are pinned to immutable commit SHAs. Dependabot is configured to propose grouped updates for Actions and test dependencies.
