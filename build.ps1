# Builds TibiaTimers.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
# Usage:  powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc)) { throw ".NET Framework 4.x compiler (csc.exe) not found." }

$out = Join-Path $PSScriptRoot 'TibiaTimers.exe'
& $csc /nologo /target:winexe /optimize+ "/out:$out" /r:System.Windows.Forms.dll /r:System.Drawing.dll (Join-Path $PSScriptRoot 'TibiaTimers.cs')
if ($LASTEXITCODE -ne 0) { throw "Build failed (is TibiaTimers.exe still running?)" }
Write-Host "Built $out"
