# Roslyn compile check for the 16:35 bundle (bandits dev, file-only; writes ONLY to $Out). Adapted from laneAPVFIX\compile_apvfix.ps1 and the F5 review agent's method:
# Unity 6000.6.7f1 DotNetSdk Roslyn csc over every non-Editor .cs under Assets (dirs ending in ~ skipped), UnityEngine modules + Library\ScriptAssemblies package dlls (no Assembly-CSharp / Tests).
# Mode -Final replaces the 8 tree files with bundle\final\<Name>.cs.patched. Default = the tree alone (baseline).  Usage: powershell -File compile_bundle.ps1 [-Final]
param([switch]$Final, [string]$Out = 'C:\Users\dom12\Documents\unity_test_hdrp\_agents\laneLG\bundle\compile_out', [string]$FinDir = 'C:\Users\dom12\Documents\unity_test_hdrp\_agents\laneLG\bundle\final', [string]$Tag = '')
$Project = 'C:\Users\dom12\Documents\unity_test_hdrp'; $U = 'C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Data'; $csc = "$U\DotNetSdk\sdk\8.0.318\Roslyn\bincore\csc.dll"; $fin = $FinDir
New-Item -ItemType Directory -Force $Out | Out-Null
$rel = @{ 'StartupWarm.cs' = 'Assets\Scripts\StartupWarm.cs'; 'GameLoop.cs' = 'Assets\Scripts\GameLoop.cs'; 'LoadingCard.cs' = 'Assets\Scripts\HUD\LoadingCard.cs'; 'LootScreen.cs' = 'Assets\Scripts\Loot\LootScreen.cs'; 'MenuCfg.cs' = 'Assets\Scripts\Menu\MenuCfg.cs'; 'MenuUI.cs' = 'Assets\Scripts\Menu\MenuUI.cs'; 'LootCfg.cs' = 'Assets\Scripts\Loot\LootCfg.cs'; 'QualityTiers.cs' = 'Assets\Scripts\QualityTiers.cs' }
$all = Get-ChildItem "$Project\Assets" -Recurse -Filter *.cs -File | Where-Object { $_.FullName -notmatch '\\[^\\]*~\\' -and $_.FullName -notmatch '\\Editor\\' } | ForEach-Object { $_.FullName }
$swap = @{}; if ($Final) { foreach ($k in $rel.Keys) { $swap[(Join-Path $Project $rel[$k])] = "$fin\$k.patched" } }
$files = @(); foreach ($f in $all) { if ($swap.ContainsKey($f)) { $files += $swap[$f] } else { $files += $f } }
# a .patched file has no .cs extension: csc accepts any path
$ex = '^Assembly-CSharp|Tests|Editor\.dll$'; $sa = Get-ChildItem "$Project\Library\ScriptAssemblies" -Filter *.dll | Where-Object { $_.Name -notmatch $ex }
$defs = 'UNITY_STANDALONE;UNITY_STANDALONE_WIN;UNITY_6000_0_OR_NEWER;UNITY_2022_3_OR_NEWER;ENABLE_INPUT_SYSTEM;ENABLE_LEGACY_INPUT_MANAGER'
$engine = Get-ChildItem "$U\Managed\UnityEngine" -Filter *.dll
$refs = @("-r:$U\NetStandard\ref\2.1.0\netstandard.dll", "-r:$U\DotNetSdk\packs\NETStandard.Library.Ref\2.1.0\ref\netstandard2.1\mscorlib.dll"); foreach ($d in $engine) { $refs += "-r:$($d.FullName)" }; foreach ($d in $sa) { $refs += "-r:$($d.FullName)" }
$name = $(if ($Final) { 'final' } else { 'tree' }) + $Tag
$common = @('-nologo', '-target:library', '-langversion:latest', '-nostdlib', '-nowarn:CS0618,CS0612,CS0649,CS0169,CS0414,CS0219,CS0168,CS0108,CS0114,CS8981,CS0659,CS0661,CS1591', "-define:$defs")
$lines = ((@($common) + $refs + @("-out:$Out\$name.dll")) | ForEach-Object { '"' + $_ + '"' }) + ($files | ForEach-Object { '"' + $_ + '"' }); $rsp = "$Out\$name.rsp"; Set-Content $rsp $lines -Encoding UTF8
$o = & dotnet $csc "@$rsp" 2>&1; $err = @($o | Where-Object { $_ -match 'error CS' }); Set-Content "$Out\$name.log" $o
Write-Host ("{0}: {1} files, errors: {2}" -f $name, $files.Count, $err.Count); $err | Select-Object -First 30 | ForEach-Object { Write-Host "  $_" }
