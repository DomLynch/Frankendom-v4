# Dot-source before using any script in tools_release (or setx FK_ROOT once). FK_ROOT = the Unity project root (the repo clone).
#   . .\tools_release\env.ps1
# Scripts read $env:FK_ROOT and $env:USERPROFILE (see SANITIZE_REPORT.md); nothing else is machine specific except the Unity/Blender paths in Tools\build_config.json and Tools\paths.json.
$env:FK_ROOT = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Write-Host "FK_ROOT = $env:FK_ROOT"
