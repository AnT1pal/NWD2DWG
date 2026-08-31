# ============================================================================
#  run_tests.ps1 — сборка и прогон регрессионных тестов
#
#  Тесты компилируются вместе с исходниками проекта, кроме NwdPlugin.cs:
#  тот загружается внутрь процесса Navisworks и требует его сборок, которых
#  на машине проверки может не быть. Точка входа переопределяется на
#  TestMain, потому что в NWD2DWG.cs есть собственный Main.
#
#  Запуск:  powershell -ExecutionPolicy Bypass -File run_tests.ps1
# ============================================================================
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $root 'src'
$work = Join-Path $root 'tests_out'

$sdk = Get-ChildItem 'C:\Program Files\dotnet\sdk' -Directory |
       Sort-Object Name -Descending | Select-Object -First 1
$csc = Get-Item (Join-Path $sdk.FullName 'Roslyn\bincore\csc.dll')
$refDir = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2'
if (-not (Test-Path $refDir)) { throw "Reference assemblies v4.7.2 не найдены: $refDir" }

$sources = @(Get-ChildItem "$src\*.cs" | Where-Object { $_.Name -ne 'NwdPlugin.cs' } |
             ForEach-Object { $_.FullName })
$sources += (Join-Path $root 'tests_RegressionTests.cs')

$refArgs = Get-ChildItem "$refDir\*.dll" |
    Where-Object { $_.Name -notmatch 'Thunk|Wrapper|vshost' } |
    ForEach-Object { "/r:`"$($_.FullName)`"" }
$srcArgs = $sources | ForEach-Object { "`"$_`"" }

if (Test-Path $work) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Path $work | Out-Null
$outExe = Join-Path $work 'RegressionTests.exe'

Write-Host "--- сборка тестов ($($sources.Count) файлов) ---"
$cscArgs = @(
    "`"$($csc.FullName)`"",
    '/nologo', '/nostdlib+', '/langversion:latest', '/warn:4', '/utf8output',
    '/target:exe', '/platform:anycpu', '/main:TestMain',
    "/out:`"$outExe`""
) + $refArgs + $srcArgs

& dotnet $cscArgs
if ($LASTEXITCODE -ne 0) { throw "Компиляция тестов завершилась с кодом $LASTEXITCODE" }

Write-Host "--- прогон ---"
& $outExe $work
$code = $LASTEXITCODE
if ($code -ne 0) { throw "Регрессионные тесты провалены (код $code)" }
Write-Host "Результаты и временные файлы: $work"
