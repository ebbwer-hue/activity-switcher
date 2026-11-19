$ErrorActionPreference = 'Stop'
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) {
    $compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compilerPath)) { throw '.NET Framework compiler not found.' }
& $compilerPath /nologo /target:winexe /optimize+ "/out:$PSScriptRoot\ActivitySwitcher.exe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll "$PSScriptRoot\ActivitySwitcher.cs"
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Output 'Built ActivitySwitcher.exe'
