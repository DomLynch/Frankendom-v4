$u="C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe"; $p="C:\Users\dom12\Documents\unity_test_hdrp"; $res=@()
foreach($step in 'HdrpBatch.World','MHIntegrate.Run','HdrpBatch.Build'){ $n=$step.Split('.')[1]; $pr=Start-Process $u -ArgumentList '-batchmode','-quit','-projectPath',$p,'-executeMethod',$step,'-logFile',"$p\TestOut\v36b_$n.log" -WindowStyle Hidden -PassThru; $pr | Wait-Process; $res += "$step exit=$($pr.ExitCode)"; if($pr.ExitCode -ne 0 -and $n -ne 'Build'){ break } }
$res | Set-Content "$p\TestOut\v36b_done.txt"
