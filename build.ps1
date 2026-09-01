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
# Плагин связан с версией API на этапе компиляции: собранный под одно
# поколение в другом не загрузится. Поэтому собираем его под КАЖДУЮ найденную
# установку и вкладываем все в exe — программа выберет нужный при запуске.
#
# Freedom пропускаем: у него нет Automation API.
$nwAll = @()
foreach ($adsk in "C:\Program Files\Autodesk", "C:\Program Files (x86)\Autodesk") {
    if (-not (Test-Path $adsk)) { continue }
    foreach ($d in Get-ChildItem $adsk -Directory -Filter "Navisworks*" -ErrorAction SilentlyContinue) {
        if ($d.Name -match 'Freedom|Exporters') { continue }
        $api = Join-Path $d.FullName "Autodesk.Navisworks.Api.dll"
        if (-not (Test-Path $api)) { continue }
        $major = [Reflection.AssemblyName]::GetAssemblyName($api).Version.Major
        if ($nwAll.major -contains $major) { continue }
        $nwAll += [pscustomobject]@{ dir = $d.FullName; major = $major; name = $d.Name }
    }
}
# Дополнительно: заранее выложенные сборки в refs\<версия>\ — на случай, когда
# нужная версия Navisworks на машине сборки не установлена.
$refsRoot = Join-Path $root 'refs'
if (Test-Path $refsRoot) {
    foreach ($d in Get-ChildItem $refsRoot -Directory) {
        $api = Join-Path $d.FullName "Autodesk.Navisworks.Api.dll"
        if (-not (Test-Path $api)) { continue }
        $major = [Reflection.AssemblyName]::GetAssemblyName($api).Version.Major
        if ($nwAll.major -contains $major) { continue }
        $nwAll += [pscustomobject]@{ dir = $d.FullName; major = $major; name = "refs\" + $d.Name }
    }
}
$nwAll = @($nwAll | Sort-Object major)
$nwDir = if ($nwAll.Count -gt 0) { $nwAll[-1].dir } else { $null }

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
$pluginFiles = @()          # (путь, версияAPI) — всё, что вложим в exe

if ($nwAll.Count -gt 0) {
    foreach ($nw in $nwAll) {
        $dllOut = Join-Path $dist ("NWD2DWG.Plugin.{0}.dll" -f $nw.major)
        $refs = @(
            "$refDir\mscorlib.dll", "$refDir\System.dll", "$refDir\System.Core.dll",
            "$refDir\System.IO.Compression.dll", "$refDir\System.IO.Compression.FileSystem.dll",
            "$refDir\System.Xml.dll", "$refDir\Microsoft.CSharp.dll",
            "$refDir\System.Drawing.dll", "$refDir\System.Windows.Forms.dll",
            "$refDir\System.Security.dll"
        )
        foreach ($d in 'Autodesk.Navisworks.Api','Autodesk.Navisworks.ComApi',
                       'Autodesk.Navisworks.Interop.ComApi','Autodesk.Navisworks.Clash',
                       'Autodesk.Navisworks.Timeliner') {
            $p = Join-Path $nw.dir "$d.dll"
            if (Test-Path $p) { $refs += $p }
        }
        $args = @("`"$($csc.FullName)`"", '/nologo','/nostdlib+','/langversion:latest',
                  '/optimize+','/checked-','/target:library','/platform:anycpu',
                  '/warn:4','/utf8output', "/out:`"$dllOut`"")
        $args += ($refs | ForEach-Object { "/r:`"$_`"" })
        $args += ($pluginSources | ForEach-Object { "`"$_`"" })

        & dotnet $args
        if ($LASTEXITCODE -ne 0) {
            Write-Host ("ВНИМАНИЕ: плагин под {0} (API {1}) не собрался — пропущен" -f $nw.name, $nw.major)
            continue
        }
        $kb = [math]::Round((Get-Item $dllOut).Length/1KB)
        Write-Host ("Собран плагин под {0} (API {1}): {2} КБ" -f $nw.name, $nw.major, $kb)
        $pluginFiles += [pscustomobject]@{ path = $dllOut; major = $nw.major }
        $pluginBuilt = $true
    }
    # Плагин самой свежей версии остаётся и под общим именем: он подхватится,
    # если версия установленного Navisworks почему-то не определилась.
    if ($pluginFiles.Count -gt 0) {
        Copy-Item $pluginFiles[-1].path $outPlugin -Force
    }
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
if ($pluginBuilt) {
    $exeArgs += "/resource:`"$outPlugin`",NWD2DWG.Plugin.dll"
    foreach ($p in $pluginFiles) {
        $exeArgs += "/resource:`"$($p.path)`",NWD2DWG.Plugin.$($p.major).dll"
    }
    Write-Host ("Вложено плагинов: {0} (API {1})" -f $pluginFiles.Count,
                (($pluginFiles | ForEach-Object { $_.major }) -join ', '))
}
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
# Пользовательская документация: руководство и сценарии работы
$upDir = Split-Path -Parent $root
foreach ($doc in 'NWD2DWG_Руководство_пользователя.pdf', 'Сценарии_использования.pdf', 'проверка.ps1') {
    $p = Join-Path $upDir $doc
    if (Test-Path $p) { Copy-Item $p $pkgDir -Force }
}
if (Test-Path $readmeMd) { Copy-Item $readmeMd $pkgDir -Force }
Copy-Item $licFile $pkgDir -Force
Copy-Item $buildScript $pkgDir -Force
# Всё, чем собирается проект: лицензия обязывает отдавать средства сборки,
# а не только исходники самой программы.
foreach ($extra in 'run_tests.ps1', 'build_installer.ps1', 'tests_RegressionTests.cs') {
    $p = Join-Path $root $extra
    if (Test-Path $p) { Copy-Item $p $pkgDir -Force }
}
Copy-Item (Join-Path $src "*.*") (Join-Path $pkgDir "src") -Force

# Исходники установщика и деинсталлятора
$instSrc = Join-Path $root 'installer'
if (Test-Path $instSrc) {
    New-Item -ItemType Directory -Path (Join-Path $pkgDir "installer") | Out-Null
    Copy-Item (Join-Path $instSrc "*.*") (Join-Path $pkgDir "installer") -Force
}

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

