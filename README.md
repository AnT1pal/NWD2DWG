# ⚡ NWD2DWG v2.0

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows%20x64-0078D6.svg)](https://microsoft.com)
[![Navisworks](https://img.shields.io/badge/Navisworks-2020%20--%202026-0696D7.svg)](https://autodesk.com)
[![AutoCAD](https://img.shields.io/badge/AutoCAD-2018%20--%202026-E51B24.svg)](https://autodesk.com)
[![glTF 2.0](https://img.shields.io/badge/Format-glTF%20%2F%20GLB-green.svg)](https://www.khronos.org/gltf/)
[![IFC 2x3](https://img.shields.io/badge/Format-IFC%202x3-orange.svg)](https://technical.buildingsmart.org/)
[![Developer](https://img.shields.io/badge/Developer-BaidurovLabs-00A2FF.svg)](https://baidurovlabs.ru)

**NWD2DWG** — универсальный высокопроизводительный BIM-конвертер геометрии из моделей **Autodesk Navisworks (.NWD, .NWC, .NWF)** в форматы **AutoCAD (.DWG, .DXF)**, **glTF / GLB (Web, VR, Blender)** и **IFC 2x3 (BIM-координация)**.

Программа решает ключевую проблему инженеров и BIM-координаторов: Navisworks по умолчанию не позволяет выгружать реальную 3D-геометрию и атрибутику в открытые CAD/BIM форматы для дальнейшей работы в AutoCAD, NanoCAD, Revit, Blender, игровых движках или веб-вьюверах.

---

## 🚀 Топ-10 возможностей NWD2DWG v2.0

1. **🔥 Mesh Decimation (QEM-сжатие полигонов 0–90%):**
   Встроенный алгоритм Quadric Error Metrics (QEM) для управляемого упрощения полигональных сеток. Уменьшает вес сверхплотных моделей в 2–10 раз с сохранением характерных ребер и геометрии.

2. **🧊 Solid Reconstructor (Распознавание тел):**
   Автоматический PCA-анализ (метод главных компонент) и подгонка примитивов (трубы/цилиндры, балки/коробки) в чистые 3D-тела вместо тяжелых фасеточных сеток.

3. **📊 BIM Attribute Transfer (XData):**
   Полный перенос вкладок свойств и атрибутов Navisworks в расширенные данные сущностей AutoCAD (XData) и свойства IFC/glTF.

4. **🎯 Export by Selection Sets / Search Sets:**
   Экспорт строго определенных поисковых наборов и выборок (например, только технологические трубопроводы или только металлоконструкции).

5. **🌐 glTF 2.0 / GLB Export:**
   Прямая генерация бинарных `.glb` и текстовых `.gltf` файлов для интерактивной 3D-визуализации в браузере (Three.js, Babylon.js), движках (Unity, Unreal Engine) и Blender.

6. **🏛️ IFC 2x3 Export:**
   Генерация стандартных файлов IFC (ISO 10303-21 STEP) с иерархией проекта (`IfcProject` → `IfcSite` → `IfcBuilding` → `IfcBuildingStorey` → `IfcBuildingElementProxy`).

7. **✂️ Section Box Crop:**
   Обрезка экспортируемой геометрии по 3D-габаритам (bounding box / рамка сечения), что позволяет выгружать только нужный этаж или узел.

8. **🎨 Цвета ACI и PBR-материалы:**
   Полная совместимость с AutoCAD Color Index (ACI 1..255) в DXF, а также перенос прозрачности и физических материалов в glTF/IFC.

9. **🐕 BIM Watchdog (Автоконвертер папок):**
   Фоновая служба мониторинга директории (`--watch`). Автоматически подхватывает новые или обновленные `.nwd`/`.nwc` файлы и конвертирует их на лету.

10. **⚡ Multi-threaded Engine & Mesh Batching:**
    Параллельная многопоточная обработка и блочная запись геометрии. Снижает количество примитивов в чертеже на **99.7%** (с сотен тысяч фасетов до компактных блоков Polyface Mesh).

---

## 📥 Использование

### Графический интерфейс (GUI)
1. Скачайте релиз `NWD2DWG_v2.0.zip` из раздела **Releases**.
2. Запустите `NWD2DWG.exe`.
3. Перетащите файл или папку `.nwd`/`.nwc` в окно программы.
4. Настройте нужный формат (**DXF Polyface Mesh**, **DXF 3DFACE**, **DWG**, **glTF/GLB**, **IFC**) и параметры (степень сжатия, распознавание тел, перенос XData).
5. Нажмите **▶ Конвертировать**.

### Командная строка (CLI)

```powershell
# 1. Быстрая конвертация NWD в DXF Polyface со сжатием сетки на 50%
.\NWD2DWG.exe --convert "C:\Models\Plant.nwd" "C:\Out\Plant.dxf" --decimate 50 --split 1

# 2. Экспорт в бинарный glTF (GLB) с переносом материалов
.\NWD2DWG.exe --convert "C:\Models\Plant.nwd" "C:\Out\Plant.glb" --format glb --materials 1

# 3. Экспорт в IFC 2x3 с BIM-атрибутами и фильтрацией по выборке
.\NWD2DWG.exe --convert "C:\Models\Plant.nwd" "C:\Out\Plant.ifc" --format ifc --xdata 1 --sets "Трубопроводы,Оборудование"

# 4. Обрезка по габаритам Section Box (minX,minY,minZ,maxX,maxY,maxZ)
.\NWD2DWG.exe --convert "C:\Models\Plant.nwd" "C:\Out\Crop.dxf" --bbox 0,0,0,100,50,20

# 5. Режим BIM Watchdog — автоматический мониторинг папки
.\NWD2DWG.exe --watch "C:\BIM_DropFolder" --format glb --interval 5

# 6. Полный самотест всех 10 модулей
.\NWD2DWG.exe --selftest
```

---

## 🛠️ Справочник параметров CLI

| Параметр | Возможные значения | Описание |
|---|---|---|
| `--convert <in> <out>` | Пути к файлам | Основной режим конвертации файла |
| `--format` | `dxf`, `3dface`, `dwg`, `gltf`, `glb`, `ifc` | Формат вывода |
| `--decimate` | `0`–`90` | Процент QEM-упрощения полигонов |
| `--solid` / `--soliddetect` | `0` / `1` | Автоматическое распознавание тел (цилиндры/коробки) |
| `--xdata` | `0` / `1` | Перенос свойств элементов Navisworks в XData / BIM-атрибуты |
| `--materials` | `0` / `1` | Перенос прозрачности и физических материалов |
| `--sets` | `"Сет1,Сет2"` | Фильтр по Navisworks Selection Sets / Search Sets |
| `--bbox` | `minX,minY,minZ,maxX,maxY,maxZ` | 3D-рамка сечения (Section Box) |
| `--split` | `0` / `1` | Разбивать сводную модель на файлы по разделам (XREF) |
| `--skiphidden` | `0` / `1` | Пропускать скрытые элементы |
| `--colors` | `0` / `1` | Переносить цвета элементов (AutoCAD Color Index) |
| `--layers` | `0` / `1` | Создавать отдельный слой на каждый элемент |
| `--threads` | `0` (авто) / `1`..`N` | Количество потоков обработки |
| `--watch` | `<путь>` | Запуск службы фонового мониторинга директории |
| `--interval` | `<сек>` | Интервал опроса в режиме Watchdog (по умолчанию 5) |
| `--selftest` | `[директория]` | Запуск автономного самотестирования всех алгоритмов |
| `--diagnostics` | `[файл] [--no-api]` | Диагностика установленных Navisworks и AutoCAD |

---

## 💡 Советы по открытию в AutoCAD

- При открытии DXF-файла в AutoCAD используйте стандартную команду `_ZOOM _E` (Показать всё / Границы), так как модель может находиться в абсолютных проектных координатах (например, несколько километров от нуля).
- Для отображения 3D-модели переключите визуальный стиль с `2D Wireframe` на `Shaded with Edges` (Тонированный с кромками) или `Conceptual` (Концептуальный).
- При включенной опции «Разбивать по разделам» (`--split`) собирайте файлы через внешние ссылки AutoCAD (`_XREF`).

---

## 🏗️ Сборка из исходного кода

Сборка осуществляется через встроенный компилятор Roslyn (.NET SDK):

```powershell
powershell -ExecutionPolicy Bypass -File .\NWD2DWG\build.ps1
```

Готовые бинарные файлы и упакованный релиз с исходным кодом формируются в директории `dist/` (`NWD2DWG.exe`, `NWD2DWG.Plugin.dll`, `NWD2DWG_v2.0.zip`).

---

## 📄 Лицензия

Проект распространяется под свободной лицензией **GNU General Public License v3.0 (GPLv3)**. Подробнее см. в файле [LICENSE](LICENSE).

Разработчик: **Baidurov Pavel** / **BaidurovLabs**  
Официальный сайт: [baidurovlabs.ru](https://baidurovlabs.ru)  
GitHub: [github.com/AnT1pal/NWD2DWG](https://github.com/AnT1pal/NWD2DWG)