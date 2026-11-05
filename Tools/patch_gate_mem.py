import re
T=r"C:\Users\dom12\Documents\unity_test_hdrp\Tools"
new_wait='''$t0=Get-Date; $killed=$false; $nx=0; while(-not $p.HasExited){ Start-Sleep 2; $p.Refresh(); $ws=[math]::Round($p.WorkingSet64/1GB,1); if(((Get-Date)-$t0).TotalSeconds -ge $nx){ Add-Content gate_mem.log ("{0} {1} {2:F0}s {3} GB" -f (Get-Date -Format HH:mm:ss),$name,((Get-Date)-$t0).TotalSeconds,$ws); $nx+=10 }; if($ws -gt 16){ $p.Kill(); $killed=$true; Add-Content gate_mem.log "$name MEMKILL at $ws GB"; break }; if(((Get-Date)-$t0).TotalSeconds -gt $SEC){ $p.Kill(); "$name TIMEOUT"; $killed=$true; break } }; if($killed -and (Select-String -Path gate_mem.log -Pattern "$name MEMKILL" -Quiet)){ "$name MEMKILL (working set > 16 GB, see gate_mem.log)" } elseif(-not $killed){ "$name exit=$($p.ExitCode)" }'''
for f,pat in (("gate_lean.ps1", r'if\(-not \$p\.WaitForExit\(\$sec\*1000\)\)\{ \$p\.Kill\(\); "\$name TIMEOUT" \} else \{ "\$name exit=\$\(\$p\.ExitCode\)" \}'),
              ("gate.ps1", r'if\(-not \$p\.WaitForExit\(\$sec\*1000\)\)\{ \$p\.Kill\(\); "\$name TIMEOUT" \} else \{ "\$name exit=\$\(\$p\.ExitCode\)" \}')):
    P=T+"\\"+f; s=open(P,encoding='utf-8').read()
    if 'gate_mem.log' in s: print(f,'ALREADY'); continue
    n,c=re.subn(pat,lambda m:new_wait.replace('$SEC','($sec)'),s)
    print(f,c)
    if c: open(P,'w',encoding='utf-8',newline='').write(n)
