# Package guards for the release workflows (docs/adr/0007-plugin-releases-and-listing.md). Runs under Windows
# PowerShell 5.1 and pwsh. Every failure writes one line naming the check, then exits 1; nothing is ever
# swallowed. PackageCheckScriptTests runs this against synthetic zips.
#
#   & ./tools/check-package.ps1 -Zip latest.zip -Version 2611.40.0.0 -Changelog $env:Changelog -MaxBytes 4MB
param(
    [Parameter(Mandatory = $true)][string]$Zip,
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Changelog = '',
    [Parameter(Mandatory = $true)][long]$MaxBytes
)

function Fail([string]$check, [string]$message) {
    [Console]::Error.WriteLine("check-package: $check - $message")
    exit 1
}

# CRLF and LF compare equal, surrounding whitespace is ignored, and $null, a missing key and "" are all "".
function Normalize($value) {
    return (([string]$value) -replace "`r`n", "`n").Trim()
}

if (-not (Test-Path -LiteralPath $Zip -PathType Leaf)) { Fail 'zip' "$Zip does not exist" }
$full = (Resolve-Path -LiteralPath $Zip).Path

Add-Type -AssemblyName System.IO.Compression.FileSystem
$entries = @()
$manifestText = $null
$archive = [System.IO.Compression.ZipFile]::OpenRead($full)
try {
    foreach ($e in $archive.Entries) {
        $entries += [pscustomobject]@{ Name = $e.FullName; Length = $e.Length }
        if ($e.FullName -ceq 'Milieu.json') {
            $reader = New-Object System.IO.StreamReader($e.Open(), [System.Text.Encoding]::UTF8)
            try { $manifestText = $reader.ReadToEnd() } finally { $reader.Dispose() }
        }
    }
} finally {
    $archive.Dispose()
}

# Size first: a bloated package (bundled textures or source JPEGs slipping in) is the likeliest regression.
$size = (Get-Item -LiteralPath $full).Length
if ($size -gt $MaxBytes) {
    $largest = ($entries | Sort-Object Length -Descending | Select-Object -First 10 |
                ForEach-Object { '{0} ({1:N0} bytes)' -f $_.Name, $_.Length }) -join ', '
    Fail 'size' ("latest.zip is {0:N0} bytes, over the {1:N0}-byte limit. Largest entries: {2}" -f $size, $MaxBytes, $largest)
}

if ($null -eq $manifestText) { Fail 'Milieu.json' 'there is no Milieu.json at the root of the zip' }
if (-not ($entries | Where-Object { $_.Name -ceq 'Milieu.dll' })) { Fail 'Milieu.dll' 'there is no Milieu.dll at the root of the zip' }

$manifest = $manifestText | ConvertFrom-Json
if ($manifest.InternalName -cne 'Milieu') { Fail 'InternalName' "the manifest's InternalName is '$($manifest.InternalName)', not 'Milieu'" }
if ([string]$manifest.AssemblyVersion -ne $Version) {
    Fail 'AssemblyVersion' "the manifest's AssemblyVersion is '$($manifest.AssemblyVersion)', expected '$Version'"
}

$shipped = $null
if ($manifest.PSObject.Properties['Changelog']) { $shipped = $manifest.Changelog }
if ((Normalize $shipped) -cne (Normalize $Changelog)) {
    Fail 'Changelog' "the manifest's Changelog does not match the tag message (was a rebuild missing the Changelog environment variable?)"
}

Write-Host ("check-package: OK - {0:N0} bytes, Milieu {1}" -f $size, $Version)
