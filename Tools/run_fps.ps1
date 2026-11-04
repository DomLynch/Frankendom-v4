$p="--project-path","C:/Users/dom12/Documents/unity_test_hdrp","--caller","plugin","--skill","unity-cli"
Remove-Item C:\Users\dom12\Documents\unity_test_hdrp\Shots\_build_out.txt -ErrorAction SilentlyContinue
unity command script eval --timeout 3000 @p 'BuildPlayerCmd.Run();' | Out-File C:\Users\dom12\Documents\unity_test_hdrp\Shots\_build_out.txt
if (Get-Process UnrealEditor* -ErrorAction SilentlyContinue) { "UNREAL RUNNING" | Out-File C:\Users\dom12\Documents\unity_test_hdrp\Shots\_fps_note.txt; exit }
$exe="C:\Users\dom12\Documents\unity_test_hdrp\Builds\HDRP_ultra3\HadesSlice.exe"
$out="C:\Users\dom12\Documents\unity_test_hdrp\PlayShots_ultra"
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
$pr=Start-Process $exe -ArgumentList "-screen-width","1920","-screen-height","1080","-screen-fullscreen","0","-autoplay","-out:$out" -PassThru
$pr | Wait-Process -Timeout 240 -ErrorAction SilentlyContinue
if (!$pr.HasExited) { $pr.Kill() }
