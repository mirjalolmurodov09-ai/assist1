<#
.SYNOPSIS  Builds the self-contained win-x64 Release of the Student Agent into .\publish and (optionally) the installer.
.EXAMPLE   .\build\publish.ps1 -Installer
#>
param([switch]$Installer)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'publish'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish (Join-Path $root 'StudentAgent\ClassroomControl.StudentAgent.csproj') `
    -c Release -r win-x64 --self-contained true -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

if ($Installer) {
    $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) was not found. Install it from https://jrsoftware.org/isinfo.php' }
    & $iscc (Join-Path $root 'installer\ClassroomControl-Student-Setup.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }
    Write-Host "Installer: $(Join-Path $root 'artifacts\ClassroomControl-Student-Setup.exe')"
}
Write-Host "Published to $out"
