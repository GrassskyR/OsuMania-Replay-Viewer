[CmdletBinding()]
param([string]$ExpectedRevision = '')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../build-version.ps1')
if (-not $env:RELEASE_TAG -or -not $env:RELEASE_TAG.StartsWith('v')) { throw 'Release tags must start with v, for example v3.1.0.' }
$version = $env:RELEASE_TAG.Substring(1)
Assert-BuildVersion $version
$tagRef = "refs/tags/$env:RELEASE_TAG"
git check-ref-format $tagRef
if ($LASTEXITCODE -ne 0) { throw "Invalid release tag: $env:RELEASE_TAG" }
git show-ref --verify --quiet $tagRef
if ($LASTEXITCODE -eq 0) {
    $revision = git rev-parse --verify "$tagRef^{commit}"
} elseif ($LASTEXITCODE -eq 1 -and $env:GITHUB_EVENT_NAME -eq 'workflow_dispatch') {
    $revision = git rev-parse --verify HEAD
    Write-Host "New tag $env:RELEASE_TAG will be created only after validation succeeds."
} else { throw "Cannot resolve release tag: $env:RELEASE_TAG" }
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve the release commit.' }
if ($ExpectedRevision -and $revision -ne $ExpectedRevision) { throw 'Release tag moved while validation was running. Refusing to publish a different commit.' }
Write-Host "Release: $env:RELEASE_TAG -> $revision (application version $version)"
if ($env:GITHUB_OUTPUT) {
    @("sha=$revision", "version=$version", "prerelease=$($version.Contains('-').ToString().ToLowerInvariant())") |
        Add-Content -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8
}
