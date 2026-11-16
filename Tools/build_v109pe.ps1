$u='C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe'; $p='C:\Users\dom12\Documents\unity_test_hdrp'
Set-Location $p
. "$p\Tools\_build_guard.ps1"; Assert-BuildSafe 'pe' -Mode CodeOnly   # RESOURCESBAKE test: the clips are Resources assets, the controller is untouched, so this must behave like a code-only build (0 scenes reopened); own-pid watchdog at 8 GB
& powershell -NoProfile -File '_agents\unitydev\build_preflight.ps1' -Mode CodeOnly; if ($LASTEXITCODE -ne 0) { exit 3 }
& powershell -NoProfile -File 'Tools\compile_check.ps1' | Out-Host; if ($LASTEXITCODE -ne 0) { Write-Host 'COMPILE CHECK FAILED: build refused'; exit 4 }
Remove-Item TestOut\v109pe_done.txt -ErrorAction SilentlyContinue
$t0=Get-Date
New-Item Tools\build_profile.txt -ItemType File -Force | Out-Null
Set-Content Tools\build_dir.txt 'Builds/HDRP_v109pe'
for($try=1; $try -le 2; $try++){
  $b=Start-Process $u -ArgumentList "-batchmode -quit -accept-apiupdate -silent-crashes -projectPath $p -executeMethod RiderClipSetup.BakeAndBuild -logFile $p\TestOut\v109pe_Build.log" -PassThru; if(-not $b.WaitForExit(2400000)){ try { $b.Kill() } catch {} }
  if(-not (Select-String -Path "$p\TestOut\v109pe_Build.log" -Pattern "threw exception" -Quiet)){ break }
}
Set-Content TestOut\v109pe_done.txt "build=$($b.ExitCode) tries=$try secs=$([int]((Get-Date)-$t0).TotalSeconds)"








. "$p\Tools\_build_guard.ps1"; Add-BuiltLine 'pe'

