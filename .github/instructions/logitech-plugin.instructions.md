---
description: "Use when writing, editing, or migrating C# code in this Logitech Actions SDK plugin. Covers SDK migration rules (Loupedeck → Logitech Actions SDK), .csproj changes, logiplugintool commands, package metadata, and C# conventions for this codebase."
applyTo: "src/LibreHardwareMonitorPlugin/**/*.cs, src/LibreHardwareMonitorPlugin/**/*.csproj, src/LibreHardwareMonitorPlugin/metadata/**"
---

# Logitech Actions SDK Plugin — Coding Instructions

## SDK Target: Logitech Actions SDK

This repo is migrating from **Loupedeck SDK** (local `PluginApi.dll`, .NET Framework 4.7.2) to the **Logitech Actions SDK** (NuGet, .NET 8). Always use the target SDK patterns:

- Target framework: `net8.0`, not `net472`
- SDK reference: NuGet package (added by `logiplugintool generate`), never reference local `PluginApi.dll`
- Output folder for Windows binaries: `win/`, not the project root (set `pluginFolderWin: win/` in `LoupedeckPackage.yaml`)

## Build & Tooling Commands

```bash
dotnet build                         # build
dotnet watch build                   # hot reload dev loop (run from src\LibreHardwareMonitorPlugin\)
logiplugintool pack -input=<dir> -output=LibreHardwareMonitor.lplug4
logiplugintool install -path=LibreHardwareMonitor.lplug4
logiplugintool generate LibreHardwareMonitor   # scaffold reference project
```

Never suggest `LoupedeckPluginTool.exe` — that is the legacy tool replaced by `logiplugintool`.

## C# Conventions

- **Namespace**: `NotADoctor99.LibreHardwareMonitorPlugin`
- **Private fields**: `_camelCase` with underscore prefix (e.g. `_periodicTimer`, `_sensorsByName`)
- **Event handlers**: `On` prefix (e.g. `OnProcessStarted`, `OnSensorValuesChanged`)
- **Safe lookups**: `TryGet*` pattern returning bool (e.g. `TryGetSensor()`)
- **Action grouping**: `groupName` with `###` separator, max 3 levels

## Key API Methods (SDK, identical across both SDKs)

| Purpose | Call |
|---|---|
| Push button image update | `ActionImageChanged(parameterName)` |
| Refresh parameter list | `ParametersChanged()` |
| Refresh folder contents | `ButtonActionNamesChanged()` |
| Declare widget | `IsWidget = true` |

## Package Metadata (`LoupedeckPackage.yaml`)

- `name` must not end in "Plugin" and cannot be changed after marketplace publish
- `supportedDevices` should include both Loupedeck and Logitech device entries
- `pluginFolderWin: win/` (Logitech Actions SDK layout)
- `type: plugin4`, `pluginFileName: LibreHardwareMonitorPlugin.dll`

## WMI / Hardware Monitor Pitfalls

- Always call `IsRunning()` before any WMI query against `root\LibreHardwareMonitor`
- Sensors with ` #` suffix in their name are duplicates — filter them out
- Only one sensor maps per `LHMGaugeType`; multi-sensor views use separate `MonitorType` lists
- `GetAvailableSensors()` is the only thread-safe method — do not add shared mutable state without locking
- Hard-coded temperature ceilings live in `ShowGaugeCommand.cs`: CPU 85°C, GPU 83°C, Storage 80°C
