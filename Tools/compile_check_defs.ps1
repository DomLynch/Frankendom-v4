# laneFIX2 (FIX2 v1): Roslyn compile check of the runtime scripts with chosen scripting defines. CPU only, no Unity start, writes only $Out.
#   Tools\compile_check_defs.ps1                          live Assets\Scripts, default defines (harness ON: the dev build)
#   Tools\compile_check_defs.ps1 -NoHarness               same plus FRANKEN_NO_HARNESS (the release build)
#   Tools\compile_check_defs.ps1 -Overlay <dir>           <dir> is an Assets\Scripts-shaped folder laid over the live one (kit rehearsal: _agents\laneX\stage\Assets\Scripts)
#   Tools\compile_check_defs.ps1 -Scripts <dir>           use <dir> instead of the live Assets\Scripts (a full scratch copy)
# Exit code = number of compiler errors (0 = clean). Same references / nowarn list as Tools\compile_check.ps1.
param(
  [string]$Project = (Split-Path -Parent $PSScriptRoot),
  [string]$Scripts = '',
  [string]$Overlay = '',
  [string]$Out = (Join-Path $env:TEMP 'compile_check_defs'),
  [switch]$NoHarness,
  [string]$UnityData = 'C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Data',
  [string]$LibraryProject = ''      # where Library\ScriptAssemblies lives (default = $Project); a scratch copy has none
)
$ErrorActionPreference = 'Stop'
if (-not $Scripts) { $Scripts = Join-Path $Project 'Assets\Scripts' }
if (-not $LibraryProject) { $LibraryProject = $Project }
$U = $UnityData
$csc = "$U\DotNetSdk\sdk\8.0.318\Roslyn\bincore\csc.dll"
if (-not (Test-Path $csc)) { $csc = (Get-ChildItem "$U\DotNetSdk" -Recurse -Filter csc.dll | Select-Object -First 1).FullName }
New-Item -ItemType Directory -Force $Out | Out-Null

$map = @{}
function Add-Tree($root) { if ($root -and (Test-Path $root)) { $root = (Resolve-Path $root).Path.TrimEnd('\'); foreach ($f in Get-ChildItem $root -Recurse -Filter *.cs) { $map[$f.FullName.Substring($root.Length)] = $f.FullName } } }
Add-Tree $Scripts
Add-Tree $Overlay     # later tree wins for the same relative path
$files = @($map.Values) + @(Get-ChildItem "$Project\Assets\MetaHumanHead" -Recurse -Filter *.cs -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
$sa = @(Get-ChildItem "$LibraryProject\Library\ScriptAssemblies" -Filter *.dll -ErrorAction SilentlyContinue | Where-Object { $_.Name -notmatch '^Assembly-CSharp|Tests|Editor\.dll$' })
$engine = Get-ChildItem "$U\Managed\UnityEngine" -Filter *.dll
$defs = 'UNITY_STANDALONE;UNITY_STANDALONE_WIN;UNITY_6000_0_OR_NEWER;UNITY_2022_3_OR_NEWER;ENABLE_INPUT_SYSTEM;ENABLE_LEGACY_INPUT_MANAGER'
if ($NoHarness) { $defs += ';FRANKEN_NO_HARNESS' }
$common = @('-nologo', '-target:library', '-langversion:latest', '-nostdlib', '-nowarn:CS0618,CS0612,CS0649,CS0169,CS0414,CS0219,CS0168,CS0108,CS0114,CS8981,CS0659,CS0661,CS1591', "-define:$defs")
$refs = @("-r:$U\NetStandard\ref\2.1.0\netstandard.dll")
foreach ($d in $engine) { $refs += "-r:$($d.FullName)" }
foreach ($d in $sa) { $refs += "-r:$($d.FullName)" }
$name = if ($NoHarness) { 'Assembly-CSharp-noharness' } else { 'Assembly-CSharp-harness' }
$rsp = Join-Path $Out "$name.rsp"
$lines = ((@($common) + $refs + @("-out:$Out\$name.dll")) | ForEach-Object { '"' + $_ + '"' }) + ($files | ForEach-Object { '"' + $_ + '"' })
Set-Content -Path $rsp -Value $lines -Encoding UTF8
Write-Host "== $name : $($files.Count) files, $($sa.Count) package dlls, defines: $defs"
$o = & dotnet $csc "@$rsp" 2>&1
$err = @($o | Where-Object { $_ -match 'error CS' })
$warn = @($o | Where-Object { $_ -match 'warning CS' })
Set-Content (Join-Path $Out "$name.log") $o
Write-Host ("errors: {0}   warnings: {1}   (log {2}\{3}.log)" -f $err.Count, $warn.Count, $Out, $name)
$err | Select-Object -First 60 | ForEach-Object { Write-Host "  $_" }
exit $err.Count
