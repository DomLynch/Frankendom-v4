$f='C:\Users\dom12\Documents\unity_test_hdrp\Assets\Data\hdrp_lighting_ultra.json'
Copy-Item $f ($f+'.bak_world') -Force
$t=[IO.File]::ReadAllText($f)
$m=[regex]::Match($t,'"fog":\s*\{.*?\}(?=\s*,\s*"post")', 'Singleline')
if(-not $m.Success){ throw 'fog block not found' }
$b=$m.Value
$b=[regex]::Replace($b,'"maxHeight":\s*[-\d.]+','"maxHeight":  38')
$b=[regex]::Replace($b,'"baseHeight":\s*[-\d.]+','"baseHeight":  -1.0')
$b=[regex]::Replace($b,'"albedo":\s*\[[^\]]*\]','"albedo":  [ 0.115, 0.115, 0.113 ]')
$t=$t.Remove($m.Index,$m.Length).Insert($m.Index,$b)
[IO.File]::WriteAllText($f,$t)
$null=Get-Content $f -Raw | ConvertFrom-Json   # parses?
'fog patched'
