param([switch]$Test)

$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (!(Test-Path -LiteralPath $compiler)) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
    $compiler = Join-Path $framework 'csc.exe'
}

$output = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
$references = @('System.Windows.Forms.dll', 'System.Drawing.dll', 'System.Runtime.Serialization.dll',
    'WPF\UIAutomationClient.dll', 'WPF\UIAutomationTypes.dll', 'WPF\WindowsBase.dll') |
    ForEach-Object { "/reference:$(Join-Path $framework $_)" }

$exe = Join-Path $output 'KeyRewind.exe'
$icon = Join-Path $PSScriptRoot 'assets\KeyRewind.ico'
$resources = @("/win32icon:$icon", "/resource:$icon,KeyRewind.ico") + @(
    Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'locales') -Filter '*.json' |
        ForEach-Object { "/resource:$($_.FullName),locales.$($_.Name)" }
)
& $compiler /nologo /warnaserror+ /optimize+ /target:winexe /platform:anycpu /codepage:65001 "/out:$exe" @resources @references @sources
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$adminShortcut = Join-Path $output 'KeyRewind (Administrator).lnk'
# The shortcut resolves KeyRewind.exe relative to its own location.
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'assets\KeyRewind (Administrator).lnk') -Destination $adminShortcut -Force
Compress-Archive -LiteralPath $exe,$adminShortcut -DestinationPath (Join-Path $output 'KeyRewind.zip') -Force

if ($Test) {
    $testExe = Join-Path $output 'SmokeTests.exe'
    $tests = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'tests') -Filter '*.cs' | ForEach-Object FullName)
    & $compiler /nologo /warnaserror+ /optimize+ /target:exe /platform:anycpu /codepage:65001 /main:SmokeTests "/out:$testExe" @resources @references @sources @tests
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    & $testExe
    if ($LASTEXITCODE -ne 0) { throw 'Smoke tests failed.' }
}

Write-Output $exe
