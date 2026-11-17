# build_compileonly.ps1 -Tag <tag from Tools\next_tag.ps1>
# COMPILE-ONLY BUILD (Strategy/Lead 17 Nov): (1) guard + Roslyn compile check, (2) tag folder from the live build via deploy_data.ps1 (hardlinks + current data), (3) Editor batch HdrpCompileOnly.Run compiles the player
# script assemblies to Temp\FkCompileOut, (4) the project's own dlls (Assembly-CSharp + asmdef assemblies that exist in the build's Managed) replace the tag folder's copies: the destination is DELETED first so the
# hardlink to other builds is broken, then copied, (5) build_info.json, BUILT line. No scene is opened. Valid only when no serialized layout changed (guard refuses new public / [SerializeField] fields).
param([Parameter(Mandatory)][string]$Tag)
$u='C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe'; $p='C:\Users\dom12\Documents\unity_test_hdrp'
Set-Location $p
if (-not $env:FK_OWNER) { $env:FK_OWNER = 'unitydev' }
. "$p\Tools\_build_guard.ps1"; Assert-BuildSafe $Tag -Mode CodeOnly
& powershell -NoProfile -File 'Tools\compile_check.ps1' | Out-Host; if ($LASTEXITCODE -ne 0) { Write-Host 'COMPILE CHECK FAILED: build refused (no Editor slot spent)'; exit 4 }
Remove-Item "TestOut\v109${Tag}_done.txt" -ErrorAction SilentlyContinue
$t0 = Get-Date
$live = ([regex]::Match((Get-Content "$p\Builds\PLAY.bat" -Raw), 'HDRP_\w+')).Value
& powershell -NoProfile -File "$p\_agents\laneBUILDSPEED\deploy_data.ps1" -v "109$Tag" -Base $live -Apply -SkipGates -IgnoreAssetChanges | Out-Null
$dst = "$p\Builds\HDRP_v109$Tag"; $man = "$dst\HadesSlice_Data\Managed"
if (-not (Test-Path "$man\Assembly-CSharp.dll")) { Write-Host "no tag folder from deploy_data ($dst)"; exit 5 }
$out = "$p\Temp\FkCompileOut"
$b = Start-Process $u -ArgumentList "-batchmode -quit -accept-apiupdate -silent-crashes -projectPath $p -executeMethod HdrpCompileOnly.Run -compileOut Temp/FkCompileOut -logFile $p\TestOut\v109${Tag}_Build.log" -PassThru
if (-not $b.WaitForExit(900000)) { try { $b.Kill() } catch {} }
$code = $b.ExitCode
$copied = @()
if ($code -eq 0 -and (Test-Path "$out\Assembly-CSharp.dll")) {
  $own = @('Assembly-CSharp') + @(Get-ChildItem "$p\Assets" -Recurse -Filter *.asmdef -ErrorAction SilentlyContinue | ForEach-Object { try { (Get-Content $_.FullName -Raw | ConvertFrom-Json).name } catch { $null } } | Where-Object { $_ })
  foreach ($n in ($own | Sort-Object -Unique)) {
    $src = "$out\$n.dll"; $dd = "$man\$n.dll"
    if ((Test-Path $src) -and (Test-Path $dd)) { Remove-Item $dd -Force; Copy-Item $src $dd -Force; $copied += $n; $pdb = "$out\$n.pdb"; if (Test-Path $pdb) { Copy-Item $pdb "$man\$n.pdb" -Force } }
  }
  $info = [ordered]@{ exe = 'HadesSlice.exe'; dataDir = 'HadesSlice_Data'; version = ''; time = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'); result = 'Succeeded'; productName = 'Frankendom'; companyName = 'Dom Games'; note = 'compile-only: dlls replaced = ' + ($copied -join ',') }
  ($info | ConvertTo-Json) | Set-Content "$dst\build_info.json" -Encoding UTF8
} else { $code = if ($code -eq 0) { 6 } else { $code } }
Set-Content "TestOut\v109${Tag}_done.txt" "build=$code compileonly dlls=$($copied -join ',') secs=$([int]((Get-Date)-$t0).TotalSeconds)"
Add-BuiltLine $Tag
