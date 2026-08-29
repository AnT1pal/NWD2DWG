# Сборка NWD2DWG (Roslyn из dotnet SDK + reference assemblies .NET Framework 4.7.2)
$ErrorActionPreference = 'Stop'

$root   = $PSScriptRoot                    # C:\rhinodwg\NWD2DWG
$src    = Join-Path $root 'src'
$dist   = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null

$dotnetDir = Split-Path -Parent (Get-Command dotnet).Source
$csc = Get-ChildItem "$dotnetDir\sdk" -Recurse -Filter csc.dll -ErrorAction SilentlyContinue |
       Where-Object { $_.FullName -match 'Roslyn\\bincore\\csc\.dll$' } |
       Sort-Object FullName -Descending | Select-Object -First 1
if (-not $csc) { throw 'csc.dll (Roslyn) не найден в dotnet SDK' }
Write-Host "Roslyn: $($csc.FullName)"

$refDir = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2'
if (-not (Test-Path $refDir)) { throw "Reference assemblies v4.7.2 не найдены: $refDir" }

# Поиск Navisworks для сборки плагина
$nwDirs = @(
    "C:\Program Files\Autodesk\Navisworks Manage 2026",
    "C:\Program Files\Autodesk\Navisworks Simulate 2026",
    "C:\Program Files\Autodesk\Navisworks Manage 2025",
    "C:\Program Files\Autodesk\Navisworks Manage 2024"
)
$nwDir = $nwDirs | Where-Object { Test-Path (Join-Path $_ "Autodesk.Navisworks.Api.dll") } | Select-Object -First 1
if (-not $nwDir) {
    # Поиск любой папки Navisworks
    $found = Get-ChildItem "C:\Program Files\Autodesk" -Filter "Navisworks*" -Directory -ErrorAction SilentlyContinue |
             Where-Object { Test-Path (Join-Path $_.FullName "Autodesk.Navisworks.Api.dll") } | Select-Object -First 1
    if ($found) { $nwDir = $found.FullName }
}

# 1. Сборка NWD2DWG.Plugin.dll
Write-Host "--- Сборка NWD2DWG.Plugin.dll ---"
$pluginSources = @(
    (Join-Path $src 'NwdPlugin.cs'),
    (Join-Path $src 'MeshDecimator.cs'),
    (Join-Path $src 'SolidReconstructor.cs'),
    (Join-Path $src 'GltfWriter.cs'),
    (Join-Path $src 'IfcWriter.cs')
)
$outPlugin = Join-Path $dist 'NWD2DWG.Plugin.dll'

$pluginRefs = @(
    "$refDir\mscorlib.dll",
    "$refDir\System.dll",
    "$refDir\System.Core.dll",
    "$refDir\Microsoft.CSharp.dll"
)
if ($nwDir) {
    Write-Host "Navisworks assemblies: $nwDir"
    $pluginRefs += "$nwDir\Autodesk.Navisworks.Api.dll"
    $pluginRefs += "$nwDir\Autodesk.Navisworks.ComApi.dll"
    $pluginRefs += "$nwDir\Autodesk.Navisworks.Interop.ComApi.dll"
}
$pluginRefArgs = $pluginRefs | ForEach-Object { "/r:`"$_`"" }
$pluginSrcArgs = $pluginSources | ForEach-Object { "`"$_`"" }

$pluginArgs = @(
    "`"$($csc.FullName)`"",
    '/nologo', '/nostdlib+', '/langversion:latest', '/optimize+', '/checked-',
    '/target:library', '/platform:anycpu', '/warn:4', '/utf8output',
    "/out:`"$outPlugin`""
) + $pluginRefArgs + $pluginSrcArgs

& dotnet $pluginArgs
if ($LASTEXITCODE -ne 0) { throw "Компиляция плагина завершилась с кодом $LASTEXITCODE" }
$pSize = (Get-Item $outPlugin).Length
Write-Host "Собран плагин: $outPlugin ($([math]::Round($pSize/1KB)) КБ)"

# 2. Сборка NWD2DWG.exe (с внедрением плагина как Embedded Resource)
Write-Host "--- Сборка NWD2DWG.exe ---"
$refArgs = Get-ChildItem "$refDir\*.dll" |
    Where-Object { $_.Name -notmatch 'Thunk|Wrapper|vshost' } |
    ForEach-Object { "/r:`"$($_.FullName)`"" }
$manifest = Join-Path $src 'app.manifest'
$outExe   = Join-Path $dist 'NWD2DWG.exe'

$exeSources = @(
    (Join-Path $src 'NWD2DWG.cs'),
    (Join-Path $src 'MeshDecimator.cs'),
    (Join-Path $src 'SolidReconstructor.cs'),
    (Join-Path $src 'GltfWriter.cs'),
    (Join-Path $src 'IfcWriter.cs')
)
$exeSrcArgs = $exeSources | ForEach-Object { "`"$_`"" }

$exeArgs = @(
    "`"$($csc.FullName)`"",
    '/nologo', '/nostdlib+', '/langversion:latest', '/optimize+', '/checked-',
    '/target:winexe', '/platform:anycpu', '/warn:4', '/utf8output',
    "/resource:`"$outPlugin`",NWD2DWG.Plugin.dll",
    "/win32manifest:`"$manifest`"",
    "/out:`"$outExe`""
) + $refArgs + $exeSrcArgs

& dotnet $exeArgs
if ($LASTEXITCODE -ne 0) { throw "Компиляция NWD2DWG.exe завершилась с кодом $LASTEXITCODE" }

$size = (Get-Item $outExe).Length
Write-Host "Собрано: $outExe ($([math]::Round($size/1KB)) КБ)"

# 3. Самотест
Write-Host "--- самотест ---"
$stDir = Join-Path $root 'selftest_out'
& $outExe '--selftest' $stDir
if ($LASTEXITCODE -ne 0) { throw "Самотест провалился (код $LASTEXITCODE), см. $stDir\selftest_report.txt" }
Write-Host "Самотест OK: $stDir"

# 4. Упаковка ZIP архива с исходным кодом (GNU GPL v3)
$zipFile = Join-Path $dist 'NWD2DWG_v2.0.zip'
$readmeFile = Join-Path $dist 'README_RU.txt'
$licFile = Join-Path $root 'LICENSE'
$buildScript = Join-Path $root 'build.ps1'

# Создаем временную структуру папки релиза для идеальной чистоты
$pkgDir = Join-Path $dist "NWD2DWG_Release_Pkg"
if (Test-Path $pkgDir) { Remove-Item $pkgDir -Recurse -Force }
New-Item -ItemType Directory -Path $pkgDir | Out-Null
New-Item -ItemType Directory -Path (Join-Path $pkgDir "src") | Out-Null

Copy-Item $outExe $pkgDir -Force
Copy-Item $outPlugin $pkgDir -Force
if (Test-Path $readmeFile) { Copy-Item $readmeFile $pkgDir -Force }
Copy-Item $licFile $pkgDir -Force
Copy-Item $buildScript $pkgDir -Force
Copy-Item (Join-Path $src "*.*") (Join-Path $pkgDir "src") -Force

$tempZip = Join-Path $dist "NWD2DWG_temp_$([System.IO.Path]::GetRandomFileName()).zip"
Compress-Archive -Path "$pkgDir\*" -DestinationPath $tempZip

try {
    if (Test-Path $zipFile) { Remove-Item $zipFile -Force -ErrorAction SilentlyContinue }
    Move-Item $tempZip $zipFile -Force
    Write-Host "Упакован полный релиз (с исходниками): $zipFile ($([math]::Round((Get-Item $zipFile).Length/1KB)) КБ)"
} catch {
    Write-Host "Архив обновлен: $tempZip"
}
Remove-Item $pkgDir -Recurse -Force -ErrorAction SilentlyContinue

