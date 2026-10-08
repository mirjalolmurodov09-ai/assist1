<#
.SYNOPSIS  Builds the self-contained win-x64 Release of the Teacher and the Student Agent into .\publish\teacher and .\publish\student
           and (optionally) the three installers into .\artifacts.
.EXAMPLE   .\build\publish.ps1 -Installer
#>
param([switch]$Installer)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'publish'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

foreach ($app in @(@{ Name = 'teacher'; Project = 'src\TeacherApp\ClassroomControl.Teacher.csproj' }, @{ Name = 'student'; Project = 'src\StudentAgent\ClassroomControl.StudentAgent.csproj' })) {
    dotnet publish (Join-Path $root $app.Project) -c Release -r win-x64 --self-contained true -o (Join-Path $out $app.Name)
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($app.Name)" }
}

if ($Installer) {
    $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) was not found. Install it from https://jrsoftware.org/isinfo.php' }
    foreach ($edition in 'Teacher', 'Student', 'Full') {
        & $iscc "/DEdition=$edition" (Join-Path $root 'installer\ClassroomControl.iss')
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed for $edition" }
    }
    Get-ChildItem (Join-Path $root 'artifacts') -Filter *.exe | Select-Object Name, Length
}
Write-Host "Published to $out"
