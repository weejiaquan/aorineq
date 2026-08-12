# Publishes everything a GitHub release ships:
#
#   publish\AorinEQ.exe              the portable, self-contained single file
#   publish\AorinEQ.exe.sha256       its digest, in the format the shipped updater parses
#   publish\AorinEQ-Setup.exe        the Inno Setup installer that carries that exact exe
#   publish\AorinEQ-Setup.exe.sha256 its digest, same format
#
# THE EXE NAMES ARE A CONTRACT. UpdateChecker requires assets named exactly AorinEQ.exe and
# AorinEQ.exe.sha256 in every release, and the website's "latest" links point at them. The
# installer is additive - it never replaces or renames the portable pair.
#
# Every sidecar is regenerated here, every time, and then VERIFIED AS SHIPPED - see
# tools/ReleaseChecks.ps1, which owns that guard and is shared with the release workflow.
#
# This script stays the single source of truth for HOW a release is built. .github/workflows/
# release.yml runs this exact file on a clean windows-latest runner rather than repeating its
# flags, so a change made here is a change CI makes too.

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'tools\ReleaseChecks.ps1')

$out = 'publish'
$exeName = 'AorinEQ.exe'
$setupName = 'AorinEQ-Setup.exe'
$exe = Join-Path $out $exeName
$setup = Join-Path $out $setupName
$issScript = Join-Path 'installer' 'AorinEQ.iss'

dotnet publish src/AorinEQ -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
if (-not (Test-Path $exe)) { throw "publish produced no $exe" }

$exeHash = Publish-Sha256Sidecar -Path $exe

# The installer reads its version out of the exe it packages, so it cannot drift from the binary -
# but ISCC is a separate tool with its own copy of the script, so the result is checked below.
$iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup 6 not found. Install it with: winget install --id JRSoftware.InnoSetup --silent"
}

& $iscc /Q $issScript
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE compiling $issScript" }
if (-not (Test-Path $setup)) { throw "ISCC produced no $setup" }

$setupHash = Publish-Sha256Sidecar -Path $setup

# The installer's version comes from the exe's FileVersion; the exe's comes from Directory.Build.props
# via the csproj. If those two ever disagree, Apps & Features would be naming a build that is not the
# one installed. (Get-ShortVersion lives in tools/ReleaseChecks.ps1 - the release workflow compares
# the same three parts against the tag.)
$exeVersion = Get-ShortVersion -Path $exe
$setupVersion = Get-ShortVersion -Path $setup
if ($setupVersion -ne $exeVersion) {
    throw "installer version $setupVersion does not match the exe it carries ($exeVersion). Do not release this."
}

$productVersion = (Get-Item $exe).VersionInfo.ProductVersion
Write-Host ""
Write-Host "published $exe"
Write-Host "  version : $productVersion"
Write-Host "  size    : $((Get-Item $exe).Length) bytes"
Write-Host "  sha256  : $exeHash (verified against $exe.sha256)"
Write-Host "published $setup"
Write-Host "  version : $setupVersion (matches the exe it carries)"
Write-Host "  size    : $((Get-Item $setup).Length) bytes"
Write-Host "  sha256  : $setupHash (verified against $setup.sha256)"
