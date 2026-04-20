# LibreHardwareMonitorPlugin — Agent Instructions

## Project Overview

A **Logitech Actions SDK plugin** (migrating from Loupedeck SDK) that surfaces live PC hardware metrics (CPU/GPU temperature, load, power, memory, storage, battery) from [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) onto Logitech devices (MX Creative Console, Loupedeck CT/Live, Actions Ring).

Data is sourced via **WMI** from `root\LibreHardwareMonitor` — LibreHardwareMonitor must be running with its WMI provider enabled.

## SDK Migration Context: Loupedeck → Logitech Actions SDK

The codebase currently targets the **Loupedeck SDK** (`PluginApi.dll` v2.0, .NET Framework 4.7.2). The target is the **Logitech Actions SDK** (same API surface, NuGet-based, .NET 8). Key differences:

| Aspect | Current (Loupedeck) | Target (Logitech Actions SDK) |
|---|---|---|
| SDK reference | `PluginApi.dll` (local) | NuGet package via `LogiPluginTool` scaffold |
| Target framework | .NET Framework 4.7.2 | .NET 8 |
| Tooling | `LoupedeckPluginTool.exe pack` | `logiplugintool pack` (global .NET tool) |
| Install | `LogiPluginTool.exe install` | `logiplugintool install` |
| Dev loop | Build → manual copy | `dotnet watch build` (hot reload) |
| Plugin link file | N/A | `%LOCALAPPDATA%\Logi\LogiPluginService\Plugins\<Name>.link` |
| Metadata format | `LoupedeckPackage.yaml` | Same — `LoupedeckPackage.yaml` |
| Package format | `.lplug4` | Same — `.lplug4` |
| Base classes | Same: `Plugin`, `ClientApplication`, `PluginDynamicCommand`, etc. | Same |
| Bin folder | Root | `win/` (Windows), `mac/` (macOS) |

The **base class API is identical** — `Plugin`, `ClientApplication`, `PluginDynamicCommand`, `PluginDynamicAdjustment`, `PluginDynamicFolderNavigation`, `BitmapBuilder`, `BitmapImage`, `BitmapColor` all carry over.

## Build & Tooling

### Prerequisites
- .NET 8 SDK — https://dotnet.microsoft.com/download/dotnet/8.0
- Logi Options+ or Loupedeck software (installs Logi Plugin Service)
- LogiPluginTool: `dotnet tool install --global LogiPluginTool`

### Build
```bash
dotnet build
```

### Hot Reload (dev loop)
```bash
cd src\LibreHardwareMonitorPlugin
dotnet watch build
```

### Package & Install
```bash
logiplugintool pack -input=<build-output-dir> -output=LibreHardwareMonitor.lplug4
logiplugintool install -path=LibreHardwareMonitor.lplug4
```

### Scaffold a fresh plugin for reference
```bash
logiplugintool generate LibreHardwareMonitor
```

### Legacy build script
[src/LibreHardwareMonitorPlugin/BuildEvents/BuildPackage.cmd](src/LibreHardwareMonitorPlugin/BuildEvents/BuildPackage.cmd) — post-build event using old `LoupedeckPluginTool.exe`; update to use `logiplugintool`.

## Architecture

```
LibreHardwareMonitorPlugin/
├── LibreHardwareMonitorPlugin.cs       # Entry point (inherits Plugin)
├── LibreHardwareMonitorApplication.cs  # App stub (inherits ClientApplication)
├── Actions/
│   ├── ShowSensorCommand.cs            # Dynamic command: per-sensor live value button
│   ├── ShowGaugeCommand.cs             # Dynamic widget: rendered bar-gauge (IsWidget=true)
│   ├── HardwareMonitorControlCenter.cs # Dynamic folder nav: sensor picker menu
│   ├── OpenApplicationCommand.cs       # Static command: start/activate LHM
│   └── PluginHelpers.cs                # UI helpers: "press to start" image/text fallbacks
├── LibreHardwareMonitor/
│   ├── LibreHardwareMonitor.cs         # Core: WMI queries, 500ms poll timer, event dispatch
│   ├── LibreHardwareMonitorSensor.cs   # Model: single sensor (value, min, max, gauge type)
│   ├── LHMGaugeType.cs                 # Enum: 35 sensor categories + Count=36
│   ├── LibreHardwareMonitorExtensions.cs # WMI ManagementObject cast helpers
│   └── NativeMethods.cs                # P/Invoke: SetForegroundWindow
└── Helpers/
    ├── PluginLog.cs                    # Static logging facade (Verbose/Info/Warning/Error)
    └── PluginResources.cs              # Embedded resource accessor (images, files)
```

### Data Flow
1. `LibreHardwareMonitor.cs` polls WMI every 500ms, fires `SensorValuesChanged` / `GaugeValuesChanged` / `MonitorValuesChanged`
2. `ShowSensorCommand` / `ShowGaugeCommand` subscribe to those events and call `ActionImageChanged(parameterName)` to push updates to the device
3. `HardwareMonitorControlCenter` listens to `SensorListChanged` and calls `ButtonActionNamesChanged()` to refresh the folder

### Static singleton
`LibreHardwareMonitorPlugin.HardwareMonitor` is the single `LibreHardwareMonitor` instance shared across all action classes.

## Key Conventions

- **Namespace**: `NotADoctor99.LibreHardwareMonitorPlugin`
- **Private fields**: `_camelCase` with underscore prefix
- **Event handler methods**: `On` prefix (e.g., `OnProcessStarted`)
- **Safe lookup**: `TryGet*` pattern (e.g., `TryGetSensor()`)
- **Gauge drawing**: `DrawGuage()` in `ShowGaugeCommand` — uses `BitmapBuilder` with embedded `g0.png`–`g15.png` and `BitmapColor` gradient (green→blue→red based on load %)
- **Action grouping**: pass `groupName` (with `###` separator for sub-groups, max 3 levels) in base constructor

## Package Metadata

[src/LibreHardwareMonitorPlugin/metadata/LoupedeckPackage.yaml](src/LibreHardwareMonitorPlugin/metadata/LoupedeckPackage.yaml) — update `supportedDevices` to include Logitech devices alongside Loupedeck:
```yaml
supportedDevices:
  - LoupedeckCt         # Loupedeck CT family
  - LoupedeckLive       # Loupedeck Live
  # Add Logitech MX Creative Console entries per SDK docs
```

For the Logitech Actions SDK, binaries go in a `win/` subfolder; update `pluginFolderWin: win/`.

## Important Pitfalls

1. **WMI dependency**: Plugin cannot function unless LibreHardwareMonitor is running with WMI provider enabled. Check `IsRunning()` before any WMI query.
2. **Sensor name deduplication**: Sensors with ` #` suffix are filtered out (LHM WMI implementation detail).
3. **Single sensor per `LHMGaugeType`**: Only one sensor maps to each gauge type; multi-sensor views use separate `MonitorType` lists.
4. **Thread safety**: Only `GetAvailableSensors()` is locked; 500ms timer and WMI watcher callbacks run concurrently with UI events — be careful adding shared state.
5. **Hard-coded thresholds**: Max temperatures (CPU 85°C, GPU 83°C, Storage 80°C) are baked into gauge color logic in `ShowGaugeCommand.cs`.
6. **`name` in YAML**: Must not end with "Plugin" and cannot be changed after marketplace publish.

## References

- [Logi Actions SDK — Getting Started](https://logitech.github.io/actions-sdk-docs/getting-started/)
- [C# SDK Introduction](https://logitech.github.io/actions-sdk-docs/csharp/plugin-development/introduction/)
- [Plugin Structure & LoupedeckPackage.yaml](https://logitech.github.io/actions-sdk-docs/csharp/tutorial/plugin-structure/)
- [Add a Simple Command](https://logitech.github.io/actions-sdk-docs/csharp/tutorial/add-a-simple-command/)
- [Marketplace Approval Guidelines](https://logitech.github.io/actions-sdk-docs/marketplace-approval-guidelines/)
- [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)
