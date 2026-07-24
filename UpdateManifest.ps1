param (
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $manifestFile,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $versionString
)

$ErrorActionPreference = "Stop"

try {
    if ($versionString -notmatch '^\d+\.\d+\.\d+$') {
        throw "Invalid version '$versionString'. Expected major.minor.patch."
    }

    $manifestBytes = [System.IO.File]::ReadAllBytes($manifestFile)
    $hasUtf8Bom = $manifestBytes.Length -ge 3 `
        -and $manifestBytes[0] -eq 0xEF `
        -and $manifestBytes[1] -eq 0xBB `
        -and $manifestBytes[2] -eq 0xBF
    $contentOffset = if ($hasUtf8Bom) { 3 } else { 0 }
    $utf8 = [System.Text.UTF8Encoding]::new($hasUtf8Bom, $true)
    $manifest = $utf8.GetString(
        $manifestBytes,
        $contentOffset,
        $manifestBytes.Length - $contentOffset)

    $versionMatches = [regex]::Matches(
        $manifest,
        '(?<prefix>"version_number"\s*:\s*")(?<version>[^"]*)(?<suffix>")')
    if ($versionMatches.Count -ne 1) {
        throw "Expected exactly one version_number in '$manifestFile', found $($versionMatches.Count)."
    }

    $versionGroup = $versionMatches[0].Groups["version"]
    if ($versionGroup.Value -ceq $versionString) {
        return
    }

    $updatedManifest =
        $manifest.Remove($versionGroup.Index, $versionGroup.Length).Insert($versionGroup.Index, $versionString)
    [System.IO.File]::WriteAllText($manifestFile, $updatedManifest, $utf8)
} catch {
    Write-Error $_.Exception.Message
    exit 1
}
