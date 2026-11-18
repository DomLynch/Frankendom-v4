# build_scriptsonly.ps1 -Tag <tag from Tools\next_tag.ps1> [-From HDRP_v109xx]
# SCRIPTS-ONLY BUILD (Strategy/COO 16 Nov 23:31): copy the live build PHYSICALLY (robocopy, no hardlinks: scripts-only rewrites Assembly-CSharp.dll and other builds hardlink it) to Builds\HDRP_v109<tag>,
# then HdrpScriptsOnly.Run builds only the scripts into it (BuildOptions.BuildScriptsOnly): no scene opened or re-serialized. HdrpBatch.Build stays for bakes.
# Guard: Assert-BuildSafe runs the cold detector (public serialized field, non-code Assets, peak history); until a scripts-only take has proven < 8 GB it runs ALONE like any cold take.
param([Parameter(Mandatory)][string]$Tag, [string]$From = '')
$u='C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe'; $p=$env:FK_ROOT
Set-Location $p
if (-not $env:FK_OWNER) { $env:FK_OWNER = 'unitydev' }
. "$p\Tools\_build_guard.ps1"; Assert-BuildSafe $Tag
& powershell -NoProfile -File 'Tools\compile_check.ps1' | Out-Host; if ($LASTEXITCODE -ne 0) { Write-Host 'COMPILE CHECK FAILED: build refused (no Editor slot spent)'; exit 4 }
if (-not $From) { $From = ([regex]::Match((Get-Content "$p\Builds\PLAY.bat" -Raw), 'HDRP_\w+')).Value }
$src = "$p\Builds\$From"; $dst = "$p\Builds\HDRP_v109$Tag"
if (-not (Test-Path "$src\HadesSlice.exe")) { Write-Host "no exe in $src"; exit 5 }
if (Test-Path "$dst\build_info.json") { Write-Host "$dst already built"; exit 6 }
Remove-Item "TestOut\v109${Tag}_done.txt" -ErrorAction SilentlyContinue
$t0 = Get-Date
Write-Host "copying $From -> HDRP_v109$Tag (physical copy)"
& robocopy $src $dst /E /COPY:D /R:1 /W:1 /NFL /NDL /NJH /NJS /NP /MT:16 | Out-Null
if ($LASTEXITCODE -ge 8) { Write-Host "robocopy failed ($LASTEXITCODE)"; exit 7 }
$tc = [int]((Get-Date) - $t0).TotalSeconds
$b = Start-Process $u -ArgumentList "-batchmode -quit -accept-apiupdate -silent-crashes -projectPath $p -executeMethod HdrpScriptsOnly.Run -buildDir Builds/HDRP_v109$Tag -logFile $p\TestOut\v109${Tag}_Build.log" -PassThru
if (-not $b.WaitForExit(1200000)) { try { $b.Kill() } catch {} }
Set-Content "TestOut\v109${Tag}_done.txt" "build=$($b.ExitCode) scriptsonly copy_s=$tc secs=$([int]((Get-Date)-$t0).TotalSeconds)"
Add-BuiltLine $Tag
