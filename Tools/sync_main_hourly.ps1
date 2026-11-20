# sync_main_hourly.ps1: commit everything dirty in the repo and push main to GitHub (Dom's standing order, 20 Nov 2026: "commit + push, live sync clean no dirt, every hour +- 10 min, if the machine is busy try again").
# Runs from Task Scheduler hourly (random delay up to 10 min). Retries every 2 min for up to 25 min when git is busy (index.lock, a push/backup running, a transient network error). Never force-pushes, never rewrites history, never touches ignored files.
# Posts one board line only when it committed or pushed something, or when it gave up.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'
$RepoDir = 'C:\Users\dom12\Documents\unity_test_hdrp'
$BoardFile = Join-Path $RepoDir '_agents\COORDINATION.md'
$LogFile = Join-Path $RepoDir '_agents\laneLG\sync_main_hourly.log'
function Write-SyncLog([string]$Text) { Add-Content -LiteralPath $LogFile -Value ("{0} {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Text) }
function Write-SyncBoard([string]$Text) { Add-Content -LiteralPath $BoardFile -Value ("`n{0} Frankendom sync-main: {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm'), $Text) }
Set-Location -LiteralPath $RepoDir
$deadline = (Get-Date).AddMinutes(25); $attempt = 0; $done = $false; $lastErr = ''
while (-not $done -and (Get-Date) -lt $deadline) {
  $attempt++
  try {
    if (Test-Path -LiteralPath (Join-Path $RepoDir '.git\index.lock')) { $lastErr = 'index.lock present (git busy)'; throw $lastErr }
    if ((git branch --show-current) -ne 'main') { $lastErr = 'not on main: refusing'; Write-SyncLog $lastErr; break }
    $dirtyCount = @(git status --porcelain 2>$null).Count
    $committed = ''
    if ($dirtyCount -gt 0) {
      git add -A 2>$null | Out-Null
      $msg = "FK hourly sync $(Get-Date -Format 'yyyy-MM-dd HH:mm'): $dirtyCount changed paths (data/code/board)`n`nCo-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
      git commit -q -m $msg 2>&1 | Out-Null
      if ($LASTEXITCODE -ne 0) { $lastErr = "commit failed (exit $LASTEXITCODE)"; throw $lastErr }
      $committed = (git rev-parse --short HEAD)
    }
    git fetch -q origin 2>&1 | Out-Null
    $counts = (git rev-list --left-right --count 'HEAD...origin/main') -split '\s+'
    $ahead = [int]$counts[0]; $behind = [int]$counts[1]
    if ($behind -gt 0) { $lastErr = "origin/main is $behind commit(s) ahead of us: not pushing (no force, no rebase); needs a human look"; Write-SyncLog $lastErr; Write-SyncBoard $lastErr; $done = $true; break }
    if ($ahead -gt 0) {
      git push origin main 2>&1 | Out-Null
      if ($LASTEXITCODE -ne 0) { $lastErr = "push failed (exit $LASTEXITCODE)"; throw $lastErr }
    }
    $afterDirty = @(git status --porcelain 2>$null).Count
    Write-SyncLog "ok attempt $attempt committed='$committed' pushed_commits=$ahead dirty_after=$afterDirty HEAD=$(git rev-parse --short HEAD)"
    if ($committed -or $ahead -gt 0) { Write-SyncBoard "committed '$committed', pushed $ahead commit(s) to origin/main, HEAD $(git rev-parse --short HEAD), working tree dirty entries now $afterDirty" }
    $done = $true
  } catch {
    Write-SyncLog "attempt $attempt failed: $lastErr $($_.Exception.Message)"
    Start-Sleep -Seconds 120
  }
}
if (-not $done) { Write-SyncLog "GAVE UP after $attempt attempts: $lastErr"; Write-SyncBoard "GAVE UP after $attempt attempts over 25 min: $lastErr (will retry next hour)" }
