# _tag_reserve.ps1: atomic build-tag reservation (Strategy 22:10 16 Nov: tag collisions pn/pv repeat fault; the mechanism, not a board rule).
# Reserve-Tag  : creates Builds\HDRP_v109<tag>.reserved with FileMode.CreateNew (atomic). Refuses if the tag is reserved by another owner or the build folder exists (unless the same owner reserved it).
# Get-NextTag  : next free two-letter tag after the highest of Builds\HDRP_v109??, *.reserved and tags named on the board.
# Nobody types a tag by hand: powershell -File Tools\next_tag.ps1 -Owner <name> prints and reserves the tag. deploy_data.ps1 and Assert-BuildSafe reserve too (same owner may reuse its own tag).
function Get-TagOwnerDefault { if ($env:FK_OWNER) { $env:FK_OWNER } else { 'anon' } }
function Get-NextTag {
    param([string]$Root = 'C:\Users\dom12\Documents\unity_test_hdrp', [string]$Prefix = 'p')
    $tags = @()
    $tags += Get-ChildItem "$Root\Builds" -Filter "HDRP_v109$Prefix?*" -ErrorAction SilentlyContinue | ForEach-Object { if ($_.Name -match "^HDRP_v109($Prefix[a-z])(\.reserved)?$") { $Matches[1] } }
    $b = "$Root\_agents\COORDINATION.md"
    if (Test-Path $b) { $tags += [regex]::Matches(((Get-Content $b -Tail 300) -join "`n"), "HDRP_v109($Prefix[a-z])\b") | ForEach-Object { $_.Groups[1].Value } }
    $max = ($tags | Sort-Object -Unique | Select-Object -Last 1); if (-not $max) { return "${Prefix}a" }
    $c = [char]([int][char]$max[1] + 1); if ($c -gt [char]'z') { throw "tag space $Prefix? exhausted, pick a new prefix" }
    return "$Prefix$c"
}
function Reserve-Tag {
    param([Parameter(Mandatory)][string]$Tag, [string]$Owner = (Get-TagOwnerDefault), [string]$Root = 'C:\Users\dom12\Documents\unity_test_hdrp')
    $Tag = $Tag -replace '^HDRP_', '' -replace '^v109', ''
    $res = "$Root\Builds\HDRP_v109$Tag.reserved"; $dir = "$Root\Builds\HDRP_v109$Tag"
    $mine = $false
    try {
        $fs = [System.IO.File]::Open($res, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write)
        $bytes = [Text.Encoding]::UTF8.GetBytes("owner=$Owner time=$(Get-Date -Format s) pid=$PID"); $fs.Write($bytes, 0, $bytes.Length); $fs.Close(); $mine = $true
    } catch [System.IO.IOException] {
        $cur = Get-Content $res -Raw -ErrorAction SilentlyContinue
        if ($cur -match "owner=$([regex]::Escape($Owner))\b") { $mine = $true } else { throw "TAG $Tag RESERVED by another owner ($cur): pick a tag with Tools\next_tag.ps1" }
    }
    if ((Test-Path $dir) -and -not (Test-Path "$res.used")) { }
    return $mine
}

# QA tags (tooling 18 Nov): QA copies are 109k + 2 letters (676 slots), reserved ONLY via next_tag.ps1 -Kind qa. The owner must be unique per run (gateproof:<pid>) because Reserve-Tag lets the same owner reuse a tag.
function Clear-StaleQaReservations {
    param([string]$Root = 'C:\Users\dom12\Documents\unity_test_hdrp', [int]$MaxAgeHours = 6, [switch]$ListOnly)
    $out = @()
    foreach ($f in @(Get-ChildItem "$Root\Builds" -Filter 'HDRP_v109k*.reserved' -File -ErrorAction SilentlyContinue)) {
        if ($f.Name -notmatch '^HDRP_v109k[a-z]{0,2}\.reserved$') { continue }
        if (((Get-Date) - $f.LastWriteTime).TotalHours -le $MaxAgeHours) { continue }
        $c = Get-Content $f.FullName -Raw -ErrorAction SilentlyContinue
        $op = if ($c -match 'pid=(\d+)') { [int]$Matches[1] } else { 0 }
        $pr = if ($op -gt 0) { Get-Process -Id $op -ErrorAction SilentlyContinue } else { $null }
        if ($pr -and $pr.StartTime -le $f.LastWriteTime.AddMinutes(1)) { continue }   # owner pid alive AND started before the file (a pid reused later by another process does not count)
        $out += $f.Name
        if (-not $ListOnly) { Remove-Item $f.FullName -Force -ErrorAction SilentlyContinue }
    }
    return $out
}
function Get-FreeQaTag {
    param([string]$Root = 'C:\Users\dom12\Documents\unity_test_hdrp')
    $used = @{}
    foreach ($f in @(Get-ChildItem "$Root\Builds" -Filter 'HDRP_v109k*' -ErrorAction SilentlyContinue)) { if ($f.Name -match '^HDRP_v109(k[a-z]{1,2})(\.reserved)?$') { $used[$Matches[1]] = $true } }
    $b = "$Root\_agents\COORDINATION.md"
    if (Test-Path $b) { foreach ($m in [regex]::Matches(((Get-Content $b -Tail 400) -join "`n"), 'HDRP_v109(k[a-z]{1,2})\b')) { $used[$m.Groups[1].Value] = $true } }
    $cands = @(); foreach ($x in 97..122) { foreach ($y in 97..122) { $t = 'k' + [char]$x + [char]$y; if (-not $used[$t]) { $cands += $t } } }
    if (-not $cands) { throw 'QA tag space k?? exhausted' }
    $cands | Get-Random -Count ([math]::Min(8, $cands.Count))
}


# TAGUNIQ v1 (Lead 19 Nov, COO ruling: a tag is never reused): a RELEASE tag is TAKEN if ANY trace exists: a Builds folder, a .reserved file, a TestOut\v109<tag>* file, or a board line naming v109<tag>. 2-letter tags first (a-z except k, which QA owns), then 3-letter.
function Test-TagTaken([string]$Tag, [string]$Root = 'C:\Users\dom12\Documents\unity_test_hdrp') {
    if (Test-Path "$Root\Builds\HDRP_v109$Tag") { return $true }
    if (Test-Path "$Root\Builds\HDRP_v109$Tag.reserved") { return $true }
    if (@(Get-ChildItem "$Root\TestOut" -Filter "v109$Tag*" -ErrorAction SilentlyContinue | Where-Object { $_.Name -match "^v109$Tag(_|\.|$)" }).Count -gt 0) { return $true }
    $b = "$Root\_agents\COORDINATION.md"; if (-not $script:boardTxt) { $script:boardTxt = if (Test-Path $b) { Get-Content $b -Raw } else { '' } }
    if ($script:boardTxt -match "v109$Tag\b") { return $true }
    return $false
}
function Get-NextFreeTag([string]$Root = 'C:\Users\dom12\Documents\unity_test_hdrp') {
    $script:boardTxt = $null; $A = @(97..122 | ForEach-Object { [string][char]$_ }); $L = @($A | Where-Object { $_ -ne 'k' })
    foreach ($x in $L) { foreach ($y in $A) { $tag = "$x$y"; if (-not (Test-TagTaken $tag $Root)) { return $tag } } }
    foreach ($x in $L) { foreach ($y in $A) { foreach ($z in $A) { $tag = "$x$y$z"; if (-not (Test-TagTaken $tag $Root)) { return $tag } } } }
    throw 'tag space exhausted (3 letters)'
}