$p='C:\Users\dom12\Documents\unity_test_hdrp'; $d="$p\Builds\HDRP_v100"; $sa="$d\HadesSlice_Data\StreamingAssets"
$t=0; while(-not (Test-Path "$d\chain_done.txt") -and $t -lt 3600){ Start-Sleep 15; $t+=15 }
Get-Process HadesSlice -ErrorAction SilentlyContinue | % { $_.WaitForExit(600000) | Out-Null }
Set-Location $d
Copy-Item "$sa\physics.json" "$d\physics_shipped.json" -Force
$r=[IO.File]::ReadAllText("$sa\physics.json"); $m=[regex]::Match($r,'"enabled":\s*true'); if($m.Success){ $r=$r.Remove($m.Index,$m.Length).Insert($m.Index,'"enabled": false'); [IO.File]::WriteAllText("$sa\physics.json",$r,(New-Object Text.UTF8Encoding($false))) }
$x=Start-Process "$d\HadesSlice.exe" -ArgumentList '-finisherauto','-finisherlist:decapitation,split_crown,run_through,split_crown_pit',"-finisherout:$d\fin_masteroff",'-silent-crashes','-screen-width 1920','-screen-height 1080','-screen-fullscreen 0','-logFile finisher_masteroff.log' -PassThru; if(-not $x.WaitForExit(500000)){ $x.Kill() }
Copy-Item "$d\physics_shipped.json" "$sa\physics.json" -Force
Set-Content "$d\masteroff_done.txt" "exit=$($x.ExitCode)"
& powershell -ExecutionPolicy Bypass -File "$p\Tools\rebuild_v100.ps1"
