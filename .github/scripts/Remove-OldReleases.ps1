#Requires -Version 7
<#
.SYNOPSIS
  Keep the newest GitHub Releases and delete the rest. Git tags are always kept.
.DESCRIPTION
  Cross-repo release policy: the Releases page keeps the three most recent releases, and
  every release tag stays on the remote, so an older build is rebuilt from its tag.
  Nothing is deleted unless -Apply is passed; without it the script prints what it would
  delete. release.yml runs it with -Apply after the publish step, so the release that was
  just published counts as one of the kept three.

  Best-effort: it always exits 0, because a prune problem must not fail a release that has
  already published. It prints on every path, including the empty one, because a prune
  that quietly deletes nothing looks exactly like a prune that never ran.
.PARAMETER ReleasesJson
  Test hook: read the release list from this file (the output of
  gh release list --json tagName,createdAt) instead of asking GitHub.
#>
param(
    [Parameter(Mandatory)][string]$Repo,
    [ValidateRange(1, 100)][int]$Keep = 3,
    [switch]$Apply,
    [string]$ReleasesJson
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'

try {
    $text = if ($ReleasesJson) {
        Get-Content -Raw -LiteralPath $ReleasesJson -ErrorAction Stop
    } else {
        $out = gh release list --repo $Repo --limit 100 --json tagName,createdAt
        if ($LASTEXITCODE -ne 0) { throw "gh release list exited $LASTEXITCODE" }
        $out -join "`n"
    }
    $releases = @($text | ConvertFrom-Json -ErrorAction Stop)
} catch {
    Write-Host "prune: could not list the releases of ${Repo}: $($_.Exception.Message)"
    exit 0
}

$mode = if ($Apply) { 'deleting' } else { 'dry run, nothing is deleted' }
Write-Host "prune: $($releases.Count) release(s) in $Repo; keeping the $Keep most recent ($mode)."
$old = @($releases | Sort-Object { [datetime]$_.createdAt } -Descending | Select-Object -Skip $Keep)
if ($old.Count -eq 0) {
    Write-Host "prune: nothing to delete (at most $Keep releases exist)."
    exit 0
}

$deleted = 0
foreach ($r in $old) {
    $tag = [string]$r.tagName
    # The tag is the whole fallback, so a release whose tag is not on the remote stays.
    gh api "repos/$Repo/git/ref/tags/$tag" --silent 2>$null
    if ($LASTEXITCODE -ne 0) { Write-Host "prune: skip $tag, its tag is not on the remote"; continue }
    if (-not $Apply) { Write-Host "prune: would delete release $tag (tag kept)"; continue }
    # Never --cleanup-tag: deleting the tag would turn a tidy-up into data loss.
    gh release delete $tag --repo $Repo --yes
    if ($LASTEXITCODE -eq 0) { $deleted++; Write-Host "prune: deleted release $tag (tag kept)" }
    else { Write-Host "prune: could not delete release $tag (gh exit $LASTEXITCODE)" }
}
Write-Host "prune: $deleted of $($old.Count) old release(s) deleted ($mode)."
exit 0
