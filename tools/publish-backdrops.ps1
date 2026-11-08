<#
.SYNOPSIS
  Publishes Milieu's pre-converted bundled backdrops (.atex) as release assets of a backdrops-v<N> tag.

.DESCRIPTION
  The .atex files are built locally by `PrepareBackdrops atex` and are never committed (docs/adr/0005). The client
  pins each file's size and SHA-256 together with the tag, and release assets are immutable once published, so this
  script refuses to upload anything that could differ from the pins the tagged commit carries:
    1. the tag must look like backdrops-v<number>
    2. the tag must point at the checked-out HEAD
    3. Milieu/Assets/BackdropPins.cs must have no local changes
    4. the tag must already be pushed to origin
    5. no release may exist for the tag yet
    6/7. the artifact folder must hold exactly the pinned .atex files and pass `PrepareBackdrops verify`
  Only then is the release created, with --verify-tag and --latest=false, holding only *.atex.

.EXAMPLE
  pwsh tools/publish-backdrops.ps1 -Tag backdrops-v2
#>
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$Dir = "artifacts/backdrops"
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

function Fail([string]$message) { Write-Error $message; exit 1 }

# 1. Tag format.
if ($Tag -cnotmatch '^backdrops-v\d+\z') { Fail "Tag '$Tag' is not of the form backdrops-v<number>." }

# 2. The tag must be the checked-out commit, so the pins compiled here are the ones the tag ships.
$tagCommit = (git rev-parse "$Tag^{commit}" 2>$null)
if ($LASTEXITCODE -ne 0 -or -not $tagCommit) { Fail "Tag $Tag does not exist locally." }
$head = (git rev-parse HEAD)
if ($tagCommit.Trim() -ne $head.Trim()) { Fail "Tag $Tag points at $tagCommit, but HEAD is $head. Check out the tagged commit first." }

# 3. No local edits to the pinned table.
git diff --quiet HEAD -- Milieu/Assets/BackdropPins.cs
if ($LASTEXITCODE -ne 0) { Fail "Milieu/Assets/BackdropPins.cs has local changes; publish only from the committed pins." }

# 4. The tag is on origin (users' plugins pin it; a local-only tag would publish to nowhere).
git ls-remote --exit-code --tags origin "refs/tags/$Tag" *> $null
if ($LASTEXITCODE -ne 0) { Fail "Tag $Tag is not pushed to origin. Push it first: git push origin $Tag" }

# 5. Never overwrite: assets are immutable because clients pin their checksums.
gh release view $Tag *> $null
if ($LASTEXITCODE -eq 0) { Fail "Release $Tag already exists. Its assets are immutable - make a new tag (and new pins) instead." }
$global:LASTEXITCODE = 0

# 6 + 7. Exactly the pinned files, each at its pinned size, header and SHA-256 (verify also rejects extra files).
if (-not (Test-Path $Dir)) { Fail "Artifact folder '$Dir' not found. Run: dotnet run --project tools/PrepareBackdrops -c Release -- atex Milieu/DefaultBackdrops $Dir" }
dotnet run --project tools/PrepareBackdrops -c Release -- verify $Dir --tag $Tag
if ($LASTEXITCODE -ne 0) { Fail "The files in '$Dir' do not match the pins for $Tag." }

$files = Get-ChildItem $Dir -File -Filter *.atex
Write-Host "Uploading $($files.Count) file(s), $([math]::Round(($files | Measure-Object Length -Sum).Sum/1MB,1)) MB total"
gh release create $Tag --verify-tag --latest=false `
    --title "Bundled Backdrops ($Tag)" `
    --notes "Pre-converted scenes for Milieu, downloaded automatically when a scene is picked - do not install manually. Credits: Milieu/DefaultBackdrops/CREDITS.md at this tag." `
    @($files.FullName)
if ($LASTEXITCODE -ne 0) { Fail "gh release create failed." }

Write-Host "Published. Checksums (must equal BackdropPins.All):"
foreach ($f in ($files | Sort-Object Name)) {
    $h = (Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLower()
    Write-Host ("  {0}  {1}  {2}" -f $f.Name, $f.Length, $h)
}
