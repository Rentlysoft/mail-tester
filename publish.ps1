#Requires -Version 7
<#
.SYNOPSIS
    Publishes mail-tester as a self-contained single file executable per platform.
.DESCRIPTION
    Output goes to dist/<rid>/. Trimming stays off: MailKit resolves SASL mechanisms
    through reflection and a trimmed build fails at runtime while authenticating.
#>
[CmdletBinding()]
param(
    [string[]] $Runtimes = @('win-x64', 'linux-x64'),
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src/MailTester/MailTester.csproj'
$distRoot = Join-Path $PSScriptRoot 'dist'

if (Test-Path $distRoot) {
    Remove-Item -Recurse -Force $distRoot
}

foreach ($runtime in $Runtimes) {
    $target = Join-Path $distRoot $runtime
    Write-Host "Publicando $runtime -> $target" -ForegroundColor Cyan

    dotnet publish $project `
        --configuration $Configuration `
        --runtime $runtime `
        --self-contained true `
        --output $target `
        -p:PublishSingleFile=true `
        -p:PublishTrimmed=false

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish falló para $runtime (exit code $LASTEXITCODE)"
    }
}

Write-Host "`nBinarios generados:" -ForegroundColor Green
Get-ChildItem -Path $distRoot -Recurse -Include 'mail-tester', 'mail-tester.exe' |
    Select-Object @{ Name = 'Ruta'; Expression = { $_.FullName.Replace("$PSScriptRoot\", '') } },
                  @{ Name = 'MB'; Expression = { [math]::Round($_.Length / 1MB, 1) } }
