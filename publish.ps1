# Publishes the single-file, self-contained release exe AND the SHA-256 sidecar that ships beside
# it in every GitHub release.
#
# The sidecar is regenerated here, every time, and then verified — because it used not to be. Until
# v3.1.0 this script published only the exe, so publish/AorinEQ.exe.sha256 was whatever the last
# release had left on disk. A release cut from a stale sidecar publishes a digest that does not
# match its own exe, and UpdateChecker REQUIRES both assets and verifies the download against that
# digest: every user's auto-update would refuse the update, and there would be nothing in the build
# output to say why. It was caught with the v3.0.0 hash still sitting next to a v3.1.0 exe.
#
# The format is the one the shipped UpdateChecker.ParseSha256Text accepts: lowercase hex, then a
# space, then "*" and the file name (the sha256sum binary-mode convention).

$ErrorActionPreference = 'Stop'

$out = 'publish'
$exeName = 'AorinEQ.exe'
$exe = Join-Path $out $exeName
$sidecar = "$exe.sha256"

dotnet publish src/AorinEQ -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
if (-not (Test-Path $exe)) { throw "publish produced no $exe" }

# Written fresh from the exe that was just produced, never carried over.
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path $sidecar -Value "$hash *$exeName" -NoNewline -Encoding ascii

# Read the sidecar back off disk and re-hash the exe, so the two are proven to agree AS SHIPPED
# rather than as intended. Parsing mirrors UpdateChecker.ParseSha256Text: a bare hex digest, or a
# sha256sum line whose first field is the digest.
$text = (Get-Content $sidecar -Raw).Trim()
$parsed = ($text -split '\s+')[0]
$actual = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()

if ($parsed -notmatch '^[0-9a-f]{64}$') {
    throw "sidecar $sidecar is not a 64-character lowercase hex digest: '$text'"
}
if ($parsed -ne $actual) {
    throw "SHA-256 MISMATCH — $sidecar says $parsed but $exe hashes to $actual. Do not release this."
}

$size = (Get-Item $exe).Length
$version = (Get-Item $exe).VersionInfo.ProductVersion
Write-Host ""
Write-Host "published $exe"
Write-Host "  version : $version"
Write-Host "  size    : $size bytes"
Write-Host "  sha256  : $actual (verified against $sidecar)"
