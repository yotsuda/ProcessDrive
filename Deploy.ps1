# ProcessDrive Deploy Script
# Usage: Run as Administrator
#   powershell -ExecutionPolicy Bypass -File Deploy.ps1

$ErrorActionPreference = 'Stop'

$ModuleName = 'ProcessDrive'
$TargetDir = Join-Path $env:ProgramFiles 'PowerShell\7\Modules' $ModuleName
$ProjectDir = $PSScriptRoot

# Build
Write-Host "Building $ModuleName..." -ForegroundColor Cyan
dotnet build "$ProjectDir\$ModuleName.csproj" -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

$BinDir = Join-Path $ProjectDir 'bin\Release\net9.0'

# Create module directory
if (!(Test-Path $TargetDir)) { New-Item $TargetDir -ItemType Directory -Force | Out-Null }

# Copy files
$ModuleDir = Join-Path $ProjectDir 'module'
Copy-Item "$BinDir\$ModuleName.dll" $TargetDir -Force
Copy-Item "$ModuleDir\$ModuleName.psd1" $TargetDir -Force
Copy-Item "$ModuleDir\$ModuleName.format.ps1xml" $TargetDir -Force

Write-Host ''
Write-Host "Deployed to: $TargetDir" -ForegroundColor Green
Write-Host ''
Write-Host 'Usage:' -ForegroundColor Yellow
Write-Host '  New-ProcDrive'
Write-Host '  cd Proc:\'
Write-Host '  dir'
