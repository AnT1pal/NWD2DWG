# Сборка NWD2DWG (Roslyn из dotnet SDK + reference assemblies .NET Framework 4.7.2)
$ErrorActionPreference = 'Stop'

$root   = $PSScriptRoot                    # C:\rhinodwg\NWD2DWG
$version = '3.5'                           # версия релиза (как в заголовке GUI)
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
    (Join-Path $src 'IfcWriter.cs'),
    (Join-Path $src 'GeoTransform.cs'),
    (Join-Path $src 'GridExtractor.cs'),
    (Join-Path $src 'PipeTracer.cs'),
    (Join-Path $src 'BoqCalculator.cs'),
    (Join-Path $src 'BcfExporter.cs'),
    (Join-Path $src 'BimDiff.cs'),
    (Join-Path $src 'SpatialTiler.cs'),
    (Join-Path $src 'BimAnonymizer.cs'),
    (Join-Path $src 'ClashClusterer.cs'),
    (Join-Path $src 'Section2Plan.cs'),
    (Join-Path $src 'CadPurger.cs'),
    (Join-Path $src 'PenetrationBuilder.cs'),
    (Join-Path $src 'ClearanceValidator.cs'),
    (Join-Path $src 'SteelProfileMatcher.cs'),
    (Join-Path $src 'CogCalculator.cs'),
    (Join-Path $src 'IsoGenerator.cs'),
    (Join-Path $src 'ScheduleMapper.cs'),
    (Join-Path $src 'ShrinkWrapper.cs'),
    (Join-Path $src 'RoomFinishSchedule.cs'),
    (Join-Path $src 'ConfigManager.cs'),
    (Join-Path $src 'EngineeringPipeline.cs'),
    (Join-Path $src 'OutputProfile.cs'),
    (Join-Path $src 'RevisionIndex.cs'),
    (Join-Path $src 'DeliveryLog.cs'),
    (Join-Path $src 'AiSettings.cs'),
    (Join-Path $src 'XlsxWriter.cs')
)
$outPlugin = Join-Path $dist 'NWD2DWG.Plugin.dll'

$pluginRefs = @(
    "$refDir\mscorlib.dll",
    "$refDir\System.dll",
    "$refDir\System.Core.dll",
    "$refDir\System.IO.Compression.dll",
    "$refDir\System.IO.Compression.FileSystem.dll",
    "$refDir\System.Xml.dll",
    "$refDir\Microsoft.CSharp.dll",
    "$refDir\System.Drawing.dll",
    "$refDir\System.Windows.Forms.dll",
    "$refDir\System.Security.dll"
)
if ($nwDir) {
    Write-Host "Navisworks assemblies: $nwDir"
    $pluginRefs += "$nwDir\Autodesk.Navisworks.Api.dll"
    $pluginRefs += "$nwDir\Autodesk.Navisworks.ComApi.dll"
    $pluginRefs += "$nwDir\Autodesk.Navisworks.Interop.ComApi.dll"
    # Clash Detective и TimeLiner: источники данных для BCF, кластеризации и 4D
    $pluginRefs += "$nwDir\Autodesk.Navisworks.Clash.dll"
    $pluginRefs += "$nwDir\Autodesk.Navisworks.Timeliner.dll"
}
$pluginRefArgs = $pluginRefs | ForEach-Object { "/r:`"$_`"" }
$pluginSrcArgs = $pluginSources | ForEach-Object { "`"$_`"" }

$pluginArgs = @(
    "`"$($csc.FullName)`"",
    '/nologo', '/nostdlib+', '/langversion:latest', '/optimize+', '/checked-',
    '/target:library', '/platform:anycpu', '/warn:4', '/utf8output',
    "/out:`"$outPlugin`""
) + $pluginRefArgs + $pluginSrcArgs

$pluginBuilt = $false
if ($nwDir) {
    & dotnet $pluginArgs
    if ($LASTEXITCODE -ne 0) { throw "Компиляция плагина завершилась с кодом $LASTEXITCODE" }
    $pSize = (Get-Item $outPlugin).Length
    Write-Host "Собран плагин: $outPlugin ($([math]::Round($pSize/1KB)) КБ)"
    $pluginBuilt = $true
} else {
    # NwdPlugin.cs жёстко ссылается на Autodesk.Navisworks.Api, поэтому без
    # установленного Navisworks (например, на раннере CI) плагин не собрать.
    # Собираем только exe: он остаётся работоспособным для --selftest,
    # --diagnostics и всех офлайн-модулей.
    Write-Host "Navisworks не найден - плагин пропущен, собирается только NWD2DWG.exe"
}

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
    (Join-Path $src 'IfcWriter.cs'),
    (Join-Path $src 'GeoTransform.cs'),
    (Join-Path $src 'GridExtractor.cs'),
    (Join-Path $src 'PipeTracer.cs'),
    (Join-Path $src 'BoqCalculator.cs'),
    (Join-Path $src 'BcfExporter.cs'),
    (Join-Path $src 'BimDiff.cs'),
    (Join-Path $src 'SpatialTiler.cs'),
    (Join-Path $src 'BimAnonymizer.cs'),
    (Join-Path $src 'ClashClusterer.cs'),
    (Join-Path $src 'Section2Plan.cs'),
    (Join-Path $src 'CadPurger.cs'),
    (Join-Path $src 'PenetrationBuilder.cs'),
    (Join-Path $src 'ClearanceValidator.cs'),
    (Join-Path $src 'SteelProfileMatcher.cs'),
    (Join-Path $src 'CogCalculator.cs'),
    (Join-Path $src 'IsoGenerator.cs'),
    (Join-Path $src 'ScheduleMapper.cs'),
    (Join-Path $src 'ShrinkWrapper.cs'),
    (Join-Path $src 'RoomFinishSchedule.cs'),
    (Join-Path $src 'ConfigManager.cs'),
    (Join-Path $src 'EngineeringPipeline.cs'),
    (Join-Path $src 'OutputProfile.cs'),
    (Join-Path $src 'RevisionIndex.cs'),
    (Join-Path $src 'DeliveryLog.cs'),
    (Join-Path $src 'AiSettings.cs'),
    (Join-Path $src 'XlsxWriter.cs')
)
$exeSrcArgs = $exeSources | ForEach-Object { "`"$_`"" }

$exeArgs = @(
    "`"$($csc.FullName)`"",
    '/nologo', '/nostdlib+', '/langversion:latest', '/optimize+', '/checked-',
    '/target:winexe', '/platform:anycpu', '/warn:4', '/utf8output',
    "/win32manifest:`"$manifest`"",
    "/win32icon:`"$(Join-Path $src 'NWD2DWG.ico')`"",
    "/out:`"$outExe`""
)
if ($pluginBuilt) { $exeArgs += "/resource:`"$outPlugin`",NWD2DWG.Plugin.dll" }
$exeArgs = $exeArgs + $refArgs + $exeSrcArgs

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
$zipFile = Join-Path $dist "NWD2DWG_v$version.zip"
$readmeRu = Join-Path $root 'README_RU.txt'
$readmeMd = Join-Path $root 'README.md'
$licFile = Join-Path $root 'LICENSE'
$buildScript = Join-Path $root 'build.ps1'

# Создаем временную структуру папки релиза для идеальной чистоты
$pkgDir = Join-Path $dist "NWD2DWG_Release_Pkg"
if (Test-Path $pkgDir) { Remove-Item $pkgDir -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Path $pkgDir | Out-Null
New-Item -ItemType Directory -Path (Join-Path $pkgDir "src") | Out-Null

Copy-Item $outExe $pkgDir -Force
if ($pluginBuilt) { Copy-Item $outPlugin $pkgDir -Force }
if (Test-Path $readmeRu) { Copy-Item $readmeRu $pkgDir -Force }
# Руководство пользователя (PDF) кладём в релиз, если оно собрано
$manual = Join-Path (Split-Path -Parent $root) 'NWD2DWG_Руководство_пользователя.pdf'
if (Test-Path $manual) { Copy-Item $manual $pkgDir -Force }
Copy-Item $licFile $pkgDir -Force
Copy-Item $buildScript $pkgDir -Force
Copy-Item (Join-Path $src "*.*") (Join-Path $pkgDir "src") -Force

# MCP-сервер: управление программой из внешнего клиента, зависимостей не имеет
$mcpSrc = Join-Path $root 'mcp'
if (Test-Path $mcpSrc) {
    New-Item -ItemType Directory -Path (Join-Path $pkgDir "mcp") | Out-Null
    Copy-Item (Join-Path $mcpSrc "*.*") (Join-Path $pkgDir "mcp") -Force
}

if (Test-Path $zipFile) {
    Remove-Item $zipFile -Force -ErrorAction SilentlyContinue
}
Compress-Archive -Path "$pkgDir\*" -DestinationPath $zipFile -Force
Write-Host "Упакован полный релиз (с исходниками и актуальной документацией): $zipFile ($([math]::Round((Get-Item $zipFile).Length/1KB)) КБ)"
Remove-Item $pkgDir -Recurse -Force -ErrorAction SilentlyContinue

