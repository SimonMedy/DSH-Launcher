# Security Policy

## Supported versions

Security fixes are maintained on the latest released version and on the current `main` branch.

## Reporting a vulnerability

Please do not open a public issue for a vulnerability that could enable code execution, unsafe remote exposure, credential disclosure, or destructive process behavior.

Use GitHub's private vulnerability reporting / Security Advisory flow for this repository when available. Include:

- affected version or commit;
- Windows and Node.js/npm versions;
- reproduction steps;
- expected and observed behavior;
- impact assessment;
- logs or proof-of-concept details with secrets removed.

If private reporting is unavailable, contact the repository owner privately through an existing trusted channel before publishing technical exploit details.

## Security invariants

Changes to DSH Launcher should preserve these invariants:

1. User-controlled configuration must never be interpolated into a shell command.
2. The launcher must never terminate an unrelated process based only on TCP-port ownership.
3. Remote trusted-authority configuration must not be presented as authentication.
4. Security-relevant configuration failures must be visible rather than silently ignored.
5. Mutable package tags must be resolved to a validated exact version before installation.
6. Secrets should not be intentionally persisted or logged by the launcher.
7. Public binary releases, if introduced, should add Authenticode signing and published hashes/provenance before being treated as trusted distribution artifacts.

## Scope note

DSH Launcher is an unofficial community launcher for DeepSeek Harness. Vulnerabilities in upstream DeepSeek Harness itself should also be reported to the appropriate upstream maintainers.
