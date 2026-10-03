param([ValidatePattern('^[-a-z0-9]*$')][string]$OutputSuffix = '')
$ErrorActionPreference = 'Stop'
$probeRoot = $PSScriptRoot
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$frameworkCsc = Join-Path $frameworkRoot 'csc.exe'
if (-not (Test-Path -LiteralPath $frameworkCsc)) { throw '.NET Framework C# compiler is unavailable. No install will be performed.' }
$frameworkWpf = Join-Path $frameworkRoot 'WPF'
$probeExecutable = Join-Path $probeRoot ('ExternalImeProbe' + $OutputSuffix + '.exe')
$compilerArgs = @('/nologo', '/target:winexe', '/platform:x64', "/out:$probeExecutable", '/reference:System.Web.Extensions.dll')
$compilerArgs += "/reference:$(Join-Path $frameworkWpf 'UIAutomationClient.dll')"
$compilerArgs += "/reference:$(Join-Path $frameworkWpf 'UIAutomationTypes.dll')"
$compilerArgs += "/reference:$(Join-Path $frameworkWpf 'WindowsBase.dll')"
$compilerArgs += Join-Path $probeRoot 'ExternalImeProbe.cs'
& $frameworkCsc @compilerArgs
if ($LASTEXITCODE -ne 0) { throw "C# compiler failed: $LASTEXITCODE" }
Write-Output $probeExecutable
