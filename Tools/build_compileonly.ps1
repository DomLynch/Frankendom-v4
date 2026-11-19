# build_compileonly.ps1 -Tag <tag from Tools\next_tag.ps1> [-DryRun [-TreeRoot <dir> -BaseInfoFile <build_info.json> -BaseName <name>]]
# COMPILE-ONLY BUILD (Strategy/Lead 17 Nov; hardened 18 Nov after the outside audit):
#  0. SIGNATURE REFUSAL (exit 5 + board line, before any tag or Editor slot is spent): compares the current tree's serialized-field / boot-hook / script-file signatures with the BASE build's (Tools\_build_sig.ps1,
#     recorded in build_info.json: baseCommit, fullBakeId, serializedSig, bootSig, scriptFilesSig). A changed serialized layout, a removed/renamed script file or a grown [RuntimeInitializeOnLoadMethod] set means "needs a full build".
#  1. guard + Roslyn compile check, 2. candidate is built in a FRESH STAGING dir (Builds\_stage_<tag>) by deploy_data.ps1 (hardlinks + current data); its exit code and RESULT line are required,
#  3. Editor batch HdrpCompileOnly.Run compiles the player script assemblies to TestOut\FkCompileOut (cleared first; dlls must be fresh, no 'error CSxxxx' in the log),
#  4. the project's own dlls replace the staged copies (destination deleted first to break the hardlink; hashes verified), 5. only then is the staging dir moved to Builds\HDRP_v109<tag> with build_info.json.
#  A failed step removes the staging dir (no candidate exists for flip.ps1) and the script's EXIT CODE is the compile result: 0 ok, 4 compile check, 5 refused/needs full build, 6 compile or copy failed, 7 deploy failed, else the Unity exit code.
# -DryRun: only step 0 (against -TreeRoot / -BaseInfoFile when given), prints the verdict and the board line it would write; never starts Unity, never writes the board.
param([Parameter(Mandatory)][string]$Tag, [switch]$DryRun, [string]$TreeRoot = '', [string]$BaseInfoFile = '', [string]$BaseName = '')
$u='C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe'; $p='C:\Users\dom12\Documents\unity_test_hdrp'
Set-Location $p
if (-not $env:FK_OWNER) { $env:FK_OWNER = 'unitydev' }
function Add-Board([string]$m) { try { $fs=[IO.File]::Open("$p\_agents\COORDINATION.md",'Append','Write','ReadWrite'); $bb=[Text.Encoding]::UTF8.GetBytes("`r`n"+(Get-Date -Format 'yyyy-MM-dd HH:mm')+" "+$m); $fs.Write($bb,0,$bb.Length); $fs.Close() } catch { } }
. "$p\Tools\_build_sig.ps1"
# ---- 0. signature compare against the base build (no cap on files; fails closed)
try {
  $sc = Test-CodeOnlySig -Repo $(if ($TreeRoot) { $TreeRoot } else { $p }) -TreeRoot $TreeRoot -BaseInfoFile $BaseInfoFile -BaseName $BaseName
  Write-Host ("signature base: " + $sc.baseName + " (" + $sc.baseHow + ")"); foreach ($n in $sc.notes) { Write-Host ("signature note: " + $n) }
  $d = $sc.detail; $full = @(); foreach ($k in 'serRemoved','serAdded','filesRemoved','filesAdded','bootAdded','bootRemoved') { foreach ($x in $d[$k]) { $full += "$k $x" } }
  if (-not $DryRun) { New-Item -ItemType Directory "$p\TestOut" -Force | Out-Null; Set-Content "$p\TestOut\v109${Tag}_sigdiff.txt" $full -Encoding UTF8 }
  $refuse = $sc.refuse; $why = ($sc.reasons -join ' | ')
} catch { $refuse = $true; $why = "signature compare could not run (fails closed): " + $_.Exception.Message }
if ($refuse) {
  $m = "BUILD REFUSED frankendom ${Tag}: compile-only not allowed, NEEDS A FULL BUILD: $why"
  Write-Host $m; if ($DryRun) { Write-Host "DRYRUN board line: $m" } else { Add-Board $m }
  exit 5
}
Write-Host 'signature compare: PASS (serialized layout, script files and boot hooks match the base build)'
if ($DryRun) { Write-Host 'DRYRUN: would continue to guard, compile check, staging deploy, Editor compile (nothing started)'; exit 0 }

# BANNED ASSET GUARD (19 Nov, goblin purge): refuse before any tag or Editor slot is spent.
$bannedHits = @(& "$p\Tools\banned_assets_check.ps1" -Root "$p\Assets" -Caller 'build_compileonly'); if ($bannedHits.Count) { $bannedHits | ForEach-Object { Write-Host $_ }; Write-Host "BUILD REFUSED $Tag (banned asset), exit 5"; exit 5 }
. "$p\Tools\_build_guard.ps1"; Assert-BuildSafe $Tag -Mode CodeOnly
& powershell -NoProfile -File 'Tools\compile_check.ps1' | Out-Host; if ($LASTEXITCODE -ne 0) { Write-Host 'COMPILE CHECK FAILED: build refused (no Editor slot spent)'; exit 4 }
Remove-Item "TestOut\v109${Tag}_done.txt" -ErrorAction SilentlyContinue
$t0 = Get-Date
$final = "$p\Builds\HDRP_v109$Tag"
if (Test-Path $final) { Write-Host "candidate $final already exists: refusing to overwrite"; exit 6 }
$live = ([regex]::Match((Get-Content "$p\Builds\PLAY.bat" -Raw), 'HDRP_\w+')).Value
# ---- fresh staging dir: nothing under Builds\HDRP_* exists until every step has passed
$stage = "$p\Builds\_stage_$Tag"; if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }; New-Item -ItemType Directory $stage | Out-Null
$dst = "$stage\HDRP_v109$Tag"; $man = "$dst\HadesSlice_Data\Managed"; $out = "$p\TestOut\FkCompileOut"
$copied = @(); $code = 0; $fail = ''
function Fail([int]$c, [string]$why) { $script:code = $c; $script:fail = $why }
$dlog = "$p\TestOut\v109${Tag}_deploy_child.log"
$dout = & powershell -NoProfile -File "$p\_agents\laneBUILDSPEED\deploy_data.ps1" -v "109$Tag" -Base $live -Apply -SkipGates -IgnoreAssetChanges -OutRoot $stage 2>&1 | Out-String
$drc = $LASTEXITCODE; Set-Content $dlog $dout -Encoding UTF8
if ($drc -ne 0 -or $dout -notmatch 'RESULT READY' -or -not (Test-Path "$man\Assembly-CSharp.dll") -or -not (Test-Path "$dst\build_info.json")) { Fail 7 "deploy_data child failed (exit $drc, see $dlog)" }
if ($code -eq 0) {
  if (Test-Path $out) { Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue }   # stale dlls from an earlier run must never pass the DLL-exists test
  $bpU=(Get-Date).AddMinutes(4).ToString('HH:mm'); Add-Board "BUILD PHASE player frankendom until $bpU (compile-only Editor $Tag, peak 3 GB)"
  $b = Start-Process $u -ArgumentList "-batchmode -nographics -quit -accept-apiupdate -silent-crashes -projectPath $p -executeMethod HdrpCompileOnly.Run -compileOut TestOut/FkCompileOut -logFile $p\TestOut\v109${Tag}_Build.log" -PassThru
  try { $b.PriorityClass = 'BelowNormal' } catch {}   # Dom plays on the same iGPU: low priority and no D3D device (-nographics)
  if (-not $b.WaitForExit(900000)) { try { $b.Kill() } catch {}; Fail 6 'Editor compile timed out (killed)' }
  elseif ($b.ExitCode -ne 0) { Fail $b.ExitCode "Editor exit code $($b.ExitCode)" }
}
if ($code -eq 0) {
  $logf = "$p\TestOut\v109${Tag}_Build.log"
  if ((Test-Path $logf) -and (Select-String -Path $logf -Pattern 'error CS\d+' -Quiet)) { Fail 6 "compile errors in $logf" }
  elseif (-not (Test-Path "$out\Assembly-CSharp.dll")) { Fail 6 'no Assembly-CSharp.dll in the compile output' }
  elseif ((Get-Item "$out\Assembly-CSharp.dll").LastWriteTime -lt $t0) { Fail 6 'Assembly-CSharp.dll in the compile output is older than this run (stale)' }
}
if ($code -eq 0) {
  $own = @('Assembly-CSharp') + @(Get-ChildItem "$p\Assets" -Recurse -Filter *.asmdef -ErrorAction SilentlyContinue | ForEach-Object { try { (Get-Content $_.FullName -Raw | ConvertFrom-Json).name } catch { $null } } | Where-Object { $_ })
  foreach ($n in ($own | Sort-Object -Unique)) {
    $src = "$out\$n.dll"; $dd = "$man\$n.dll"
    if ((Test-Path $src) -and (Test-Path $dd)) { Remove-Item $dd -Force; Copy-Item $src $dd -Force; $copied += $n; $pdb = "$out\$n.pdb"; if (Test-Path $pdb) { Copy-Item $pdb "$man\$n.pdb" -Force }
      if ((Get-FileHash $src -Algorithm SHA256).Hash -ne (Get-FileHash $dd -Algorithm SHA256).Hash) { Fail 6 "hash mismatch after copying $n.dll"; break } }
  }
  if ($code -eq 0 -and $copied -notcontains 'Assembly-CSharp') { Fail 6 'Assembly-CSharp.dll was not replaced in the candidate' }
}
if ($code -eq 0) {
  try {   # amend the deploy_data build_info (keeps baseCommit/fullBakeId/signatures/realBuildTime from the base); source = compileonly so flip.ps1 reports CODE
    $o0 = Get-Content "$dst\build_info.json" -Raw | ConvertFrom-Json
    if (-not $o0.fullBakeId) { Set-BuildInfoSig $dst $live $sc.baseSig $sc.baseCommit $true 'legacyEstimate' }   # legacy base without fields: pin the candidate to the estimated base commit (sigDirty true = not exact)
    $o = Get-Content "$dst\build_info.json" -Raw | ConvertFrom-Json; $hs = Get-GitHeadState $p
    foreach ($kv in @(@('result','Succeeded'), @('source','compileonly'), @('time',(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')), @('version',"v109$Tag"), @('note','compile-only: dlls replaced = ' + ($copied -join ',')), @('codeCommit',$hs.commit), @('codeDirty',[bool]$hs.dirty), @('compileOnlyBase',$live))) {
      if ($o.PSObject.Properties[$kv[0]]) { $o.($kv[0]) = $kv[1] } else { $o | Add-Member -NotePropertyName $kv[0] -NotePropertyValue $kv[1] } }
    if (-not $o.fullBakeId) { Fail 6 'base build_info has no fullBakeId after deploy (cannot pin the candidate)' }
    [IO.File]::WriteAllText("$dst\build_info.json", ($o | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
  } catch { Fail 6 "build_info write failed: $($_.Exception.Message)" }
}
if ($code -eq 0) { try { Move-Item $dst $final -ErrorAction Stop } catch { Fail 6 "could not move the staged candidate into Builds: $($_.Exception.Message)" } }
if ($code -ne 0) { Write-Host "COMPILE-ONLY FAILED ($code): $fail; removing the incomplete candidate"; if (Test-Path $final -PathType Container) { if (-not (Test-Path "$final\build_info.json")) { Remove-Item $final -Recurse -Force -ErrorAction SilentlyContinue } } }
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
Set-Content "TestOut\v109${Tag}_done.txt" "build=$code compileonly dlls=$($copied -join ',') secs=$([int]((Get-Date)-$t0).TotalSeconds)$(if ($fail) { ' fail=' + $fail })"
Add-BuiltLine $Tag
exit $code
