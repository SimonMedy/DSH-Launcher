# Security Policy

## Supported versions

Security fixes are developed against the latest source on the default branch. Users should update to the newest published release when a security fix is announced.

## Reporting a vulnerability

Please do not disclose exploitable security issues in a public GitHub issue before a fix is available. Use GitHub's private vulnerability reporting feature for this repository when available, or contact the repository owner privately through the contact information on their GitHub profile.

Include the affected version or commit, reproduction steps, expected impact, and any relevant logs with secrets removed.

## Security model

DSH Launcher runs DeepSeek Harness with the permissions of the current Windows user. It does not request elevation.

Trusted authorities are browser-trust / authority validation inputs for DSH Web. They are not client authentication and do not make a Harness listener safe to expose directly to the public Internet. Use a trusted network boundary such as Tailscale, WireGuard, or an authenticated reverse proxy for remote access.

Additional CLI arguments are passed directly to the DSH Node.js process as individual arguments and are never interpolated into a shell command. Do not place secrets in command-line arguments.

The launcher never terminates a process merely because it owns TCP port 3080. If the port is occupied, startup fails safely and the conflicting process is left untouched.
