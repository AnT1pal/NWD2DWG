#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
nwd2dwg_mcp.py - MCP-сервер для управления NWD2DWG снаружи.

Что это даёт
------------
Программа умеет всё делать из командной строки. MCP превращает эту командную
строку в набор инструментов, которые может вызывать языковая модель или любой
другой клиент MCP: «сравни выдачу за прошлую неделю с сегодняшней»,
«покажи, сколько тонн металла в ведомости», «прогони модель по шаблону КМ».

Транспорт - stdio, протокол - JSON-RPC 2.0 по спецификации MCP.
Внешних зависимостей нет: только стандартная библиотека Python 3.8+.

Подключение (пример конфигурации клиента):

    {
      "mcpServers": {
        "nwd2dwg": {
          "command": "python",
          "args": ["C:/rhinodwg/NWD2DWG/mcp/nwd2dwg_mcp.py"],
          "env": { "NWD2DWG_EXE": "C:/rhinodwg/NWD2DWG/dist/NWD2DWG.exe" }
        }
      }
    }

Безопасность
------------
Инструменты разделены на читающие и изменяющие. Изменяющие помечены
destructiveHint, чтобы клиент спрашивал подтверждение. Запуск произвольных
команд не предусмотрен: сервер вызывает только NWD2DWG.exe с ключами из
закрытого списка.
"""

import json
import os
import subprocess
import sys
import glob
import time

PROTOCOL_VERSION = "2024-11-05"
SERVER_NAME = "nwd2dwg"
SERVER_VERSION = "3.5"

# Ключи конвертации, которые сервер разрешает передавать. Список закрытый:
# он же служит документацией для модели и защитой от произвольных аргументов.
FLAGS_BOOL = [
    "skiphidden", "colors", "layers", "split", "solid", "xdata", "materials",
    "geoshift", "grids", "pipes", "boq", "bcf", "anonymize", "clash-cluster",
    "section-plan", "purge", "penetrations", "clearance", "steel", "cog",
    "iso", "4d", "shrinkwrap", "room-finish", "visible", "acadvisible",
]
FLAGS_VALUE = [
    "format", "decimate", "sets", "threads", "preset", "schedule",
    "clash-eps", "clash-minpts", "clash-tol", "bcf-author",
    "sched-source", "sched-date", "min-headroom", "clearance-cell",
    "section-z", "section-eps", "section-layer",
    "room-min-area", "room-max-area", "room-height",
    "pipe-dn-min", "pipe-dn-max", "pipe-min-len",
    "sleeve-gap", "sleeve-ext", "sleeve-min-thk",
    "steel-tol", "steel-custom", "steel-min-len",
    "density-steel", "density-concrete", "density-piping",
    "cog-min-mass", "decimate-min-tris", "solid-confidence",
    "shrink-lvl", "boq-group",
]

BUILTIN_PRESETS = [
    "Жилые и общественные здания",
    "Производственные здания и цеха",
    "Технологические трубопроводы",
    "Металлоконструкции КМ / КМД",
    "Наружные сети и генплан",
    "Обмерные и сканированные модели",
    "Выдача: комплект по СПДС",
    "Выдача: только ведомости сметчику",
    "Выдача: плоская, для скриптов",
]


# ---------------------------------------------------------------------------
# Расположение файлов
# ---------------------------------------------------------------------------
def exe_path():
    p = os.environ.get("NWD2DWG_EXE")
    if p and os.path.isfile(p):
        return p
    here = os.path.dirname(os.path.abspath(__file__))
    for rel in ("../dist/NWD2DWG.exe", "../../dist/NWD2DWG.exe", "NWD2DWG.exe"):
        cand = os.path.normpath(os.path.join(here, rel))
        if os.path.isfile(cand):
            return cand
    return None


def appdata_dir():
    base = os.environ.get("APPDATA") or os.path.expanduser("~")
    return os.path.join(base, "NWD2DWG")


# ---------------------------------------------------------------------------
# Запуск программы
# ---------------------------------------------------------------------------
def console_encodings():
    """Консольный вывод .NET идёт в кодировке OEM (на русской Windows cp866),
    а не в UTF-8. Порядок подбора: сначала OEM, затем обычные варианты."""
    encs = []
    try:
        import ctypes
        encs.append("cp%d" % ctypes.windll.kernel32.GetOEMCP())
    except Exception:
        encs.append("cp866")
    encs += ["utf-8", "cp1251"]
    return encs


def dec(data):
    if not data:
        return ""
    for enc in console_encodings():
        try:
            return data.decode(enc)
        except (UnicodeDecodeError, LookupError):
            continue
    return data.decode("utf-8", "replace")


def run_exe(args, timeout=1800):
    exe = exe_path()
    if not exe:
        return 127, "", ("NWD2DWG.exe не найден. Укажите путь в переменной "
                         "окружения NWD2DWG_EXE.")
    try:
        started = time.time()
        pr = subprocess.run([exe] + args, capture_output=True, timeout=timeout)
        out = dec(pr.stdout)
        err = dec(pr.stderr)
        out += "\n[выполнено за %.1f с, код возврата %d]" % (
            time.time() - started, pr.returncode)
        return pr.returncode, out, err
    except subprocess.TimeoutExpired:
        return 124, "", "превышено время ожидания (%d с)" % timeout
    except Exception as ex:
        return 1, "", "%s: %s" % (type(ex).__name__, ex)


def tail(text, limit=20000):
    if len(text) <= limit:
        return text
    return "...(начало опущено)...\n" + text[-limit:]


def with_err(out, err):
    return tail(out) + (("\nSTDERR: " + tail(err, 4000)) if err.strip() else "")


def read_text(path, max_bytes=200000):
    """Кодировка отчётов зависит от профиля выдачи, поэтому подбираем её."""
    for enc in ("utf-8-sig", "cp1251", "utf-8"):
        try:
            with open(path, "rb") as f:
                data = f.read(max_bytes + 1)
            more = len(data) > max_bytes
            txt = data[:max_bytes].decode(enc)
            if more:
                txt += "\n...(файл обрезан)..."
            return txt
        except (UnicodeDecodeError, LookupError):
            continue
        except OSError as ex:
            return "не удалось прочитать: %s" % ex
    return "не удалось определить кодировку файла"


# ---------------------------------------------------------------------------
# Описания инструментов
# ---------------------------------------------------------------------------
def S(**props):
    return props


TOOLS = [
    {
        "name": "list_presets",
        "description": ("Список шаблонов настроек: встроенные отраслевые и "
                        "пользовательские из %APPDATA%\\NWD2DWG\\presets. "
                        "Имя шаблона передаётся в convert как preset."),
        "inputSchema": {"type": "object", "properties": {}},
        "annotations": {"readOnlyHint": True},
    },
    {
        "name": "list_settings",
        "description": ("Текущие сохранённые настройки: расширенные параметры "
                        "расчёта и профиль выдачи, с именами полей и значениями."),
        "inputSchema": {"type": "object", "properties": {}},
        "annotations": {"readOnlyHint": True},
    },
    {
        "name": "probe",
        "description": ("Разведка модели без конвертации: состав, число элементов, "
                        "габариты. Быстрый способ понять, с чем предстоит работать."),
        "inputSchema": {
            "type": "object",
            "properties": S(model=S(type="string",
                                    description="путь к .nwd/.nwf/.nwc")),
            "required": ["model"],
        },
        "annotations": {"readOnlyHint": True},
    },
    {
        "name": "convert",
        "description": ("Конвертация модели и запуск расчётных модулей. "
                        "Требует установленного Navisworks. Операция длительная "
                        "(минуты) и создаёт файлы на диске."),
        "inputSchema": {
            "type": "object",
            "properties": S(
                model=S(type="string", description="исходная модель Navisworks"),
                output=S(type="string",
                         description="файл или папка результата (необязательно)"),
                preset=S(type="string", description="имя шаблона настроек"),
                flags=S(type="array", items=S(type="string"),
                        description=("ключи-переключатели, например boq, cog, steel, "
                                     "section-plan. Допустимые: " + ", ".join(FLAGS_BOOL))),
                options=S(type="object",
                          description=("ключи со значением, например "
                                       "{\"format\":\"dxf\",\"steel-tol\":\"3\"}. "
                                       "Допустимые: " + ", ".join(FLAGS_VALUE))),
                timeout_sec=S(type="integer",
                              description="предел ожидания, по умолчанию 1800"),
            ),
            "required": ["model"],
        },
        "annotations": {"destructiveHint": True, "readOnlyHint": False},
    },
    {
        "name": "diff_index",
        "description": ("Сравнение двух выдач по индексам ревизий. Navisworks и "
                        "исходные модели не нужны - достаточно двух файлов _index.csv. "
                        "Отвечает на вопрос «что изменилось с прошлой выдачи»."),
        "inputSchema": {
            "type": "object",
            "properties": S(
                old_index=S(type="string", description="индекс предыдущей выдачи"),
                new_index=S(type="string", description="индекс текущей выдачи"),
                report=S(type="string",
                         description="куда положить отчёт (необязательно)"),
            ),
            "required": ["old_index", "new_index"],
        },
        "annotations": {"readOnlyHint": False},
    },
    {
        "name": "delivery_log",
        "description": ("Журнал выдач по объекту: когда, из какой модели "
                        "и что было выдано."),
        "inputSchema": {
            "type": "object",
            "properties": S(path=S(type="string",
                                   description="путь к файлу журнала выдач")),
            "required": ["path"],
        },
        "annotations": {"readOnlyHint": True},
    },
    {
        "name": "read_output",
        "description": ("Чтение выданного файла: протокола, ведомости, отчёта об "
                        "изменениях. Кодировка определяется автоматически."),
        "inputSchema": {
            "type": "object",
            "properties": S(
                path=S(type="string", description="путь к файлу"),
                max_bytes=S(type="integer",
                            description="ограничение объёма, по умолчанию 200000"),
            ),
            "required": ["path"],
        },
        "annotations": {"readOnlyHint": True},
    },
    {
        "name": "list_outputs",
        "description": ("Состав папки выдачи с размерами и датами. Понимает "
                        "структуру по разделам (01_Модель, 02_Ведомости и далее)."),
        "inputSchema": {
            "type": "object",
            "properties": S(
                folder=S(type="string", description="папка выдачи"),
                pattern=S(type="string", description="маска, по умолчанию *"),
            ),
            "required": ["folder"],
        },
        "annotations": {"readOnlyHint": True},
    },
    {
        "name": "diagnostics",
        "description": ("Проверка окружения: найден ли Navisworks, AutoCAD, "
                        "плагин, права на запись. С чего начинать разбор проблем."),
        "inputSchema": {
            "type": "object",
            "properties": S(model=S(type="string",
                                    description="модель для проверки (необязательно)")),
        },
        "annotations": {"readOnlyHint": True},
    },
    {
        "name": "selftest",
        "description": ("Самотест расчётных модулей без Navisworks. "
                        "Возвращает отчёт по блокам."),
        "inputSchema": {"type": "object", "properties": {}},
        "annotations": {"readOnlyHint": True},
    },
]


# ---------------------------------------------------------------------------
# Реализация инструментов
# ---------------------------------------------------------------------------
def t_list_presets(_a):
    lines = ["Встроенные шаблоны:"]
    lines += ["  - " + n for n in BUILTIN_PRESETS]
    d = os.path.join(appdata_dir(), "presets")
    user = sorted(glob.glob(os.path.join(d, "*.json"))) if os.path.isdir(d) else []
    lines.append("")
    if user:
        lines.append("Пользовательские шаблоны (%s):" % d)
        for f in user:
            lines.append("  - " + os.path.splitext(os.path.basename(f))[0])
    else:
        lines.append("Пользовательских шаблонов нет (%s)" % d)
    return "\n".join(lines)


def settings_path(fn):
    """Параметры расчёта могут лежать рядом с программой — так носят
    переносную сборку. Этот вариант имеет приоритет, как и в самой программе."""
    exe = exe_path()
    if exe:
        local = os.path.join(os.path.dirname(exe), fn)
        if os.path.isfile(local):
            return local
    return os.path.join(appdata_dir(), fn)


def t_list_settings(_a):
    out = []
    for fn, title in (("settings.json", "Расширенные параметры расчёта"),
                      ("output.json", "Профиль выдачи"),
                      ("ai.json", "Настройки ИИ-помощника")):
        p = settings_path(fn)
        out.append("=== %s (%s) ===" % (title, p))
        out.append(read_text(p, 60000) if os.path.isfile(p)
                   else "файл не создан, действуют значения по умолчанию")
        out.append("")
    return "\n".join(out)


def t_probe(a):
    model = a.get("model", "")
    if not model:
        return "не указана модель"
    code, out, err = run_exe(["--probe", model], timeout=600)
    return with_err(out, err)


def t_convert(a):
    model = a.get("model", "")
    if not model:
        return "не указана модель"
    if not os.path.isfile(model):
        return "модель не найдена: " + model

    args = ["--convert", model]
    out_path = a.get("output")
    if out_path:
        args.append(out_path)

    preset = a.get("preset")
    if preset:
        args += ["--preset", preset]

    for f in a.get("flags") or []:
        f = str(f).lstrip("-")
        if f not in FLAGS_BOOL:
            return "недопустимый ключ: %s\nдопустимы: %s" % (f, ", ".join(FLAGS_BOOL))
        args.append("--" + f)

    for k, v in (a.get("options") or {}).items():
        k = str(k).lstrip("-")
        if k not in FLAGS_VALUE:
            return "недопустимый параметр: %s\nдопустимы: %s" % (k, ", ".join(FLAGS_VALUE))
        args += ["--" + k, str(v)]

    code, out, err = run_exe(args, timeout=int(a.get("timeout_sec") or 1800))
    res = with_err(out, err)
    if code != 0:
        res = "Конвертация завершилась с ошибкой (код %d).\n%s" % (code, res)
    return res


def t_diff_index(a):
    args = ["--diff-index", a.get("old_index", ""), a.get("new_index", "")]
    if a.get("report"):
        args.append(a["report"])
    code, out, err = run_exe(args, timeout=300)
    return with_err(out, err)


def t_delivery_log(a):
    code, out, err = run_exe(["--delivery-log", a.get("path", "")], timeout=120)
    return with_err(out, err)


def t_read_output(a):
    p = a.get("path", "")
    if not os.path.isfile(p):
        return "файл не найден: " + p
    return read_text(p, int(a.get("max_bytes") or 200000))


def t_list_outputs(a):
    folder = a.get("folder", "")
    if not os.path.isdir(folder):
        return "папка не найдена: " + folder
    pattern = a.get("pattern") or "*"
    rows, total = [], 0
    for path in sorted(glob.glob(os.path.join(folder, "**", pattern), recursive=True)):
        if os.path.isdir(path):
            continue
        try:
            st = os.stat(path)
        except OSError:
            continue
        total += st.st_size
        rows.append("%10.1f KB  %s  %s" % (
            st.st_size / 1024.0,
            time.strftime("%Y-%m-%d %H:%M", time.localtime(st.st_mtime)),
            os.path.relpath(path, folder)))
    if not rows:
        return "файлов не найдено"
    count = len(rows)
    rows.append("")
    rows.append("итого файлов %d, объём %.1f МБ" % (count, total / 1048576.0))
    return "\n".join(rows)


def t_diagnostics(a):
    args = ["--diagnostics"]
    if a.get("model"):
        args.append(a["model"])
    code, out, err = run_exe(args, timeout=300)
    return with_err(out, err)


def t_selftest(_a):
    code, out, err = run_exe(["--selftest"], timeout=600)
    return with_err(out, err)


HANDLERS = {
    "list_presets": t_list_presets,
    "list_settings": t_list_settings,
    "probe": t_probe,
    "convert": t_convert,
    "diff_index": t_diff_index,
    "delivery_log": t_delivery_log,
    "read_output": t_read_output,
    "list_outputs": t_list_outputs,
    "diagnostics": t_diagnostics,
    "selftest": t_selftest,
}


# ---------------------------------------------------------------------------
# Протокол JSON-RPC поверх stdio
# ---------------------------------------------------------------------------
def send(msg):
    sys.stdout.write(json.dumps(msg, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def handle(req):
    method = req.get("method", "")
    rid = req.get("id")
    params = req.get("params") or {}

    if method == "initialize":
        return {
            "jsonrpc": "2.0", "id": rid,
            "result": {
                "protocolVersion": PROTOCOL_VERSION,
                "capabilities": {"tools": {}},
                "serverInfo": {"name": SERVER_NAME, "version": SERVER_VERSION},
                "instructions": (
                    "Управление NWD2DWG - конвертером моделей Navisworks в DWG/DXF "
                    "с расчётными модулями. Начните с probe или diagnostics, "
                    "затем list_presets, затем convert. Готовые файлы читаются "
                    "через read_output, изменения между выдачами - через diff_index."
                ),
            },
        }

    if method in ("notifications/initialized", "initialized"):
        return None

    if method == "tools/list":
        return {"jsonrpc": "2.0", "id": rid, "result": {"tools": TOOLS}}

    if method == "tools/call":
        name = params.get("name", "")
        args = params.get("arguments") or {}
        fn = HANDLERS.get(name)
        if not fn:
            return {"jsonrpc": "2.0", "id": rid,
                    "error": {"code": -32601,
                              "message": "неизвестный инструмент: " + name}}
        try:
            text = fn(args)
            is_err = False
        except Exception as ex:
            text = "%s: %s" % (type(ex).__name__, ex)
            is_err = True
        return {"jsonrpc": "2.0", "id": rid,
                "result": {"content": [{"type": "text", "text": text}],
                           "isError": is_err}}

    if method == "ping":
        return {"jsonrpc": "2.0", "id": rid, "result": {}}

    if rid is None:
        return None
    return {"jsonrpc": "2.0", "id": rid,
            "error": {"code": -32601, "message": "метод не поддерживается: " + method}}


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "--check":
        exe = exe_path()
        print("NWD2DWG.exe:  " + (exe or "НЕ НАЙДЕН"))
        print("настройки:    " + appdata_dir())
        print("инструментов: %d" % len(TOOLS))
        missing = [t["name"] for t in TOOLS if t["name"] not in HANDLERS]
        print("без обработчика: " + (", ".join(missing) if missing else "нет"))
        return 0 if exe and not missing else 1

    try:
        sys.stdin.reconfigure(encoding="utf-8")
        sys.stdout.reconfigure(encoding="utf-8")
    except AttributeError:
        pass

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
        except ValueError:
            send({"jsonrpc": "2.0", "id": None,
                  "error": {"code": -32700, "message": "разбор JSON не удался"}})
            continue
        resp = handle(req)
        if resp is not None:
            send(resp)
    return 0


if __name__ == "__main__":
    sys.exit(main())
