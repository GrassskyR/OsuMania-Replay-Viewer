function Assert-BuildVersion {
    param([Parameter(Mandatory)][string]$Version)
    # Windows file versions also require each numeric component to fit in 16 bits.
    if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$') {
        throw "Invalid version '$Version'. Use MAJOR.MINOR.PATCH or MAJOR.MINOR.PATCH-prerelease (for example 3.1.0-rc.1)."
    }
    foreach ($component in $Version.Split('-')[0].Split('.')) {
        if ($component.Length -gt 5 -or [int]$component -gt 65534) { throw 'Version components must be between 0 and 65534.' }
    }
    if ($Version.Contains('-')) {
        foreach ($identifier in $Version.Substring($Version.IndexOf('-') + 1).Split('.')) {
            if ($identifier -match '^0[0-9]+$') { throw 'Numeric prerelease identifiers cannot have leading zeros.' }
        }
    }
}
