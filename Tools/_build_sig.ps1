# _build_sig.ps1 - build signatures for the compile-only guard (audit 18 Nov: "Pin compile-only to the build it extends").
# Dot-source: . "$PSScriptRoot\_build_sig.ps1".  Pure functions, no Unity; only Set-BuildInfoSig / Write-SigSidecar write files.
# Signatures (taken over ALL .cs under Assets\Scripts, no file cap; comments are stripped so a comment-only edit changes nothing):
#   serializedSig  = hash of the sorted set of serialized field declarations (public / [SerializeField] / [field: SerializeField]; not static/const/readonly/[NonSerialized];
#                    initial values ignored) in files that hold a MonoBehaviour / ScriptableObject / [Serializable] type (files named in Tools\_serfield_exempt.txt are skipped, as before)
#   bootSig        = hash of every [RuntimeInitializeOnLoadMethod] occurrence as "file::Method"
#   scriptFilesSig = hash of the sorted list of .cs file paths (catches removed / renamed / added files)
# build_info.json fields: baseCommit, fullBakeId, serializedSig, bootSig, scriptFilesSig, sigDirty, sigAt, sigVersion (item lists live in TestOut\buildsig\<fullBakeId>.json).
$script:SigProject = 'C:\Users\dom12\Documents\unity_test_hdrp'
function Get-SigHash([string[]]$items) { $s = (@($items | Sort-Object -Unique -CaseSensitive) -join "`n"); $b = [Text.Encoding]::UTF8.GetBytes($s); ([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($b)) -replace '-', '').Substring(0, 16).ToLower() }
function Remove-CsComments([string]$t) { $t = [regex]::Replace($t, '/\*[\s\S]*?\*/', ''); [regex]::Replace($t, '(?m)//.*$', '') }
function Get-SerItems([string]$text, [string]$rel) {
    $t = Remove-CsComments $text
    if ($t -notmatch '\b(MonoBehaviour|ScriptableObject|StateMachineBehaviour|NetworkBehaviour)\b|\[\s*(System\.)?Serializable\s*\]') { return @() }
    $items = New-Object System.Collections.Generic.List[string]; $pending = ''
    $t = [regex]::Replace($t, '([;{}])', "`$1`n")   # one statement per line, so one-line classes (public class X : MonoBehaviour { public int hp; }) are seen
    foreach ($raw in ($t -split "`n")) {
        $ln = $raw.Trim(); if ($ln -eq '') { continue }
        if ($ln -match '^(\[[^\]]*\]\s*)+$') { $pending += ' ' + $ln; continue }
        $attrs = $pending; $pending = ''
        $m = [regex]::Match($ln, '^((?:\[[^\]]*\]\s*)*)(.*)$'); $attrs += ' ' + $m.Groups[1].Value; $r = $m.Groups[2].Value.Trim()
        if ($attrs -match 'NonSerialized') { continue }
        if ($attrs -match 'field\s*:\s*SerializeField') {
            $pm = [regex]::Match($r, '^(?:(?:public|private|protected|internal)\s+)?([\w<>\[\],\.\?]+)\s+(\w+)\s*\{'); if ($pm.Success) { $items.Add("$rel::$($pm.Groups[1].Value) $($pm.Groups[2].Value) (autoprop)") }; continue
        }
        $ini = ($r -match '=\s*new\b[^;]*\{\s*$')   # field with an object initializer: 'public Foo f = new Foo {' (the rest of the initializer is split over later lines)
        if (($r -notmatch ';\s*$' -and -not $ini) -or ($r -match '[}]|=>' -and -not $ini)) { continue }
        if ($r -match '\{' -and -not $ini) { continue }
        $d = ($r -replace '[;{]\s*$', ''); $eq = $d.IndexOf('='); if ($eq -ge 0) { $d = $d.Substring(0, $eq) }; $d = $d.Trim()
        if ($d -match '[()]') { continue }
        $fm = [regex]::Match($d, '^(?<mods>(?:(?:public|private|protected|internal|static|readonly|const|volatile|new)\s+)*)(?<type>[\w<>\[\],\.\?\s]+?)\s+(?<names>\w+(?:\s*,\s*\w+)*)$')
        if (-not $fm.Success) { continue }
        $mods = $fm.Groups['mods'].Value
        if ($mods -match '\b(static|const|readonly)\b') { continue }
        if ($mods -notmatch '\bpublic\b' -and $attrs -notmatch 'SerializeField') { continue }
        if ($fm.Groups['type'].Value -match '^(return|using|namespace|class|struct|enum|interface|delegate|event|else|goto|throw)\b') { continue }
        $ty = ($fm.Groups['type'].Value -replace '\s+', ' ') -replace '\s*([<>,\[\]])\s*', '$1'
        foreach ($n in ($fm.Groups['names'].Value -split '\s*,\s*')) { $items.Add("$rel::$ty $n") }
    }
    return $items.ToArray()
}
function Get-BootItems([string]$text, [string]$rel) {
    $t = Remove-CsComments $text; $o = @()
    foreach ($m in [regex]::Matches($t, '\[\s*(?:UnityEngine\.)?RuntimeInitializeOnLoadMethod[^\]]*\]\s*(?:\[[^\]]*\]\s*)*(?:(?:public|private|internal|protected|static|unsafe)\s+)*[\w<>\.]+\s+(\w+)\s*\(')) { $o += "$rel::$($m.Groups[1].Value)" }
    return $o
}
# Root = a folder that contains Assets\Scripts (project root, a fixture, or an extracted git archive)
function Get-BuildSig([string]$Root, [string]$ExemptFile = '') {
    $sd = Join-Path $Root 'Assets\Scripts'; if (-not (Test-Path $sd)) { throw "no Assets\Scripts under $Root" }
    if (-not $ExemptFile) { $ExemptFile = Join-Path $script:SigProject 'Tools\_serfield_exempt.txt' }
    $ex = @(Get-Content $ExemptFile -ErrorAction SilentlyContinue | Where-Object { $_ -and $_ -notmatch '^\s*#' } | ForEach-Object { $_.Trim() })
    $files = New-Object System.Collections.Generic.List[string]; $ser = New-Object System.Collections.Generic.List[string]; $boot = New-Object System.Collections.Generic.List[string]
    $rootLen = (Resolve-Path $Root).Path.TrimEnd('\').Length + 1
    foreach ($f in [IO.Directory]::EnumerateFiles($sd, '*.cs', [IO.SearchOption]::AllDirectories)) {
        $rel = $f.Substring($rootLen).Replace('\', '/'); $files.Add($rel)
        $txt = [IO.File]::ReadAllText($f)
        if ($ex -notcontains [IO.Path]::GetFileName($f)) { foreach ($i in (Get-SerItems $txt $rel)) { $ser.Add($i) } }
        foreach ($i in (Get-BootItems $txt $rel)) { $boot.Add($i) }
    }
    [pscustomobject]@{ files = @($files | Sort-Object -Unique -CaseSensitive); ser = @($ser | Sort-Object -Unique -CaseSensitive); boot = @($boot | Sort-Object -Unique -CaseSensitive)
        scriptFilesSig = (Get-SigHash $files); serializedSig = (Get-SigHash $ser); bootSig = (Get-SigHash $boot) }
}
function Get-BuildSigFromCommit([string]$Repo, [string]$Commit, [string]$ExemptFile = '') {
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("bsig_" + [guid]::NewGuid().ToString('N')); New-Item -ItemType Directory $tmp | Out-Null
    try {
        $tar = "$tmp.tar"; & git -C $Repo archive --format=tar -o $tar $Commit Assets/Scripts 2>$null; if ($LASTEXITCODE -ne 0) { throw "git archive $Commit failed" }
        & tar -xf $tar -C $tmp; if ($LASTEXITCODE -ne 0) { throw 'tar extract failed' }
        return (Get-BuildSig $tmp $ExemptFile)
    } finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue; Remove-Item "$tmp.tar" -Force -ErrorAction SilentlyContinue }
}
function Write-SigSidecar([string]$FullBakeId, $Sig) {
    $d = Join-Path $script:SigProject 'TestOut\buildsig'; New-Item -ItemType Directory $d -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $d "$FullBakeId.json"), ([pscustomobject]@{ files = $Sig.files; ser = $Sig.ser; boot = $Sig.boot } | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding($false)))
}
# Adds/overwrites the signature fields in <BuildDir>\build_info.json (read-modify-write, other fields kept)
function Set-BuildInfoSig([string]$BuildDir, [string]$FullBakeId, $Sig, [string]$Commit, [bool]$Dirty, [string]$SigAt) {
    $f = Join-Path $BuildDir 'build_info.json'; $o = Get-Content $f -Raw | ConvertFrom-Json
    foreach ($kv in @(@('baseCommit', $Commit), @('fullBakeId', $FullBakeId), @('serializedSig', $Sig.serializedSig), @('bootSig', $Sig.bootSig), @('scriptFilesSig', $Sig.scriptFilesSig), @('sigDirty', $Dirty), @('sigAt', $SigAt), @('sigVersion', 1))) {
        if ($o.PSObject.Properties[$kv[0]]) { $o.($kv[0]) = $kv[1] } else { $o | Add-Member -NotePropertyName $kv[0] -NotePropertyValue $kv[1] }
    }
    [IO.File]::WriteAllText($f, ($o | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
    Write-SigSidecar $FullBakeId $Sig
}
function Get-GitHeadState([string]$Repo) { $h = (& git -C $Repo rev-parse HEAD 2>$null); $d = @(& git -C $Repo status --porcelain --untracked-files=all -- Assets/Scripts 2>$null).Count -gt 0; @{ commit = "$h".Trim(); dirty = $d } }
# Base signature of a build: sidecar lists if present and hashes agree, else rebuilt from baseCommit (git archive), else (legacy build, no fields) from the newest commit before the build time (marked legacy)
function Get-BaseSig([string]$Repo, $Info, [string]$BuildName) {
    $legacy = $false; $commit = [string]$Info.baseCommit; $fid = [string]$Info.fullBakeId
    if ($fid) { $sc = Join-Path $script:SigProject "TestOut\buildsig\$fid.json"
        if (Test-Path $sc) { $j = Get-Content $sc -Raw | ConvertFrom-Json; $sg = [pscustomobject]@{ files = @($j.files); ser = @($j.ser); boot = @($j.boot) }; $sg | Add-Member serializedSig (Get-SigHash $sg.ser); $sg | Add-Member bootSig (Get-SigHash $sg.boot); $sg | Add-Member scriptFilesSig (Get-SigHash $sg.files)
            if ($sg.serializedSig -eq $Info.serializedSig -and $sg.bootSig -eq $Info.bootSig) { return [pscustomobject]@{ sig = $sg; how = "sidecar $fid"; legacy = $false; commit = $commit } } } }
    if (-not $commit) { $t = if ($Info.realBuildTime) { $Info.realBuildTime } else { $Info.time }; if (-not $t) { throw "base build $BuildName has no baseCommit and no time" }; $commit = (& git -C $Repo rev-list -1 --before="$t" HEAD 2>$null); $legacy = $true }
    if (-not $commit) { throw "no commit found for base build $BuildName" }
    $sg = Get-BuildSigFromCommit $Repo $commit
    return [pscustomobject]@{ sig = $sg; how = $(if ($legacy) { "LEGACY estimate: newest commit before the build time ($commit)" } else { "git archive $commit" }); legacy = $legacy; commit = $commit }
}
function Format-SigList($a) { (($a | Select-Object -First 6) -join '; ') + $(if (@($a).Count -gt 6) { " (+$(@($a).Count - 6) more; full list in the sigdiff file)" } else { '' }) }
function Compare-BuildSig($Base, $Cur) {
    $d = @{}
    $d.serAdded = @($Cur.ser | Where-Object { $Base.ser -cnotcontains $_ }); $d.serRemoved = @($Base.ser | Where-Object { $Cur.ser -cnotcontains $_ })
    $d.filesRemoved = @($Base.files | Where-Object { $Cur.files -cnotcontains $_ }); $d.filesAdded = @($Cur.files | Where-Object { $Base.files -cnotcontains $_ })
    $d.bootAdded = @($Cur.boot | Where-Object { $Base.boot -cnotcontains $_ }); $d.bootRemoved = @($Base.boot | Where-Object { $Cur.boot -cnotcontains $_ })
    $r = @()
    if ($d.serAdded.Count + $d.serRemoved.Count -gt 0) { $r += "serialized layout changed (+$($d.serAdded.Count) / -$($d.serRemoved.Count)): " + (Format-SigList (@($d.serRemoved | ForEach-Object { "removed $_" }) + @($d.serAdded | ForEach-Object { "added $_" }))) }
    if ($d.filesRemoved.Count -gt 0) { $r += "removed/renamed script file(s) ($($d.filesRemoved.Count)): " + (Format-SigList $d.filesRemoved) }
    if ($d.bootAdded.Count -gt 0) { $r += "boot-hook set grew (the player startup table of the full bake omits them): " + (Format-SigList $d.bootAdded) }
    $notes = @(); if ($d.filesAdded.Count -gt 0) { $notes += "new script file(s) ($($d.filesAdded.Count)): " + (Format-SigList $d.filesAdded) }; if ($d.bootRemoved.Count -gt 0) { $notes += "boot hook(s) removed (stale table entries): " + (Format-SigList $d.bootRemoved) }
    [pscustomobject]@{ refuse = ($r.Count -gt 0); reasons = $r; notes = $notes; detail = $d }
}
# One call for the guard and build_compileonly. BaseInfoFile/TreeRoot/BaseName are for fixtures; default = live build (PLAY.bat) vs the project tree.
function Test-CodeOnlySig([string]$Repo = $script:SigProject, [string]$TreeRoot = '', [string]$BaseInfoFile = '', [string]$BaseName = '') {
    if (-not $TreeRoot) { $TreeRoot = $Repo }
    if (-not $BaseInfoFile) {
        if (-not $BaseName) { $BaseName = ([regex]::Match((Get-Content (Join-Path $Repo 'Builds\PLAY.bat') -Raw), 'HDRP_\w+')).Value }
        $BaseInfoFile = Join-Path $Repo "Builds\$BaseName\build_info.json"
    }
    $info = Get-Content $BaseInfoFile -Raw | ConvertFrom-Json
    $b = Get-BaseSig $Repo $info $BaseName; $cur = Get-BuildSig $TreeRoot; $c = Compare-BuildSig $b.sig $cur
    $c | Add-Member baseName $BaseName; $c | Add-Member baseHow $b.how; $c | Add-Member legacy $b.legacy; $c | Add-Member baseCommit $b.commit; $c | Add-Member cur $cur; $c | Add-Member baseSig $b.sig
    $c | Add-Member fullBakeId ([string]$info.fullBakeId); return $c
}
