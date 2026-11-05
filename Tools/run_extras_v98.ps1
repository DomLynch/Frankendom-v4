$p='C:\Users\dom12\Documents\unity_test_hdrp'; $d="$p\Builds\HDRP_v98"; Set-Location $d
$fl="$d\HadesSlice_Data\StreamingAssets\runtime_flags_balance.json"
Remove-Item "$d\extras.txt" -ErrorAction SilentlyContinue
function RunJob($name,$jobArgs){ $x=Start-Process "$d\HadesSlice.exe" -ArgumentList ($jobArgs+@('-silent-crashes','-screen-fullscreen 0',"-logFile $name.log")) -PassThru; if(-not $x.WaitForExit(1500000)){ $x.Kill() }; Add-Content "$d\extras.txt" "$name exit=$($x.ExitCode)" }
RunJob 'balance10' @('-balanceauto','-seeds:10','-balanceseed:1234','-fast',"-flags:$fl",'-screen-width 1280','-screen-height 720'); Copy-Item "$d\balance_summary.txt" "$d\balance_summary_10seeds.txt" -Force
RunJob 'maul_balance' @('-weapon','pitborn_maul','-balanceauto','-seeds:3','-balanceseed:1234','-fast',"-flags:$fl",'-screen-width 1280','-screen-height 720'); Copy-Item "$d\balance_summary.txt" "$d\balance_summary_maul.txt" -Force
RunJob 'maul_woundshot' @('-weapon','pitborn_maul','-woundshot','-screen-width 1920','-screen-height 1080',"-out:$d\woundshot_maul")
RunJob 'woundshot' @('-woundshot','-screen-width 1920','-screen-height 1080',"-out:$d\woundshot")
Set-Content "$d\chain_done.txt" "extras done"
