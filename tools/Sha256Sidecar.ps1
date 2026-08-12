# The .sha256 sidecar guard, in one place.
#
# Dot-source this file to get the two functions. It is shared by publish.ps1 (which WRITES the
# sidecars a release ships) and by .github/workflows/release.yml (which, after uploading, downloads
# the assets back off the release and checks the bytes GitHub actually stored). Both therefore parse
# a sidecar the way the SHIPPED UpdateChecker.ParseSha256Text parses it, rather than the way the
# writer intended it - which is the whole point of the guard, and the reason it is not written twice.
#
# History: until v3.1.0 publish.ps1 published only the exe, so AorinEQ.exe.sha256 was whatever the
# previous release had left on disk. A release cut from a stale sidecar ships a digest that does not
# match its own exe, and UpdateChecker REQUIRES both assets and verifies the download against that
# digest: every user's auto-update refuses the update, with nothing in the build output to say why.
# It was caught by eye, with the v3.0.0 hash still sitting next to a v3.1.0 exe.

# Dot-sourcing runs in the CALLER's scope, so this file deliberately sets no preference variables:
# it must not change how the script that sourced it behaves.

# Reads "<Path>.sha256" off disk, parses it the way ParseSha256Text does, re-hashes $Path and proves
# the two agree. Returns the verified digest; throws loudly on anything else.
function Test-Sha256Sidecar {
    param([Parameter(Mandatory)][string] $Path)

    $sidecar = "$Path.sha256"
    if (-not (Test-Path $Path)) { throw "cannot verify a digest for $Path - the file does not exist" }
    if (-not (Test-Path $sidecar)) { throw "missing sidecar $sidecar. Do not release this." }

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

# Writes "<lowercase hex> *<file name>" (the sha256sum binary-mode convention, which is what
# ParseSha256Text accepts) and then verifies it AS SHIPPED via Test-Sha256Sidecar - read back off
# disk and parsed by the shipped rules, not trusted as written. Returns the verified digest.
#
# Regenerating is idempotent, so this is also the call to make after a step that CHANGES the bytes
# of a file that already has a sidecar - code signing being the one on the roadmap. See the signing
# notes in .github/workflows/release.yml.
function Publish-Sha256Sidecar {
    param([Parameter(Mandatory)][string] $Path)

    $name = Split-Path $Path -Leaf
    $hash = (Get-FileHash $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -Path "$Path.sha256" -Value "$hash *$name" -NoNewline -Encoding ascii

    return Test-Sha256Sidecar -Path $Path
}
