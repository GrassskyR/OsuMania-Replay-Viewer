# Exercise release resolution in an isolated repository; never create tags in the source checkout.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repo 'tools/build-version.ps1')
foreach ($file in @((Get-Item (Join-Path $repo 'build.ps1'))) + @(Get-ChildItem (Join-Path $repo 'tools') -Filter '*.ps1' -Recurse)) {
    $tokens = $null
    $errors = $null
    [void][Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors)
    if ($errors) { throw "$($file.FullName): $($errors.Message -join '; ')" }
}
Write-Host 'PASS PowerShell script syntax'

function Expect-Failure([scriptblock]$Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw 'Expected invalid input/state to be rejected.' }
}
foreach ($version in @('0.0.0', '3.1.0', '3.1.0-rc.1', '3.1.0-beta-test.0')) { Assert-BuildVersion $version }
foreach ($version in @('3.1', 'v3.1.0', '03.1.0', '3.1.0-01', '3.1.0-rc..1', '65535.0.0', '3.1.0+metadata', '3.1.0/unsafe')) {
    Expect-Failure { Assert-BuildVersion $version }
}
Write-Host 'PASS stable/prerelease version validation and Windows version limits'

$fixture = [IO.Path]::GetFullPath((Join-Path $repo ('out/ci-check-' + [Guid]::NewGuid().ToString('N'))))
$savedEnvironment = @{}
foreach ($name in @('RELEASE_TAG', 'GITHUB_EVENT_NAME', 'GITHUB_OUTPUT')) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name) }
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
Push-Location $fixture
try {
    git init --quiet
    git -c user.name=CI -c user.email=ci@example.invalid commit --allow-empty --quiet -m first
    if ($LASTEXITCODE -ne 0) { throw 'Could not initialize the release test fixture.' }
    $first = git rev-parse HEAD
    git tag v3.1.0
    git -c user.name=CI -c user.email=ci@example.invalid tag -a v3.1.0-rc.1 -m prerelease
    git -c user.name=CI -c user.email=ci@example.invalid commit --allow-empty --quiet -m second
    $second = git rev-parse HEAD
    $env:GITHUB_OUTPUT = Join-Path $fixture 'outputs.txt'
    $env:GITHUB_EVENT_NAME = 'push'
    $resolve = Join-Path $repo 'tools/ci/resolve-release.ps1'

    function Resolve-Fixture([string]$Tag) {
        $env:RELEASE_TAG = $Tag
        Set-Content -LiteralPath $env:GITHUB_OUTPUT -Value ''
        & $resolve
        ConvertFrom-StringData (Get-Content -LiteralPath $env:GITHUB_OUTPUT -Raw)
    }
    $result = Resolve-Fixture 'v3.1.0'
    if ($result.sha -ne $first -or $result.version -ne '3.1.0' -or $result.prerelease -ne 'false') { throw 'Lightweight tag did not resolve to its commit/version.' }
    $result = Resolve-Fixture 'v3.1.0-rc.1'
    if ($result.sha -ne $first -or $result.prerelease -ne 'true') { throw 'Annotated prerelease tag did not resolve to its commit.' }
    Expect-Failure { Resolve-Fixture 'v9.9.9' }
    $env:GITHUB_EVENT_NAME = 'workflow_dispatch'
    $result = Resolve-Fixture 'v3.2.0'
    if ($result.sha -ne $second) { throw 'New manual tag must use the selected commit.' }
    $env:RELEASE_TAG = 'v3.1.0'
    Expect-Failure { & $resolve -ExpectedRevision $second }
    Expect-Failure { Resolve-Fixture 'v3.1.00' }
    Write-Host 'PASS existing/annotated tags, manual new tags, missing pushed tags and moved-tag rejection'
} finally {
    Pop-Location
    foreach ($entry in $savedEnvironment.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value) }
    $allowedRoot = [IO.Path]::GetFullPath((Join-Path $repo 'out')) + [IO.Path]::DirectorySeparatorChar
    if (-not $fixture.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Invalid test cleanup path: $fixture" }
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
