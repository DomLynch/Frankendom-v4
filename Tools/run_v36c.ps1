$u="C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe"; $p="C:\Users\dom12\Documents\unity_test_hdrp"
$pr=Start-Process $u -ArgumentList '-batchmode','-quit','-projectPath',$p,'-executeMethod','HdrpBatch.Build','-logFile',"$p\TestOut\v36c_Build.log" -WindowStyle Hidden -PassThru; $pr | Wait-Process; "exit=$($pr.ExitCode)" | Set-Content "$p\TestOut\v36c_done.txt"
