# DSH Launcher

An unofficial community Windows system tray launcher for DeepSeek Harness.

This project is not affiliated with or endorsed by DeepSeek.

It starts DeepSeek Harness silently in the background, opens the local web interface when ready, and provides a modern DeepSea WPF popup interface from the system tray.

## Features

- DeepSea dark ocean tray popup interface
- Silent background execution (no persistent terminal window)
- Automatic browser launch when web interface is ready
- System tray controls (open interface, view logs, restart, stop)
- Automatic dependency update support (`npx --yes`)
- Reliable process lifecycle management & port cleanup
- Single-instance protection

## Requirements

- Windows 10 or Windows 11 (x64)
- .NET 10 SDK (required for building via `setup.cmd`)
- Node.js installed (`npx` available in `PATH`)

DeepSeek Harness is launched with:

```text
npx --yes @deepseek-ai/dsh web
```

## Setup

1. Download or clone this repository.
2. Keep the folder in a permanent location.
3. Double-click `setup.cmd`.
4. The launcher builds automatically and creates a **DeepSeek Harness** shortcut on your desktop.

## Usage

Launch **DeepSeek Harness** from the desktop shortcut.

The launcher will:

1. Start DeepSeek Harness in the background.
2. Wait for `http://127.0.0.1:3080`.
3. Open the interface in your default browser.
4. Keep the tray icon available while Harness is running.

### Tray Menu

Click the tray icon to open the DeepSea popup:

- **Open DeepSeek Harness** (opens `http://127.0.0.1:3080`)
- **Open Harness Logs**
- **Open Launcher Logs**
- **Restart DeepSeek Harness**
- **Stop DeepSeek Harness**

Double-clicking the tray icon also opens the web interface directly.

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

## Repository Structure

```text
.
├── README.md
├── setup.cmd
├── .gitignore
├── src/
│   └── DSHLauncher/
│       ├── App.xaml / App.xaml.cs
│       ├── MainWindow.xaml / MainWindow.xaml.cs
│       ├── DSHLauncher.csproj
│       └── Services/
│           └── HarnessService.cs
└── assets/
    └── DeepSeekHarness.ico
```

- `setup.cmd` builds the WPF app to `dist/` and creates the desktop shortcut.
- `src/DSHLauncher` contains the modern WPF tray application code.
- `assets/` contains the high-resolution application icon.

## Notes

This project is only a Windows launcher for DeepSeek Harness. It does not bundle or modify DeepSeek Harness itself.

Stopping the launcher from the tray also terminates the DeepSeek Harness process tree it started.
