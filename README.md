# DSH Launcher

An unofficial community Windows system tray launcher for DeepSeek Harness.

This project is not affiliated with or endorsed by DeepSeek.

It starts DeepSeek Harness silently in the background, opens the local web interface when ready, and keeps a tray icon available for control.

## Features

- No persistent PowerShell window
- System tray controls
- Automatic browser launch
- Restart and stop actions
- Separate Harness and launcher logs
- Single-instance protection
- Custom DeepSeek icon
- No Windows service or startup task

## Requirements

- Windows 10 or Windows 11
- Node.js installed
- `npx` available in `PATH`

DeepSeek Harness is launched with:

```text
npx @deepseek-ai/dsh web
```

## Setup

1. Download or clone this repository.
2. Keep the folder in a permanent location.
3. Double-click `setup.cmd`.
4. A **DeepSeek Harness** shortcut is created on your desktop.

## Usage

Launch **DeepSeek Harness** from the desktop or taskbar.

The launcher will:

1. Start DeepSeek Harness in the background.
2. Wait for `http://127.0.0.1:3080`.
3. Open the interface in your default browser.
4. Keep the tray icon available while Harness is running.

### Tray menu

- **Open DeepSeek Harness**
- **Open Harness Logs**
- **Open Launcher Logs**
- **Restart DeepSeek Harness**
- **Stop DeepSeek Harness**

Double-clicking the tray icon also opens the web interface.

## Logs

Logs are stored in:

```text
%LOCALAPPDATA%\DeepSeekHarness\logs\
```

Files:

```text
harness.log
harness-error.log
launcher.log
```

## Repository structure

```text
.
├── README.md
├── setup.cmd
├── .gitignore
├── scripts/
│   ├── tray-launcher.ps1
│   └── launch-hidden.vbs
└── assets/
    └── DeepSeekHarness.ico
```

- `setup.cmd` creates the desktop shortcut.
- `scripts/` contains the launcher logic.
- `assets/` contains the application icon.
- `.gitignore` keeps local/editor files out of the repository.

## Notes

This project is only a Windows launcher for DeepSeek Harness. It does not bundle or modify DeepSeek Harness itself.

Stopping the launcher from the tray also stops the DeepSeek Harness process tree it started.
