# next_tag.ps1 -Owner <name> [-Kind build|qa] : prints the next free tag and reserves it atomically (retries on a race). Use this tag for your build/deploy, never type one. Rolls to the next prefix letter when p? is exhausted (pz -> qa).
# -Kind qa (tooling 18 Nov): reserves a free QA-copy tag (k + 2 letters, picked among free slots at random so concurrent callers rarely meet) after clearing stale k* reservations (owner process dead, over 6 h). A QA tag is refused if anyone holds it. gateproof.ps1 uses this; do not compute k-tags.
param([string]$Owner = 'anon', [string]$Prefix = 'a', [ValidateSet('build','qa')][string]$Kind = 'build', [switch]$Preview)
. "$PSScriptRoot\_tag_reserve.ps1"
if ($Kind -eq 'qa') {
  if (-not $Preview) { $null = Clear-StaleQaReservations }
  for ($i = 0; $i -lt 12; $i++) {
    foreach ($t in @(Get-FreeQaTag)) {
      if ($Preview) { $t; exit 0 }
      $res = "$PSScriptRoot\..\Builds\HDRP_v109$t.reserved"
      if (Test-Path $res) { continue }   # never reuse a tag anyone holds, even the same owner
      try { $null = Reserve-Tag -Tag $t -Owner $Owner; $t; exit 0 } catch { continue }
    }
    Start-Sleep -Milliseconds (60 * ($i + 1))
  }
  throw 'could not reserve a QA tag'
}
for ($i = 0; $i -lt 6; $i++) {
  $t = $null
  $t = Get-NextFreeTag   # TAGUNIQ v1: never reuse a tag with any trace (folder, .reserved, TestOut file, board line); 2 letters then 3
  if (-not $t) { throw 'tag space exhausted' }
  try { $null = Reserve-Tag -Tag $t -Owner $Owner; $t; exit 0 } catch { Start-Sleep -Milliseconds (80 * ($i + 1)) }
}
throw 'could not reserve a tag'


