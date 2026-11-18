# build_texstream.ps1 -Tag <tag from Tools\next_tag.ps1>
# TEXTURE STREAMING BAKE (Strategy GO 17 Nov), two phases (COO 15:53, after the 38 GB stop of run 1):
#  phase 1 = TexStreamBatch.Run -texphase:1 -nographics: streamingMipmaps on the world / prop / terrain texture importers in chunks, NO scene opened, then quit. Peak is logged and posted before phase 2.
#  phase 2 = TexStreamBatch.Run -texphase:2: the player build (BuildPlayerCmd.Run) into Builds\HDRP_v109<tag>.
# A bake: runs ALONE (guard default mode). The caller sets FK_BAKE_LIFT=<tag> (per-slot lift of the scene-bake freeze) and posts EDITOR TAKEN.
param([Parameter(Mandatory)][string]$Tag)
$u='C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe'; $p=$env:FK_ROOT
Set-Location $p
if (-not $env:FK_OWNER) { $env:FK_OWNER = 'unitydev' }
. "$p\Tools\_build_guard.ps1"; Assert-BuildSafe $Tag
& powershell -NoProfile -File 'Tools\compile_check.ps1' | Out-Host; if ($LASTEXITCODE -ne 0) { Write-Host 'COMPILE CHECK FAILED: build refused (no Editor slot spent)'; exit 4 }
function Board($t) { try { $fs=[IO.File]::Open("$p\_agents\COORDINATION.md",'Append','Write','ReadWrite'); $bb=[Text.Encoding]::UTF8.GetBytes("`r`n"+(Get-Date -Format 'yyyy-MM-dd HH:mm')+" "+$t); $fs.Write($bb,0,$bb.Length); $fs.Close() } catch {} }
Remove-Item "TestOut\v109${Tag}_done.txt" -ErrorAction SilentlyContinue
$t0 = Get-Date
New-Item Tools\build_profile.txt -ItemType File -Force | Out-Null
Set-Content Tools\build_dir.txt "Builds/HDRP_v109$Tag"
# phase 1
$b = Start-Process $u -ArgumentList "-batchmode -nographics -quit -accept-apiupdate -silent-crashes -projectPath $p -executeMethod TexStreamBatch.Run -texphase:1 -logFile $p\TestOut\v109${Tag}_Tex1.log" -PassThru
if (-not $b.WaitForExit(1800000)) { try { $b.Kill() } catch {} }
$p1 = $b.ExitCode
$peak = 0; try { $peak = (Import-Csv "TestOut\v109${Tag}_mem.csv" | ForEach-Object { [double]$_.peakMyUnityGB } | Measure-Object -Maximum).Maximum } catch {}
$chunks = (Select-String -Path "TestOut\v109${Tag}_Tex1.log" -Pattern 'TEXSTREAM chunk' -ErrorAction SilentlyContinue | Select-Object -Last 1).Line
$sum = (Select-String -Path "TestOut\v109${Tag}_Tex1.log" -Pattern 'TEXSTREAM importers' -ErrorAction SilentlyContinue | Select-Object -First 1).Line
Board "unitydev TEXSTREAM PHASE 1 (importer flags, no scene, -nographics) exit=$p1 Editor peak $peak GB, $([int]((Get-Date)-$t0).TotalSeconds) s; $sum; last: $chunks"
if ($p1 -ne 0) { Set-Content "TestOut\v109${Tag}_done.txt" "build=$p1 phase1 failed peak=$peak"; Add-BuiltLine $Tag; exit 6 }
# phase 2: ALONE (COO 15:53): wait until no QA slot of any lane, no player, 28 GB free (the idle laptop has ~35 GB)
while ($true) { $ns=0; foreach ($sf in @(Get-ChildItem "$p\_agents\qa_slots" -Filter 'slot_*.json' -ErrorAction SilentlyContinue)) { try { $sj = Get-Content $sf.FullName -Raw | ConvertFrom-Json; if (Get-Process -Id $sj.pid -ErrorAction SilentlyContinue) { $ns++ } } catch {} }; $fr=(Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory/1MB; $pl=@(Get-Process HadesSlice,FronteirDom,FrontierDom -ErrorAction SilentlyContinue).Count; if ($ns -eq 0 -and $pl -eq 0 -and $fr -ge 26) { break }; Start-Sleep 5 }   # COO 15:59: the player build alone peaks ~39 GB: start floor 26 GB free (nographics peak 21.2 GB + 5); only slots whose pid is alive count
Board "unitydev EDITOR TAKEN frankendom ALONE (texture streaming bake phase 2 = player build, expected peak 30-36 GB, ~15 min); FD APV bake waits for the RELEASED line"
for ($try = 1; $try -le 2; $try++) {
  $ng = $(if ($try -eq 1) { "-nographics " } else { "" })   # COO 15:58: try the player build without a graphics device first (iGPU memory counts as RAM); fall back to the normal build if it fails
  $b = Start-Process $u -ArgumentList "-batchmode ${ng}-quit -accept-apiupdate -silent-crashes -projectPath $p -executeMethod TexStreamBatch.Run -texphase:2 -logFile $p\TestOut\v109${Tag}_Build.log" -PassThru
  if (-not $b.WaitForExit(3000000)) { try { $b.Kill() } catch {} }
  Board "unitydev TEXSTREAM PHASE 2 try $try (nographics=$($try -eq 1)) exit=$($b.ExitCode)"
  if ($b.ExitCode -eq 0 -and (Test-Path "$p\Builds\HDRP_v109$Tag\build_info.json")) { break }
}
Set-Content "TestOut\v109${Tag}_done.txt" "build=$($b.ExitCode) tries=$try secs=$([int]((Get-Date)-$t0).TotalSeconds) phase1peak=$peak"
Add-BuiltLine $Tag



