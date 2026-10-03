$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
if (-not (Test-Path $csc)) { throw '.NET Framework C# compiler not found.' }
New-Item -ItemType Directory -Force (Join-Path $root 'bin') | Out-Null
$out = Join-Path $root 'bin\Jevboard.exe'
$refs = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Security.dll', 'System.Web.Extensions.dll', 'WPF\UIAutomationClient.dll', 'WPF\UIAutomationTypes.dll', 'WPF\WindowsBase.dll')
$args = @('/nologo', '/target:winexe', '/platform:x64', "/out:$out")
foreach ($r in $refs) { $args += '/reference:' + (Join-Path $fw $r) }
$args += Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }
& $csc @args
if ($LASTEXITCODE -ne 0) { throw "csc failed: $LASTEXITCODE" }
Write-Output $out
