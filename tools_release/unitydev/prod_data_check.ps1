# prod_data_check.ps1 (Lead, 12 Nov): a build may only go live with PRODUCTION data. Usage: prod_data_check.ps1 -Build HDRP_v109xx ; exit 0 = ok, 6 = leak.
param([Parameter(Mandatory=$true)][string]$Build)
$sa = "$($env:FK_ROOT)\Builds\$Build\HadesSlice_Data\StreamingAssets"
function J($f){ (Get-Content "$sa\$f" -Raw).TrimStart([char]0xFEFF) | ConvertFrom-Json }
$bad=@()
try { $fb=J 'fight_balance.json'; foreach($f in $fb.fighters){ if($null -ne $f.takenMul -and [double]$f.takenMul -ne 1.0){ $bad += "fight_balance takenMul $($f.key)=$($f.takenMul)" } } } catch { $bad += "fight_balance unreadable" }
try { $m=J 'menu.json'; if(-not $m.enabled){ $bad += 'menu.json enabled=false (no pause menu)' } } catch { $bad += 'menu.json unreadable' }
try { $p=J 'direction_prompt.json'; if($p.prefsKey -match 'dev|test'){ $bad += "direction_prompt prefsKey=$($p.prefsKey)" } } catch { $bad += 'direction_prompt unreadable' }
try { $o=J 'fps_overlay.json'; if($o.enabled){ $bad += 'fps_overlay enabled=true (Dom finish order: OFF for alpha)' } } catch { }
if($bad.Count -gt 0){ Write-Host ('PROD DATA CHECK FAILED: ' + ($bad -join '; ')); exit 6 }
Write-Host 'PROD DATA CHECK OK (takenMul 1.0 all fighters, menu on, prompt key production, fps overlay off)'; exit 0
