# Frankendom hourly backup (COO 19 Nov 2026, Dom: "make sure the commit and deploys, computer and github live sync clean are all on for 3 devs, every 1 hour").
# Same design as FrontierDom's tools\backup_hourly.ps1: snapshots the WHOLE working tree (everything not git-ignored, so uncommitted work too) into a commit on the side branch
# backup-hourly of origin, using a TEMPORARY index. The real index, the current branch and the working tree are never touched, so it cannot disturb a Lead's commit.
# Files over 90 MB that are not LFS are left out (GitHub limit). Skips while this project's Unity Editor runs (hashing mid-write = IOException 1224) or a git command holds index.lock.
# Log: Logs\backup_hourly.log. Restore: git fetch origin backup-hourly; git checkout origin/backup-hourly -- <path>
$ErrorActionPreference = 'Continue'
$P = 'C:\Users\dom12\Documents\unity_test_hdrp'; Set-Location $P
$log = "$P\Logs\backup_hourly.log"; New-Item -ItemType Directory -Force "$P\Logs" | Out-Null
function L($m) { Add-Content $log ("{0} {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $m) }
try { $busy = Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" -ErrorAction Stop | Where-Object { $_.CommandLine -match 'unity_test_hdrp' } } catch { $busy = $true }
if ($busy) { L "SKIP: this project's Unity Editor is running"; exit 0 }
if (Test-Path "$P\.git\index.lock") { L "SKIP: .git\index.lock exists (a git command is running)"; exit 0 }
$tmp = "$env:TEMP\fk_backup_index"; Remove-Item $tmp -ErrorAction SilentlyContinue; $env:GIT_INDEX_FILE = $tmp
try {
  git read-tree HEAD 2>&1 | Out-Null
  git add -A 2>&1 | Out-Null
  $big = @(git diff --cached --name-only --diff-filter=AM | Where-Object { (Test-Path -LiteralPath $_ -PathType Leaf) -and ((Get-Item -LiteralPath $_).Length -gt 90MB) -and -not ((git check-attr filter -- $_) -match ': filter: lfs$') })
  foreach ($b in $big) { git rm --cached -q -- $b 2>&1 | Out-Null; L "left out (>90 MB, not LFS): $b" }
  $tree = (git write-tree).Trim()
  git fetch -q origin backup-hourly 2>&1 | Out-Null
  $parent = (git rev-parse -q --verify refs/remotes/origin/backup-hourly 2>$null); if (-not $parent) { $parent = (git rev-parse HEAD).Trim() }
  $ptree = (git rev-parse "$parent^{tree}" 2>$null)
  if ($ptree -eq $tree) { L "no change since last backup ($($tree.Substring(0,8)))"; exit 0 }
  $c = (git commit-tree $tree -p $parent -m ("hourly backup {0} (working tree snapshot; branch main at {1})" -f (Get-Date -Format 'yyyy-MM-dd HH:mm'), (git rev-parse --short HEAD))).Trim()
  # COO 20 Nov 14:1x: two pushes failed on 'Could not resolve host: github.com' (13:47, 14:06) while the name resolved fine a minute later. Retry up to 4 times, 20 s apart.
  $pushExit = 1; for ($try = 1; $try -le 4 -and $pushExit -ne 0; $try++) { if ($try -gt 1) { Start-Sleep -Seconds 20 }; git push -q origin "${c}:refs/heads/backup-hourly" 2>&1 | ForEach-Object { L "push (try $try): $_" }; $pushExit = $LASTEXITCODE }
  $global:LASTEXITCODE = $pushExit
  if ($pushExit -ne 0) { L "PUSH FAILED (exit $LASTEXITCODE), snapshot $($c.Substring(0,8)) is local only"; exit 1 }
  L ("backup {0} pushed (tree {1}, {2} files left out)" -f $c.Substring(0,8), $tree.Substring(0,8), $big.Count)
} finally { Remove-Item Env:\GIT_INDEX_FILE -ErrorAction SilentlyContinue; Remove-Item $tmp -ErrorAction SilentlyContinue }
