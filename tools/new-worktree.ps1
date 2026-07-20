<#
.SYNOPSIS
  One-shot helper to spin up a parallel agent worktree for BYH.

.DESCRIPTION
  Creates a git worktree at C:\dvr\byh-worktrees\<branch-slug>\ off a new
  branch from main, warms it up with a Debug build, and prints the path to
  hand to the next agent. Idempotent guard: refuses to clobber an existing
  worktree or branch.

  See docs/git-workflow.md for the full workflow.

.PARAMETER Branch
  The task branch to create, e.g. 'task/REQ-010-qr-recognize'.
  The worktree dir is derived from the last path segment.

.PARAMETER Base
  Base branch to branch from. Defaults to 'main'.

.PARAMETER NoBuild
  Skip the warm-up Debug build. Useful when you only need to inspect files.

.EXAMPLE
  pwsh tools/new-worktree.ps1 task/REQ-010-qr-recognize
  pwsh tools/new-worktree.ps1 task/REQ-011-number-annotate -NoBuild
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Branch,
    [string]$Base = 'main',
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'

# --- Resolve paths ----------------------------------------------------------
$RepoRoot   = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path   # .../selection-assistant
$WtParent   = 'C:\dvr\byh-worktrees'                                # sibling of gh-kb\
$Slug       = ($Branch -split '/')[-1]                              # last segment
$WtPath     = Join-Path $WtParent $Slug

Write-Host "Repo root : $RepoRoot"
Write-Host "Branch    : $Branch (from $Base)"
Write-Host "Worktree  : $WtPath"
Write-Host ''

# --- Guards -----------------------------------------------------------------
if (-not (Test-Path (Join-Path $RepoRoot '.git'))) {
    throw "Not a git repository: $RepoRoot"
}

# Make sure we run git against the main repo regardless of cwd
$GitDir = "-C `"$RepoRoot`""

if (Test-Path $WtPath) {
    throw "Worktree dir already exists: $WtPath`n  Remove it first or pick another branch."
}

# Check branch does not already exist
$existingBranch = & git $GitDir.Split(' ') branch --list $Branch 2>$null
if ($LASTEXITCODE -eq 0 -and $existingBranch) {
    throw "Branch '$Branch' already exists.`n  Use:  git $GitDir worktree add $WtPath $Branch"
}

# --- Create worktree --------------------------------------------------------
Write-Host "[1/3] Creating worktree + branch..." -ForegroundColor Cyan
& git -C $RepoRoot worktree add -b $Branch $WtPath $Base
if ($LASTEXITCODE -ne 0) { throw "git worktree add failed (exit $LASTEXITCODE)" }

# --- Warm up build ----------------------------------------------------------
if ($NoBuild) {
    Write-Host "[2/3] Skipping build (-NoBuild)" -ForegroundColor DarkGray
} else {
    Write-Host "[2/3] Warming up Debug build (this may take a minute)..." -ForegroundColor Cyan
    & dotnet build (Join-Path $WtPath 'SelectionAssistant.slnx') -c Debug --nologo 2>&1 |
        Select-Object -Last 5 | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkGray }
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Build failed (exit $LASTEXITCODE). Worktree is still usable; fix errors then rebuild."
    }
}

# --- Done -------------------------------------------------------------------
Write-Host "[3/3] Ready." -ForegroundColor Green
Write-Host ''
Write-Host "Worktree path (hand this to the agent):" -ForegroundColor Yellow
Write-Host "  $WtPath" -ForegroundColor Yellow
Write-Host ''
Write-Host 'To merge back into main when done:' -ForegroundColor DarkGray
Write-Host "  cd $RepoRoot" -ForegroundColor DarkGray
Write-Host "  git merge --no-ff $Branch" -ForegroundColor DarkGray
Write-Host "  git worktree remove `"$WtPath`"" -ForegroundColor DarkGray
Write-Host "  git branch -d $Branch" -ForegroundColor DarkGray
