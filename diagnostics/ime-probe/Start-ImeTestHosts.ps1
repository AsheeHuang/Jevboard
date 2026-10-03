param([switch]$BuildOnly, [ValidatePattern('^[-a-z0-9]*$')][string]$OutputSuffix = '')
$ErrorActionPreference = 'Stop'
$probeRoot = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$frameworkRoot = Split-Path $compiler
$exe = Join-Path $probeRoot ('ImeTestHost' + $OutputSuffix + '.exe')
$wpfExe = Join-Path $probeRoot ('WpfImeTestHost' + $OutputSuffix + '.exe')
$source = Join-Path $probeRoot 'ImeTestHost.cs'
$references = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll', 'System.Xaml.dll', 'WPF\WindowsBase.dll', 'WPF\PresentationCore.dll', 'WPF\PresentationFramework.dll', 'WPF\UIAutomationTypes.dll', 'WPF\UIAutomationProvider.dll')
$compilerArgs = @('/nologo', '/target:winexe', '/platform:x64', ('/out:' + $exe))
foreach ($reference in $references) { $compilerArgs += '/reference:' + (Join-Path $frameworkRoot $reference) }
$compilerArgs += $source
$compilerArgs += Join-Path $probeRoot 'TsfProbe.cs'
& $compiler @compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'IME test host compilation failed.' }
$wpfCompilerArgs = $compilerArgs | ForEach-Object { if ($_ -eq ('/out:' + $exe)) { '/out:' + $wpfExe } else { $_ } }
& $compiler @wpfCompilerArgs
if ($LASTEXITCODE -ne 0) { throw 'WPF IME test host compilation failed.' }
if ($BuildOnly) { Write-Output $exe; Write-Output $wpfExe; return }
$logRoot = Join-Path $probeRoot 'logs'
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
# These are explicitly requested visible interactive test windows.
$forms = Start-Process -FilePath $exe -ArgumentList @('winforms', ('"' + $logRoot + '"')) -PassThru
$wpf = Start-Process -FilePath $wpfExe -ArgumentList @('wpf', ('"' + $logRoot + '"')) -PassThru
Write-Output ('WinForms PID: ' + $forms.Id)
Write-Output ('WPF PID: ' + $wpf.Id)
Write-Output ('Logs: ' + $logRoot)
