<#
.SYNOPSIS
    Commits any local changes and pushes them, safely enough to run unattended.

.DESCRIPTION
    Written to be run automatically, which means the interesting part is everything it refuses to do.
    An unattended commit that fires at the wrong moment captures a half-written file, or a
    non-compiling tree, or fights with a rebase already in progress. So it bails out rather than
    guessing whenever the repository is in a state a human would want to look at first.

    Specifically it stops, without committing, when:

      * a merge, rebase, cherry-pick or bisect is in progress;
      * the working tree has been touched within -QuietSeconds, which is the guard against
        committing a file someone is still typing into;
      * nothing has actually changed.

    And it tolerates rather than fails on:

      * files locked by another process -- this repository is synced by both OneDrive and GoodSync,
        which hold files open often enough that an all-or-nothing 'git add' would frequently abort.
        Locked paths are skipped and named, so a change is never silently dropped;
      * a remote that has moved on, by rebasing onto it before pushing;
      * having no upstream or no network, in which case the commit stands and the push is retried
        next time.

.PARAMETER QuietSeconds
    How long the working tree must have been untouched. The point is to avoid committing a file
    mid-save; a few seconds is enough for an editor, longer suits a background timer.

.PARAMETER RequireBuild
    Build before committing and abort if it fails. Off by default because it costs tens of seconds,
    which is too slow for something that fires after every change -- but worth turning on for a
    scheduled run.

.PARAMETER WhatIf
    Report what would happen and change nothing.

.EXAMPLE
    scripts\auto-commit.ps1
    scripts\auto-commit.ps1 -QuietSeconds 120 -RequireBuild
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [int] $QuietSeconds = 5,
    [switch] $RequireBuild,
    [string] $Root = (Join-Path $PSScriptRoot '..')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Root = (Resolve-Path $Root).Path
Push-Location $Root
try {
    # ---------------------------------------------------------------- Preconditions -------------
    $gitDir = Join-Path $Root '.git'
    if (-not (Test-Path $gitDir)) { throw "$Root is not a git repository." }

    # An operation in progress means someone is partway through resolving something. Committing on
    # top of that would make a confusing situation worse.
    $inProgress = @(
        @{ Path = 'MERGE_HEAD';        What = 'a merge' }
        @{ Path = 'rebase-merge';      What = 'a rebase' }
        @{ Path = 'rebase-apply';      What = 'a rebase' }
        @{ Path = 'CHERRY_PICK_HEAD';  What = 'a cherry-pick' }
        @{ Path = 'BISECT_LOG';        What = 'a bisect' }
    ) | Where-Object { Test-Path (Join-Path $gitDir $_.Path) } | Select-Object -First 1

    if ($inProgress) {
        Write-Host "Skipping: $($inProgress.What) is in progress. Finish it first."
        return
    }

    $status = @(git status --porcelain 2>$null)
    if ($status.Count -eq 0) {
        Write-Host 'Nothing to commit.'
        # Still worth pushing: a previous run may have committed and failed to push.
        $ahead = (git rev-list --count '@{upstream}..HEAD' 2>$null)
        if ($LASTEXITCODE -eq 0 -and [int]$ahead -gt 0) {
            Write-Host "$ahead commit(s) not yet pushed; pushing."
        } else {
            return
        }
    }

    # ---------------------------------------------------------------- Quiet period --------------
    if ($status.Count -gt 0 -and $QuietSeconds -gt 0) {
        # Newest write across everything git considers changed. Paths come from porcelain output,
        # which quotes anything unusual, so the quoting is undone before use.
        $newest = [datetime]::MinValue
        foreach ($line in $status) {
            $path = $line.Substring(3).Trim('"')
            # A rename reports "old -> new"; the new name is the one on disk.
            if ($path -match ' -> ') { $path = ($path -split ' -> ')[-1].Trim('"') }
            $full = Join-Path $Root $path
            if (Test-Path $full -PathType Leaf) {
                $written = (Get-Item $full -Force).LastWriteTime
                if ($written -gt $newest) { $newest = $written }
            }
        }

        if ($newest -ne [datetime]::MinValue) {
            $idle = ((Get-Date) - $newest).TotalSeconds
            if ($idle -lt $QuietSeconds) {
                Write-Host ("Skipping: something was written {0:N0}s ago, waiting for {1}s of quiet." -f $idle, $QuietSeconds)
                return
            }
        }
    }

    # ---------------------------------------------------------------- Optional build ------------
    if ($RequireBuild) {
        Write-Host 'Building before commit...'
        dotnet build (Join-Path $Root 'CloudDrive.slnx') --nologo --verbosity quiet | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-Warning 'The build failed, so nothing was committed.'
            return
        }
    }

    # ---------------------------------------------------------------- Stage ---------------------
    $skipped = @()
    if ($status.Count -gt 0) {
        git add -A 2>$null
        if ($LASTEXITCODE -ne 0) {
            # Almost always one locked file. Add what can be added and name what cannot, so a
            # skipped change is visible rather than silently missing from the commit.
            Write-Host 'A bulk add failed; adding file by file.'
            foreach ($line in $status) {
                $path = $line.Substring(3).Trim('"')
                if ($path -match ' -> ') { $path = ($path -split ' -> ')[-1].Trim('"') }
                git add -- $path 2>$null
                if ($LASTEXITCODE -ne 0) { $skipped += $path }
            }
        }
    }

    foreach ($path in $skipped) { Write-Warning "Left out (locked or unreadable): $path" }

    $staged = @(git diff --cached --name-only)
    if ($staged.Count -eq 0 -and $status.Count -gt 0) {
        Write-Warning 'Nothing could be staged.'
        return
    }

    # ---------------------------------------------------------------- Commit --------------------
    if ($staged.Count -gt 0) {
        # A summary beats "wip": the file list is what makes an automatic commit reviewable later.
        $summary = if ($staged.Count -le 3) {
            ($staged | ForEach-Object { Split-Path $_ -Leaf }) -join ', '
        } else {
            "$($staged.Count) files"
        }

        $body = @()
        $body += "Automatic commit: $summary"
        $body += ''
        $body += 'Committed by scripts\auto-commit.ps1. Files:'
        $body += ($staged | ForEach-Object { "  $_" })
        if ($skipped.Count -gt 0) {
            $body += ''
            $body += 'Left out because they were locked by another process:'
            $body += ($skipped | ForEach-Object { "  $_" })
        }

        $message = $body -join "`n"
        if ($PSCmdlet.ShouldProcess($summary, 'commit')) {
            # autocrlf off so a line-ending rewrite does not turn every file into a diff.
            git -c core.autocrlf=false commit -q -m $message
            if ($LASTEXITCODE -ne 0) { Write-Warning 'The commit failed.'; return }
            Write-Host "Committed: $summary"
        }
    }

    # ---------------------------------------------------------------- Push ----------------------
    $branch = (git rev-parse --abbrev-ref HEAD).Trim()

    git rev-parse --abbrev-ref '@{upstream}' *> $null
    $hasUpstream = $LASTEXITCODE -eq 0

    if (-not $PSCmdlet.ShouldProcess($branch, 'push')) { return }

    if (-not $hasUpstream) {
        # A branch can push perfectly well with no upstream configured -- push.default matches it by
        # name -- so refusing here would have declined to push a branch that works. Set the upstream
        # while pushing, which also makes the ahead/behind checks above meaningful next time.
        Write-Host "No upstream for '$branch'; setting one while pushing."
        git push -u origin HEAD --quiet 2>$null
        if ($LASTEXITCODE -eq 0) { Write-Host "Pushed to $branch." }
        else { Write-Warning 'The push failed. The commit is safe locally.' }
        return
    }

    # Rebase onto the remote first. A plain push against a moved branch just fails, and this script
    # exists precisely so nobody has to notice and fix that by hand.
    git fetch --quiet 2>$null
    $behind = (git rev-list --count 'HEAD..@{upstream}' 2>$null)
    if ($LASTEXITCODE -eq 0 -and [int]$behind -gt 0) {
        Write-Host "$behind commit(s) upstream; rebasing."
        git rebase --quiet '@{upstream}' 2>$null
        if ($LASTEXITCODE -ne 0) {
            git rebase --abort 2>$null
            Write-Warning 'The rebase conflicted and was aborted. Resolve it by hand; the commit is safe locally.'
            return
        }
    }

    git push --quiet 2>$null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Pushed to $branch."
    } else {
        Write-Warning 'The push failed. The commit is safe locally and will be pushed next time.'
    }
} finally {
    Pop-Location
}
