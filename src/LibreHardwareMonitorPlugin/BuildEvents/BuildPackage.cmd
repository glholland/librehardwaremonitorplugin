@echo off
rem Legacy script - packaging is now handled inline by the SDK-style .csproj build targets.
rem Use 'logiplugintool pack' and 'logiplugintool install' directly if needed:
rem
rem   logiplugintool pack -input=<build-output-dir> -output=LibreHardwareMonitor.lplug4
rem   logiplugintool install -path=LibreHardwareMonitor.lplug4
rem
rem During development, 'dotnet build' writes a .link file and triggers a hot reload automatically.