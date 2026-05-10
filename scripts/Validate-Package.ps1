param(
    [string]$PackagePath = "bin/Release/ai02/latest.zip"
)

$ErrorActionPreference = "Stop"

$expectedEntries = @(
    "ai02.deps.json",
    "ai02.dll",
    "ai02.json",
    "ai02.pdb",
    "Microsoft.Windows.SDK.NET.dll",
    "WinRT.Runtime.dll"
)

$resolvedPackage = Resolve-Path -LiteralPath $PackagePath
Add-Type -AssemblyName System.IO.Compression.FileSystem

$zip = [System.IO.Compression.ZipFile]::OpenRead($resolvedPackage.Path)
try {
    $entries = $zip.Entries |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_.FullName) -and -not $_.FullName.EndsWith("/") } |
        Select-Object -ExpandProperty FullName

    $missingEntries = $expectedEntries | Where-Object { $_ -notin $entries }
    $unexpectedEntries = $entries | Where-Object { $_ -notin $expectedEntries }

    if ($missingEntries.Count -gt 0) {
        throw "Package is missing expected files: $($missingEntries -join ', ')"
    }

    if ($unexpectedEntries.Count -gt 0) {
        throw "Package contains unexpected files: $($unexpectedEntries -join ', ')"
    }

    $manifestEntry = $zip.Entries | Where-Object FullName -eq "ai02.json" | Select-Object -First 1
    if ($null -eq $manifestEntry) {
        throw "Package is missing ai02.json."
    }

    $manifestStream = $manifestEntry.Open()
    $manifestReader = [System.IO.StreamReader]::new($manifestStream)
    try {
        $manifest = $manifestReader.ReadToEnd() | ConvertFrom-Json
    }
    finally {
        $manifestReader.Dispose()
        $manifestStream.Dispose()
    }

    if ([string]::IsNullOrWhiteSpace($manifest.Name)) {
        throw "Manifest Name is empty."
    }

    if ($manifest.InternalName -ne "ai02") {
        throw "Manifest InternalName is invalid: $($manifest.InternalName)"
    }

    if ([string]::IsNullOrWhiteSpace($manifest.AssemblyVersion)) {
        throw "Manifest AssemblyVersion is empty."
    }
}
finally {
    $zip.Dispose()
}

$hash = Get-FileHash -LiteralPath $resolvedPackage.Path -Algorithm SHA256
$hashFilePath = "$($resolvedPackage.Path).sha256"
$hashLine = "$($hash.Hash.ToLowerInvariant())  $([System.IO.Path]::GetFileName($resolvedPackage.Path))"
Set-Content -LiteralPath $hashFilePath -Value $hashLine -Encoding ascii -NoNewline

Write-Host "Package validation passed: $($resolvedPackage.Path)"
Write-Host "SHA256: $($hash.Hash)"
Write-Host "Checksum file: $hashFilePath"
