<#
.SYNOPSIS
    Writes winget manifests for a release into packaging/winget/out/<version>, filling in the
    MSI URLs and SHA256 hashes from artifacts/. Submit the folder to microsoft/winget-pkgs
    (e.g. with `wingetcreate submit`).
#>
param(
    [Parameter(Mandatory)] [string]$Tag,
    [string]$Repository = 'STR0UDY/scour'
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot/../.."
$version = $Tag.TrimStart('v')
$out = Join-Path $PSScriptRoot "out/$version"
New-Item -ItemType Directory -Force $out | Out-Null

function Hash($rid) {
    $msi = Join-Path $root "artifacts/Scour-$version-$rid.msi"
    if (-not (Test-Path $msi)) { throw "Missing $msi; run build/publish.ps1 first." }
    (Get-FileHash $msi -Algorithm SHA256).Hash
}

$values = @{
    '{{VERSION}}'     = $version
    '{{REPO}}'        = $Repository
    '{{SHA256_X64}}'  = Hash 'win-x64'
    '{{SHA256_ARM64}}' = Hash 'win-arm64'
}

foreach ($template in Get-ChildItem $PSScriptRoot -Filter '*.yaml') {
    $text = Get-Content $template.FullName -Raw
    foreach ($key in $values.Keys) { $text = $text.Replace($key, $values[$key]) }
    Set-Content -Path (Join-Path $out $template.Name) -Value $text -Encoding utf8
}
Write-Host "Manifests written to $out"
