[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BranchName,

    [string]$CommitMessage = "",
    [string]$RemoteName = "origin",
    [switch]$CreateBranch = $true,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

function Test-GitRepo {
    git rev-parse --is-inside-work-tree *> $null
    return $LASTEXITCODE -eq 0
}

if (-not (Test-GitRepo)) {
    throw "Current directory is not inside a Git repository. Run this script from the repo root."
}

$currentBranch = (git rev-parse --abbrev-ref HEAD).Trim()
Write-Host "Current branch: $currentBranch" -ForegroundColor Cyan

# Check for uncommitted changes
$status = git status --porcelain
if ([string]::IsNullOrWhiteSpace($status)) {
    Write-Host "No changes detected in working tree." -ForegroundColor Yellow
}
else {
    if ([string]::IsNullOrWhiteSpace($CommitMessage)) {
        $CommitMessage = Read-Host "Enter commit message"
    }

    if ([string]::IsNullOrWhiteSpace($CommitMessage)) {
        throw "Commit message is required when there are changes to commit."
    }

    Write-Host "==> Staging all changes..." -ForegroundColor Cyan
    git add -A

    Write-Host "==> Committing..." -ForegroundColor Cyan
    git commit -m "$CommitMessage"
}

# Check if target branch already exists locally
$branchExistsLocally = git branch --list $BranchName
$branchExistsRemotely = git ls-remote --heads $RemoteName $BranchName

if ([string]::IsNullOrWhiteSpace($branchExistsLocally)) {
    if ($CreateBranch) {
        Write-Host "==> Creating new local branch '$BranchName'..." -ForegroundColor Cyan
        git checkout -b $BranchName
    }
    else {
        throw "Branch '$BranchName' does not exist locally. Use -CreateBranch to create it."
    }
}
else {
    Write-Host "==> Switching to existing local branch '$BranchName'..." -ForegroundColor Cyan
    git checkout $BranchName
}

Write-Host "==> Pushing to '$RemoteName/$BranchName'..." -ForegroundColor Cyan

if ($Force) {
    git push --force-with-lease $RemoteName $BranchName
}
elseif ([string]::IsNullOrWhiteSpace($branchExistsRemotely)) {
    git push -u $RemoteName $BranchName
}
else {
    git push $RemoteName $BranchName
}

Write-Host "==> Done. Pushed to $RemoteName/$BranchName." -ForegroundColor Green

if ($currentBranch -ne $BranchName) {
    Write-Host "Note: you are now on branch '$BranchName' (switched from '$currentBranch')." -ForegroundColor Yellow
}