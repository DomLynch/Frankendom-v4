$u="C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe"; $p="C:\Users\dom12\Documents\unity_test_hdrp"
foreach($step in 'Noop','V25'){ Remove-Item "$p\Shots\_batch_$step.log" -ea 0; $pr=Start-Process $u -ArgumentList '-batchmode','-projectPath',$p,'-executeMethod',"HdrpBatch.$step",'-quit','-logFile',"$p\Shots\_batch_$step.log" -WindowStyle Hidden -PassThru; $pr | Wait-Process }
"done" | Set-Content "$p\Shots\_v25_done.txt"
