# Windows installer

DSH Launcher uses Inno Setup to build a conventional Windows installer from the existing self-contained `win-x64` publish output.

## Security model

The installer intentionally stays narrow:

- per-user installation under `%LOCALAPPDATA%\Programs\DSH Launcher`;
- `PrivilegesRequired=lowest`, so Setup does not request UAC elevation;
- no service, scheduled task, PATH modification, shell extension, file association, firewall rule, or machine-wide registry configuration;
- no `taskkill` or process-name based termination;
- `AppMutex=Local\DSHLauncher` prevents install/update/uninstall while the launcher is running;
- `CloseApplications=no` prevents Setup from trying to close the application itself;
- configuration and logs under `%LOCALAPPDATA%\DeepSeekHarness` are not part of the installer payload and are not deleted by uninstall;
- the application remains self-contained, so the installer does not bootstrap or download a .NET runtime on the user's machine.

The installer itself does not download application payloads. The existing launcher remains responsible for installing/updating the upstream `@deepseek-ai/dsh` npm package according to its documented security model.

## CI toolchain

CI uses an exact Inno Setup release (`7.1.0`), downloaded from the official `jrsoftware/issrc` GitHub release and verified using GitHub release attestations before execution. The compiler is used only on the GitHub-hosted Windows runner and is not committed to or redistributed with this repository.

The installer CI job:

1. restores and publishes the launcher self-contained for `win-x64`;
2. downloads and verifies the pinned Inno Setup compiler installer;
3. compiles `DSHLauncher.iss`;
4. performs a silent install/uninstall smoke test in the runner's per-user profile;
5. emits a SHA-256 checksum next to the setup executable;
6. uploads the setup executable and checksum as a short-lived CI artifact.

The CI artifact is validation output, not a signed production release. Before distributing installer binaries broadly, use Authenticode signing and publish release checksums/provenance from a dedicated release workflow.
