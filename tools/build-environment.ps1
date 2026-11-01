function Resolve-BuildDotnet {
    param([string]$ExplicitPath, [Parameter(Mandatory)][string]$Repo)
    $candidates = if ($ExplicitPath) { @($ExplicitPath) } else {
        @(
            if ($env:DOTNET_ROOT_X64) { Join-Path $env:DOTNET_ROOT_X64 'dotnet.exe' }
            if ($env:DOTNET_ROOT) { Join-Path $env:DOTNET_ROOT 'dotnet.exe' }
            (Get-Command dotnet -All -ErrorAction SilentlyContinue | ForEach-Object Source)
            if ($env:ProgramW6432) { Join-Path $env:ProgramW6432 'dotnet/dotnet.exe' }
            Join-Path $env:ProgramFiles 'dotnet/dotnet.exe'
            Join-Path $env:USERPROFILE '.dotnet/dotnet.exe'
        )
    }
    $failures = [Collections.Generic.List[string]]::new()
    Push-Location $Repo
    try {
        foreach ($candidate in @($candidates | Where-Object { $_ } | Select-Object -Unique)) {
            if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
            $candidate = (Resolve-Path -LiteralPath $candidate).Path
            # Read the PE machine type instead of parsing localized dotnet --info text.
            $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($candidate))
            try {
                $reader.BaseStream.Position = 0x3c
                $offset = $reader.ReadInt32()
                $reader.BaseStream.Position = $offset + 4
                $machine = $reader.ReadUInt16()
            } finally { $reader.Dispose() }
            if ($machine -ne 0x8664) { $failures.Add("$candidate : not an x64 host"); continue }
            $sdkVersion = & $candidate --version 2>&1
            if ($LASTEXITCODE -eq 0) {
                Write-Host "Using .NET SDK $sdkVersion ($candidate)"
                return $candidate
            }
            $failures.Add("$candidate : no SDK compatible with global.json")
        }
    } finally { Pop-Location }
    $required = (Get-Content -LiteralPath (Join-Path $Repo 'global.json') -Raw | ConvertFrom-Json).sdk.version
    throw (@("No compatible x64 .NET SDK found (global.json requests $required).",
        'Install the .NET 10 x64 SDK: https://dotnet.microsoft.com/download/dotnet/10.0',
        'For a portable SDK, pass -DotnetPath <path/to/dotnet.exe> or set DOTNET_ROOT_X64 to its directory.') + $failures -join "`n")
}

function Test-BuildEnvironment {
    param([ValidateSet('Development', 'Publish')][string]$Profile, [string]$MsysRoot)
    if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Install PowerShell 7: winget install Microsoft.PowerShell' }
    Write-Host 'PASS Windows, PowerShell 7 and compatible x64 .NET SDK'
    if ($Profile -eq 'Development') { return }
    if (-not (Get-Command python -ErrorAction SilentlyContinue)) { throw 'Install Python 3 and add python.exe to PATH: winget install Python.Python.3.13' }
    $pythonMajor = & python -c 'import sys; print(sys.version_info[0])' 2>&1
    if ($LASTEXITCODE -ne 0 -or "$pythonMajor" -ne '3') { throw 'Python 3 is required. Install Python 3 and add it to PATH.' }
    & python --version
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $compiler = if (Test-Path -LiteralPath $vswhere) {
        & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'VC/Tools/MSVC/**/bin/Hostx64/x64/cl.exe'
    }
    if (-not $compiler) { throw 'Install Visual Studio Build Tools with Desktop development with C++ and a Windows SDK: winget install Microsoft.VisualStudio.2022.BuildTools' }
    $kitRegistry = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots' -ErrorAction SilentlyContinue
    $kits = if ($kitRegistry) { $kitRegistry.KitsRoot10 }
    if (-not $kits -or -not (Get-ChildItem -Path (Join-Path $kits 'Lib/*/um/x64/kernel32.lib') -ErrorAction SilentlyContinue)) {
        throw 'Windows SDK x64 libraries are missing. Add a Windows 10/11 SDK in Visual Studio Installer.'
    }
    Write-Host "PASS MSVC ($compiler) and Windows SDK ($kits)"
    foreach ($relative in @('usr/bin/bash.exe', 'usr/bin/make.exe', 'usr/bin/diff.exe', 'usr/bin/tar.exe',
        'mingw64/bin/gcc.exe', 'mingw64/bin/pkg-config.exe', 'mingw64/lib/libz.a')) {
        if (-not (Test-Path -LiteralPath (Join-Path $MsysRoot $relative))) {
            throw "Missing MSYS2 dependency: $MsysRoot/$relative. Install MSYS2, then run in MINGW64: pacman -S --needed make diffutils tar mingw-w64-x86_64-gcc mingw-w64-x86_64-zlib mingw-w64-x86_64-pkgconf. Use -MsysRoot for a custom installation."
        }
    }
    Write-Host "PASS MSYS2 toolchain ($MsysRoot)"
}
