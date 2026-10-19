<#
.SYNOPSIS
    Builds a release of Scour: one self-contained folder per architecture with the app, the CLI
    and the admin helper side by side, a zip of it, and an MSI installer.

.EXAMPLE
    ./build/publish.ps1                       # x64 and ARM64, zip + MSI, unsigned
    ./build/publish.ps1 -Runtime win-x64 -NoMsi
    ./build/publish.ps1 -Sign                 # signs every binary first (see Sign-Files below)

.NOTES
    Signing: set SCOUR_SIGN_CERT_THUMBPRINT for an OV/EV certificate in the user store, or
    SCOUR_TRUSTED_SIGNING_METADATA (path to an Azure Trusted Signing metadata.json) plus
    SCOUR_TRUSTED_SIGNING_DLIB (path to Azure.CodeSigning.Dlib.dll). signtool must be on PATH.
#>
param(
    [string[]]$Runtime = @('win-x64', 'win-arm64'),
    [string]$Configuration = 'Release',
    [switch]$Sign,
    [switch]$NoMsi
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content "$root/Directory.Build.props")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$artifacts = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Force $artifacts | Out-Null

function Sign-Files([string]$dir) {
    $targets = Get-ChildItem $dir -Recurse -Include 'ScourApp.exe', 'scour.exe', 'Scour.Elevator.exe', 'Scour.*.dll', 'ScourApp.dll', 'scour.dll' | ForEach-Object FullName
    if ($env:SCOUR_TRUSTED_SIGNING_METADATA) {
        & signtool sign /v /fd SHA256 /tr http://timestamp.acs.microsoft.com /td SHA256 `
            /dlib $env:SCOUR_TRUSTED_SIGNING_DLIB /dmdf $env:SCOUR_TRUSTED_SIGNING_METADATA @targets
    } elseif ($env:SCOUR_SIGN_CERT_THUMBPRINT) {
        & signtool sign /v /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /sha1 $env:SCOUR_SIGN_CERT_THUMBPRINT @targets
    } else {
        throw 'Signing requested but neither SCOUR_TRUSTED_SIGNING_METADATA nor SCOUR_SIGN_CERT_THUMBPRINT is set.'
    }
    if ($LASTEXITCODE -ne 0) { throw 'signtool failed.' }
}

foreach ($rid in $Runtime) {
    $out = Join-Path $artifacts "Scour-$version-$rid"
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    $platform = if ($rid -eq 'win-arm64') { 'ARM64' } else { 'x64' }

    Write-Host "== ${rid}: app" -ForegroundColor Cyan
    dotnet publish "$root/src/Scour.App/Scour.App.csproj" -c $Configuration -r $rid -p:Platform=$platform `
        --self-contained -p:WindowsAppSDKSelfContained=true -p:PublishReadyToRun=true -o $out
    if ($LASTEXITCODE -ne 0) { throw "App publish failed for $rid" }

    # The CLI and helper share the app's folder and its runtime, so they are published on top.
    foreach ($project in 'Scour.Cli', 'Scour.Elevator') {
        Write-Host "== ${rid}: $project" -ForegroundColor Cyan
        dotnet publish "$root/src/$project/$project.csproj" -c $Configuration -r $rid --self-contained -o $out
        if ($LASTEXITCODE -ne 0) { throw "$project publish failed for $rid" }
    }

    Remove-Item "$out/*.pdb" -ErrorAction SilentlyContinue
    if ($Sign) { Sign-Files $out }

    $zip = "$out.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path "$out/*" -DestinationPath $zip
    Write-Host "Zip: $zip" -ForegroundColor Green

    if (-not $NoMsi) {
        Write-Host "== ${rid}: installer" -ForegroundColor Cyan
        dotnet build "$root/installer/Scour.Installer.wixproj" -c $Configuration -p:Platform=$platform `
            -p:PublishDir="$out/" -p:ProductVersion=$version -p:OutputName="Scour-$version-$rid" -o $artifacts
        if ($LASTEXITCODE -ne 0) { throw "Installer build failed for $rid" }
        Remove-Item "$artifacts/*.wixpdb" -ErrorAction SilentlyContinue
        if ($Sign -and (Get-Command signtool -ErrorAction SilentlyContinue)) { Sign-Files $artifacts }
        Write-Host "MSI: $artifacts/Scour-$version-$rid.msi" -ForegroundColor Green
    }
}

Get-ChildItem $artifacts -File | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash, $_.Name
} | Set-Content (Join-Path $artifacts 'SHA256SUMS.txt')
Get-Content (Join-Path $artifacts 'SHA256SUMS.txt')
