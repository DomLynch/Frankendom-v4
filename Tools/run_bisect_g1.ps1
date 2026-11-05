$b='C:\Users\dom12\Documents\unity_test_hdrp\Builds\HDRP_v94'; Set-Location $b; $sa="$b\HadesSlice_Data\StreamingAssets"; $bk="$b\sa_backup_bisect"; New-Item -ItemType Directory -Force $bk | Out-Null
foreach($f in 'death.json','head_clamp.json','weapon_grip.json','hud.json'){ if(-not (Test-Path "$bk\$f")){ Copy-Item "$sa\$f" "$bk\$f" -Force } }
$enc=New-Object Text.UTF8Encoding($false)
$j=Get-Content "$sa\death.json" -Raw | ConvertFrom-Json; $j.enabled=$false; [IO.File]::WriteAllText("$sa\death.json",($j | ConvertTo-Json -Depth 12),$enc)
$r=[IO.File]::ReadAllText("$sa\head_clamp.json"); $r=$r.Replace('"maxUpDeg": 4,','"maxUpDeg": 90,'); [IO.File]::WriteAllText("$sa\head_clamp.json",$r,$enc)
$j=Get-Content "$sa\weapon_grip.json" -Raw | ConvertFrom-Json; foreach($c in $j.characters){ if($c.name -eq 'Joan' -and $c.states -and $c.states.Count -gt 0){ $c.enabled=$false } }; [IO.File]::WriteAllText("$sa\weapon_grip.json",($j | ConvertTo-Json -Depth 12),$enc)
$j=Get-Content "$sa\hud.json" -Raw | ConvertFrom-Json; $j.guardDir.enabled=$false; [IO.File]::WriteAllText("$sa\hud.json",($j | ConvertTo-Json -Depth 20),$enc)
Remove-Item "$b\verify_out_g1" -Recurse -ErrorAction SilentlyContinue
$p=Start-Process "$b\HadesSlice.exe" -ArgumentList '-verifyGround','-verifyOut:verify_out_g1','-noprewarm92','-silent-crashes','-screen-width 1920','-screen-height 1080','-screen-fullscreen 0','-logFile verify_g1.log' -PassThru
if(-not $p.WaitForExit(420000)){ $p.Kill() }
foreach($f in 'death.json','head_clamp.json','weapon_grip.json','hud.json'){ Copy-Item "$bk\$f" "$sa\$f" -Force }
'done' | Set-Content "$b\g1_done.txt"
