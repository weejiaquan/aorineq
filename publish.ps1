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
# Every sidecar is regenerated here, every time, and then VERIFIED AS SHIPPED - because it used
# not to be. Until v3.1.0 this script published only the exe, so publish\AorinEQ.exe.sha256 was
# whatever the last release had left on disk. A release cut from a stale sidecar publishes a digest
# that does not match its own exe, and UpdateChecker REQUIRES both assets and verifies the download
# against that digest: every user's auto-update would refuse the update, with nothing in the build
# output to say why. It was caught with the v3.0.0 hash still sitting next to a v3.1.0 exe.

$ErrorActionPreference = 'Stop'

$out = 'publish'
$exeName = 'AorinEQ.exe'
$setupName = 'AorinEQ-Setup.exe'
$exe = Join-Path $out $exeName
$setup = Join-Path $out $setupName
$issScript = Join-Path 'installer' 'AorinEQ.iss'

# Writes "<lowercase hex> *<file name>" (the sha256sum binary-mode convention, which is what the
# shipped UpdateChecker.ParseSha256Text accepts), then reads it back off disk and parses it the way
# that shipped function does, and re-hashes the file. The two are proven to agree AS SHIPPED rather
# than as intended. Returns the verified digest.
function Publish-Sha256Sidecar {
    param([Parameter(Mandatory)][string] $Path)

    $name = Split-Path $Path -Leaf
    $sidecar = "$Path.sha256"

    $hash = (Get-FileHash $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -Path $sidecar -Value "$hash *$name" -NoNewline -Encoding ascii

    # Mirrors ParseSha256Text: a bare hex digest, or a sha256sum line whose first field is one.
    $text = (Get-Content $sidecar -Raw).Trim()
    $parsed = ($text -split '\s+')[0]
    $actual = (Get-FileHash $Path -Algorithm SHA256).Hash.ToLowerInvariant()

    if ($parsed -notmatch '^[0-9a-f]{64}$') {
        throw "sidecar $sidecar is not a 64-character lowercase hex digest: '$text'"
    }
    if ($parsed -ne $actual) {
        throw "SHA-256 MISMATCH - $sidecar says $parsed but $Path hashes to $actual. Do not release this."
    }
    return $actual
}

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

# The installer's version comes from the exe's FileVersion; the exe's comes from the csproj. If
# those two ever disagree, Apps & Features would be naming a build that is not the one installed.
# Compared on Major.Minor.Build alone: the SDK stamps the exe "3.3.0.0" and Inno stamps the setup
# "3.3.0", which are the same version written two ways.
function Get-ShortVersion {
    param([Parameter(Mandatory)][string] $Path)
    $v = [Version](Get-Item $Path).VersionInfo.FileVersion
    return "$($v.Major).$($v.Minor).$([Math]::Max($v.Build, 0))"
}
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
