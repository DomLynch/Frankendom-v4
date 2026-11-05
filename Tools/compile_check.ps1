param(
  [string]$Project = 'C:\Users\dom12\Documents\unity_test_hdrp',
  [string]$Out = (Join-Path $env:TEMP 'unitydev_check'),
  [switch]$Baseline   # compile LIVE only (no lane overlay): the error count to compare against
)
# lanePHYS2 compile check (stack: live <- lanePHYS1 <- lanePHYS2; run apply_phys1.py and apply_phys2.py dry runs first so the patched copies are fresh)
# (derived from lanePHYS1)  (Roslyn of the Unity Editor, no Unity start, writes only $Out). Source set = LIVE Assets\Scripts <- overlay of this lane's Assets\Scripts
# (new Physics\*.cs, RagdollDeath.cs and the patched copies that apply_phys1.py derives from live). Run `python apply_phys1.py` (dry run) first so the patched copies are fresh.
$ErrorActionPreference = 'Stop'
$U = 'C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Data'
$csc = "$U\DotNetSdk\sdk\8.0.318\Roslyn\bincore\csc.dll"
if (-not (Test-Path $csc)) { $csc = (Get-ChildItem "$U\DotNetSdk" -Recurse -Filter csc.dll | Select-Object -First 1).FullName }
New-Item -ItemType Directory -Force $Out | Out-Null

$map = @{}
function Add-Tree($root) { if (Test-Path $root) { foreach ($f in Get-ChildItem $root -Recurse -Filter *.cs) { $map[$f.FullName.Substring($root.Length)] = $f.FullName } } }
Add-Tree "$Project\Assets\Scripts"

$files = @($map.Values) + @(Get-ChildItem "$Project\Assets\MetaHumanHead" -Recurse -Filter *.cs -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
$sa = Get-ChildItem "$Project\Library\ScriptAssemblies" -Filter *.dll | Where-Object { $_.Name -notmatch '^Assembly-CSharp|Tests|Editor\.dll$' }
$engine = Get-ChildItem "$U\Managed\UnityEngine" -Filter *.dll
$defs = 'UNITY_STANDALONE;UNITY_STANDALONE_WIN;UNITY_6000_0_OR_NEWER;UNITY_2022_3_OR_NEWER;ENABLE_INPUT_SYSTEM;ENABLE_LEGACY_INPUT_MANAGER'
$common = @('-nologo', '-target:library', '-langversion:latest', '-nostdlib', '-nowarn:CS0618,CS0612,CS0649,CS0169,CS0414,CS0219,CS0168,CS0108,CS0114,CS8981,CS0659,CS0661,CS1591', "-define:$defs")
$refs = @("-r:$U\NetStandard\ref\2.1.0\netstandard.dll")
foreach ($d in $engine) { $refs += "-r:$($d.FullName)" }
foreach ($d in $sa) { $refs += "-r:$($d.FullName)" }
$name = if ($Baseline) { 'Assembly-CSharp-baseline' } else { 'Assembly-CSharp' }
$rsp = Join-Path $Out "$name.rsp"
$lines = ((@($common) + $refs + @("-out:$Out\$name.dll")) | ForEach-Object { '"' + $_ + '"' }) + ($files | ForEach-Object { '"' + $_ + '"' })
Set-Content -Path $rsp -Value $lines -Encoding UTF8
Write-Host "== $name : $($files.Count) files"
$o = & dotnet $csc "@$rsp" 2>&1
$err = $o | Where-Object { $_ -match 'error CS' }
$warn = $o | Where-Object { $_ -match 'warning CS' }
Set-Content (Join-Path $Out "$name.log") $o
Write-Host ("errors: {0}   warnings: {1}   (log {2}\{3}.log)" -f @($err).Count, @($warn).Count, $Out, $name)
$err | Select-Object -First 60 | ForEach-Object { Write-Host "  $_" }

exit @($err).Count

