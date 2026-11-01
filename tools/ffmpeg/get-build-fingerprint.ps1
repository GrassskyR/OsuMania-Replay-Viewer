[CmdletBinding()]
param([string]$MsysRoot = 'C:/msys64')
$ErrorActionPreference = 'Stop'
$inputs = [Collections.Generic.List[string]]::new()
$required = @('usr/bin/bash.exe', 'usr/bin/make.exe', 'usr/bin/diff.exe', 'usr/bin/tar.exe', 'usr/bin/pacman.exe',
    'mingw64/bin/gcc.exe', 'mingw64/bin/ld.exe', 'mingw64/bin/as.exe', 'mingw64/bin/pkg-config.exe', 'mingw64/lib/libz.a')
foreach ($relative in $required) {
    $path = Join-Path $MsysRoot $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing MSYS2 dependency: $path. Install MSYS2 and run in MINGW64: pacman -S --needed make diffutils tar mingw-w64-x86_64-gcc mingw-w64-x86_64-zlib mingw-w64-x86_64-pkgconf. Pass -MsysRoot for a custom installation."
    }
    $inputs.Add("${relative}:$((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash)")
}
foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -File | Where-Object Extension -In '.ps1', '.sh', '.json' | Sort-Object Name) {
    $inputs.Add("$($file.Name):$((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash)")
}
# Include the package graph: GCC's driver alone does not identify its runtime, headers or linker.
$packages = & (Join-Path $MsysRoot 'usr/bin/pacman.exe') -Q 2>&1
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect the MSYS2 toolchain package versions.' }
$inputs.Add(($packages -join "`n"))
$sha = [Security.Cryptography.SHA256]::Create()
try { ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($inputs -join "`n")))).Replace('-', '').ToLowerInvariant() }
finally { $sha.Dispose() }
