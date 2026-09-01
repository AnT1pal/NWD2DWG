# Сборка установщика NWD2DWG.
#
# Делает две программы:
#   Удаление NWD2DWG.exe  — деинсталлятор, кладётся внутрь поставки
#   NWD2DWG_Setup_<версия>.exe — установщик со всей поставкой внутри
#
# Сторонние сборщики не нужны: всё компилируется Roslyn напрямую, как и
# остальной проект. Перед запуском соберите программу: build.ps1
param(
    [string]$Version = "3.5"
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$inst = Join-Path $root 'installer'
$stage = Join-Path $env:TEMP ("nwd2dwg_setup_" + [Guid]::NewGuid().ToString('N').Substring(0,8))
$refDir = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2'

Write-Host "--- Установщик NWD2DWG $Version ---"

# --- компилятор -------------------------------------------------------------
$csc = Get-ChildItem "$env:ProgramFiles\dotnet\sdk" -Recurse -Filter 'csc.dll' -ErrorAction SilentlyContinue |
       Sort-Object FullName -Descending | Select-Object -First 1
if (-not $csc) { throw "Не найден csc.dll — нужен .NET SDK" }
if (-not (Test-Path $refDir)) { throw "Не найдены reference assemblies .NET Framework 4.7.2: $refDir" }

$exeMain = Join-Path $dist 'NWD2DWG.exe'
if (-not (Test-Path $exeMain)) { throw "Нет $exeMain — сначала соберите программу: build.ps1" }

$icon = Join-Path $root 'src\NWD2DWG.ico'
$refsWin = @(
    "$refDir\mscorlib.dll", "$refDir\System.dll", "$refDir\System.Core.dll",
    "$refDir\System.Drawing.dll", "$refDir\System.Windows.Forms.dll",
    "$refDir\System.IO.Compression.dll", "$refDir\System.IO.Compression.FileSystem.dll",
    "$refDir\Microsoft.CSharp.dll", "$refDir\System.Xml.dll"
)
$refArgs = $refsWin | ForEach-Object { "/r:`"$_`"" }

New-Item -ItemType Directory -Force $stage | Out-Null

# --- 1. деинсталлятор -------------------------------------------------------
Write-Host "1. Деинсталлятор"
$uninstExe = Join-Path $stage 'Удаление NWD2DWG.exe'
$uninstArgs = @("`"$($csc.FullName)`"", '/nologo','/nostdlib+','/langversion:latest','/optimize+',
                '/target:winexe','/platform:anycpu','/warn:4','/utf8output',
                "/out:`"$uninstExe`"") + $refArgs
if (Test-Path $icon) { $uninstArgs += "/win32icon:`"$icon`"" }
$uninstArgs += "`"$(Join-Path $inst 'Uninstall.cs')`""
& dotnet $uninstArgs
if ($LASTEXITCODE -ne 0) { throw "Деинсталлятор не собрался (код $LASTEXITCODE)" }
Write-Host ("   {0} КБ" -f [math]::Round((Get-Item $uninstExe).Length/1KB))

# --- 2. состав поставки -----------------------------------------------------
Write-Host "2. Состав поставки"
$docs = Join-Path $stage 'Документация'
New-Item -ItemType Directory -Force $docs | Out-Null
New-Item -ItemType Directory -Force (Join-Path $stage 'mcp') | Out-Null

Copy-Item $exeMain (Join-Path $stage 'NWD2DWG.exe') -Force

$up = Split-Path $root -Parent      # C:\rhinodwg — там лежат PDF и проверка.ps1
$payloadDocs = @(
    @{ from = (Join-Path $up 'NWD2DWG_Руководство_пользователя.pdf'); to = 'Руководство_пользователя.pdf' },
    @{ from = (Join-Path $up 'Сценарии_использования.pdf');           to = 'Сценарии_использования.pdf' },
    @{ from = (Join-Path $root 'README.md');                          to = 'README.md' },
    @{ from = (Join-Path $root 'LICENSE');                            to = 'ЛИЦЕНЗИЯ.txt' }
)
foreach ($d in $payloadDocs) {
    if (Test-Path $d.from) {
        Copy-Item $d.from (Join-Path $docs $d.to) -Force
        Write-Host ("   Документация\{0}" -f $d.to)
    } else {
        Write-Host ("   ВНИМАНИЕ: нет {0} — в поставку не войдёт" -f $d.from)
    }
}

$mcpSrc = Join-Path $root 'mcp\nwd2dwg_mcp.py'
if (Test-Path $mcpSrc) { Copy-Item $mcpSrc (Join-Path $stage 'mcp\nwd2dwg_mcp.py') -Force; Write-Host "   mcp\nwd2dwg_mcp.py" }

$check = Join-Path $up 'проверка.ps1'
if (Test-Path $check) { Copy-Item $check (Join-Path $stage 'проверка.ps1') -Force; Write-Host "   проверка.ps1" }

# --- 3. упаковка ------------------------------------------------------------
Write-Host "3. Упаковка"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $env:TEMP ("nwd2dwg_payload_" + [Guid]::NewGuid().ToString('N').Substring(0,8) + ".zip")
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip,
    [IO.Compression.CompressionLevel]::Optimal, $false, [Text.Encoding]::UTF8)
$files = @(Get-ChildItem $stage -Recurse -File)
Write-Host ("   файлов {0}, архив {1} КБ" -f $files.Count, [math]::Round((Get-Item $zip).Length/1KB))

# --- 4. установщик ----------------------------------------------------------
Write-Host "4. Установщик"
$verFile = Join-Path $env:TEMP 'nwd2dwg_version.txt'
[IO.File]::WriteAllText($verFile, $Version, (New-Object Text.UTF8Encoding $false))

$licFile = Join-Path $env:TEMP 'nwd2dwg_license.txt'
$licSrc = Join-Path $root 'LICENSE'
if (Test-Path $licSrc) {
    [IO.File]::WriteAllText($licFile, [IO.File]::ReadAllText($licSrc), (New-Object Text.UTF8Encoding $false))
} else {
    [IO.File]::WriteAllText($licFile, "GNU General Public License v3", (New-Object Text.UTF8Encoding $false))
}

$setupExe = Join-Path $dist ("NWD2DWG_Setup_" + $Version + ".exe")
$setupArgs = @("`"$($csc.FullName)`"", '/nologo','/nostdlib+','/langversion:latest','/optimize+',
               '/target:winexe','/platform:anycpu','/warn:4','/utf8output',
               "/out:`"$setupExe`"") + $refArgs
$setupArgs += "/resource:`"$zip`",payload.zip"
$setupArgs += "/resource:`"$licFile`",LICENSE.txt"
$setupArgs += "/resource:`"$verFile`",version.txt"
if (Test-Path $icon) {
    $setupArgs += "/win32icon:`"$icon`""
    $setupArgs += "/resource:`"$icon`",app.ico"
}
$manifest = Join-Path $inst 'app.manifest'
if (Test-Path $manifest) { $setupArgs += "/win32manifest:`"$manifest`"" }
$setupArgs += "`"$(Join-Path $inst 'Setup.cs')`""

& dotnet $setupArgs
if ($LASTEXITCODE -ne 0) { throw "Установщик не собрался (код $LASTEXITCODE)" }

# --- уборка -----------------------------------------------------------------
Remove-Item $zip, $verFile, $licFile -Force -ErrorAction SilentlyContinue
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue

# Установщик докладываем в архив релиза: build.ps1 пакует его раньше, чем
# установщик собран, и без этого шага в архиве его бы не оказалось.
$zipFile = Join-Path $dist ("NWD2DWG_v" + $Version + ".zip")
if (Test-Path $zipFile) {
    $z = [IO.Compression.ZipFile]::Open($zipFile, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $name = Split-Path $setupExe -Leaf
        $old = @($z.Entries | Where-Object { $_.FullName -eq $name })
        foreach ($e in $old) { $e.Delete() }
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, $setupExe, $name) | Out-Null
    } finally { $z.Dispose() }
    Write-Host ("Установщик добавлен в архив релиза: {0}" -f $zipFile)
} else {
    Write-Host "ВНИМАНИЕ: архива релиза нет — соберите build.ps1, иначе установщик в него не попадёт"
}

Write-Host ""
Write-Host ("Готово: {0} ({1} КБ)" -f $setupExe, [math]::Round((Get-Item $setupExe).Length/1KB))
Write-Host "Проверить установку без последствий можно в любую пустую папку — деинсталлятор уберёт всё по описи."
