<#
.SYNOPSIS
  Builds the NexaVerify IIS deployment package (API, Blazor portal, migrator, idempotent SQL script, checksums).

.DESCRIPTION
  Framework-dependent publish for win-x64 (the server needs the .NET 10 ASP.NET Core Hosting Bundle). Output layout:

    <OutputDir>/
      api/            published API + web.config (deploy/iis/web.config.api)
      portal/         published Blazor portal + web.config (deploy/iis/web.config.portal)
      migrator/       published Migrator (run `dotnet NexaVerify.Migrator.dll migrate` with an ADMIN connection string)
      sql/migrate.sql idempotent EF migration script for DBA review / manual apply
      VERSION.txt, build-info.json, SHA256SUMS
    nexaverify-iis-<Version>.zip   (everything above)

  The package contains no secrets and no environment-specific configuration. See docs/runbooks/iis-deployment.md.

.PARAMETER Version     Version label written into the package (default: 0.0.0-local).
.PARAMETER OutputDir   Where to write the package (default: artifacts/iis under the repository root).
.PARAMETER Configuration  Build configuration (default Release).
.PARAMETER SkipTests   Do not run the unit/architecture tests before publishing.

.EXAMPLE
  pwsh scripts/publish-iis.ps1 -Version 1.0.0-rc.1
#>
[CmdletBinding()]
param(
    [string] $Version = '0.0.0-local',
    [string] $OutputDir,
    [string] $Configuration = 'Release',
    [switch] $SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot 'artifacts/iis' }
$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)

function Invoke-Dotnet {
    param([Parameter(ValueFromRemainingArguments = $true)] [string[]] $Arguments)
    Write-Host "dotnet $($Arguments -join ' ')" -ForegroundColor DarkGray
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE" }
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'The .NET SDK is not on PATH (see global.json for the required version).' }

Push-Location $repoRoot
try {
    $revision = 'unknown'
    if (Get-Command git -ErrorAction SilentlyContinue) {
        $rev = (& git rev-parse --short=12 HEAD 2>$null)
        if ($LASTEXITCODE -eq 0 -and $rev) { $revision = $rev.Trim() }
    }

    if (Test-Path $OutputDir) { Remove-Item -Recurse -Force $OutputDir }
    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

    Invoke-Dotnet restore NexaVerify.slnx
    Invoke-Dotnet build NexaVerify.slnx --no-restore -c $Configuration   # warnings are errors (Directory.Build.props)

    if (-not $SkipTests) {
        # Fast, database-free suites only. The full suite (Testcontainers) runs in CI.
        foreach ($project in 'tests/Domain.UnitTests', 'tests/Application.UnitTests', 'tests/Architecture.Tests') {
            Invoke-Dotnet test $project --no-build -c $Configuration
        }
    }

    $common = @('-c', $Configuration, '-r', 'win-x64', '--self-contained', 'false', '/p:UseAppHost=false', "/p:Version=$Version", "/p:InformationalVersion=$Version+$revision")
    Invoke-Dotnet publish src/Api/NexaVerify.Api.csproj @common -o (Join-Path $OutputDir 'api')
    Invoke-Dotnet publish src/Web/Blazor/NexaVerify.Web.csproj @common -o (Join-Path $OutputDir 'portal')
    Invoke-Dotnet publish src/Migrator/NexaVerify.Migrator.csproj @common -o (Join-Path $OutputDir 'migrator')

    # Hardened web.config (limits aligned with RequestLimits, no server header, no secrets) replaces the generated one.
    Copy-Item (Join-Path $repoRoot 'deploy/iis/web.config.api') (Join-Path $OutputDir 'api/web.config') -Force
    Copy-Item (Join-Path $repoRoot 'deploy/iis/web.config.portal') (Join-Path $OutputDir 'portal/web.config') -Force

    # Idempotent migration script. Generating it needs no database; the connection string below is a syntactic placeholder.
    $sqlDir = Join-Path $OutputDir 'sql'
    New-Item -ItemType Directory -Force -Path $sqlDir | Out-Null
    $previous = $env:ConnectionStrings__Default
    $env:ConnectionStrings__Default = 'Server=placeholder;Database=NexaVerify;User Id=placeholder;Password=placeholder;TrustServerCertificate=True'
    try {
        $script = & dotnet (Join-Path $OutputDir 'migrator/NexaVerify.Migrator.dll') script
        if ($LASTEXITCODE -ne 0) { throw "Migrator 'script' failed with exit code $LASTEXITCODE" }
        Set-Content -Path (Join-Path $sqlDir 'migrate.sql') -Value $script -Encoding UTF8
    }
    finally {
        if ($null -eq $previous) { Remove-Item Env:ConnectionStrings__Default -ErrorAction SilentlyContinue } else { $env:ConnectionStrings__Default = $previous }
    }
    if ((Get-Item (Join-Path $sqlDir 'migrate.sql')).Length -lt 1024) { throw 'migrate.sql looks empty; refusing to package.' }

    Set-Content -Path (Join-Path $OutputDir 'VERSION.txt') -Value "$Version+$revision" -Encoding ASCII
    [ordered]@{ version = $Version; revision = $revision; builtUtc = (Get-Date).ToUniversalTime().ToString('o'); configuration = $Configuration; runtime = 'win-x64'; frameworkDependent = $true } |
        ConvertTo-Json | Set-Content -Path (Join-Path $OutputDir 'build-info.json') -Encoding UTF8

    # Checksums over every file in the package (verify on the server before unpacking into the site folder).
    $sums = Get-ChildItem -Path $OutputDir -Recurse -File | Where-Object { $_.Name -ne 'SHA256SUMS' } | ForEach-Object {
        $relative = $_.FullName.Substring($OutputDir.Length).TrimStart('\', '/').Replace('\', '/')
        '{0}  {1}' -f (Get-FileHash -Algorithm SHA256 -Path $_.FullName).Hash.ToLowerInvariant(), $relative
    }
    Set-Content -Path (Join-Path $OutputDir 'SHA256SUMS') -Value $sums -Encoding ASCII

    $zip = Join-Path (Split-Path $OutputDir -Parent) "nexaverify-iis-$Version.zip"
    if (Test-Path $zip) { Remove-Item -Force $zip }
    Compress-Archive -Path (Join-Path $OutputDir '*') -DestinationPath $zip
    $zipHash = (Get-FileHash -Algorithm SHA256 -Path $zip).Hash.ToLowerInvariant()

    Write-Host ''
    Write-Host "Package: $zip" -ForegroundColor Green
    Write-Host "SHA-256: $zipHash"
    Write-Host 'Next: docs/runbooks/iis-deployment.md (prerequisites, app pool, secrets, migration, smoke test).'
}
finally {
    Pop-Location
}
