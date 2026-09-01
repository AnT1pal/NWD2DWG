// Удаление NWD2DWG.
//
// Работает по описи «установлено.txt», которую оставил рядом установщик:
// удаляет ровно то, что ставил, и ничего больше. Настройки и шаблоны
// пользователя лежат отдельно в %APPDATA% и по умолчанию остаются — их
// сносим только по явной галочке.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NWD2DWG.UninstallApp
{
    internal static class Program
    {
        internal const string AppName     = "NWD2DWG";
        internal const string DisplayName = "NWD2DWG — конвертер моделей Navisworks";
        internal const string RegPath     = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\NWD2DWG";
        internal const string EnvVar      = "NWD2DWG_EXE";

        [STAThread]
        static int Main(string[] argv)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool silent = false;
            foreach (string a in argv)
                if (a.Equals("/silent", StringComparison.OrdinalIgnoreCase) ||
                    a.Equals("/S", StringComparison.OrdinalIgnoreCase)) silent = true;

            string dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            string list = Path.Combine(dir, "установлено.txt");

            if (!File.Exists(list))
            {
                if (!silent)
                    MessageBox.Show(
                        "Рядом с деинсталлятором нет описи установленного (установлено.txt),\r\n" +
                        "поэтому удалять нечего — или файл потеряли.\r\n\r\n" +
                        "Папку можно удалить вручную:\r\n" + dir,
                        AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }

            if (!silent &&
                MessageBox.Show("Удалить " + DisplayName + "?\r\n\r\nПапка: " + dir,
                                AppName, MessageBoxButtons.YesNo,
                                MessageBoxIcon.Question) != DialogResult.Yes)
                return 2;

            bool dropSettings = false;
            string settings = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

            if (!silent && Directory.Exists(settings))
                dropSettings = MessageBox.Show(
                    "Удалить также настройки, шаблоны и ключи ИИ?\r\n\r\n" + settings +
                    "\r\n\r\nЕсли собираетесь ставить программу заново — оставьте.",
                    AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

            try
            {
                using (var f = new UninstallForm(dir, list, settings, dropSettings, silent))
                {
                    if (silent) { f.RunSilent(); return 0; }
                    Application.Run(f);
                }
            }
            catch (Exception ex)
            {
                if (!silent)
                    MessageBox.Show("Удаление не завершено: " + ex.Message, AppName,
                                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            return 0;
        }
    }

    internal class UninstallForm : Form
    {
        private readonly string _dir, _list, _settings;
        private readonly bool _dropSettings, _silent;

        private readonly ProgressBar _bar = new ProgressBar();
        private readonly ListBox _log = new ListBox();
        private readonly Button _close = new Button();

        internal UninstallForm(string dir, string list, string settings, bool dropSettings, bool silent)
        {
            _dir = dir; _list = list; _settings = settings;
            _dropSettings = dropSettings; _silent = silent;

            Text = "Удаление — " + Program.DisplayName;
            ClientSize = new Size(560, 340);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);
            BackColor = Color.White;

            _bar.Location = new Point(14, 14); _bar.Size = new Size(532, 20);
            _log.Location = new Point(14, 44); _log.Size = new Size(532, 240);
            _log.BorderStyle = BorderStyle.FixedSingle;
            _log.Font = new Font("Consolas", 8.5f);
            _log.IntegralHeight = false;

            _close.Text = "Закрыть"; _close.Size = new Size(96, 28);
            _close.Location = new Point(450, 296);
            _close.Enabled = false;
            _close.Click += (s, e) => Close();

            Controls.AddRange(new Control[] { _bar, _log, _close });
            Shown += (s, e) => { Application.DoEvents(); Run(); };
        }

        private void Say(string s)
        {
            if (_silent) return;
            _log.Items.Add(s);
            _log.TopIndex = _log.Items.Count - 1;
            Application.DoEvents();
        }

        internal void RunSilent() { Run(); }

        private void Run()
        {
            var files = new List<string>();
            var links = new List<string>();
            var menus = new List<string>();
            string regRoot = "HKCU", pathEntry = null;
            bool envVar = false;

            foreach (string raw in File.ReadAllLines(_list, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                int sp = line.IndexOf(' ');
                if (sp <= 0) continue;
                string tag = line.Substring(0, sp);
                string val = line.Substring(sp + 1).Trim();

                switch (tag)
                {
                    case "[файл]":       files.Add(Path.Combine(_dir, val)); break;
                    case "[ярлык]":      links.Add(val); break;
                    case "[меню]":       menus.Add(val); break;
                    case "[реестр]":     regRoot = val; break;
                    case "[переменная]": envVar = true; break;
                    case "[путь]":       pathEntry = val; break;
                }
            }

            if (!_silent) { _bar.Maximum = Math.Max(1, files.Count + 5); _bar.Value = 0; }

            // ---- файлы ----
            // Себя из списка исключаем: работающий exe удалить нельзя, а
            // сообщение об этом выглядело бы как сбой. Его сносит пакетный
            // файл в самом конце.
            string me = "";
            try { me = Path.GetFullPath(Assembly.GetExecutingAssembly().Location); } catch { }

            int failed = 0, deleted = 0, total = 0;
            foreach (string f in files)
            {
                Step();
                try
                {
                    if (me.Length > 0 && Path.GetFullPath(f).Equals(me, StringComparison.OrdinalIgnoreCase))
                        continue;
                    total++;
                    if (!File.Exists(f)) continue;
                    File.SetAttributes(f, FileAttributes.Normal);
                    File.Delete(f);
                    deleted++;
                }
                catch { failed++; Say("не удалось удалить: " + f); }
            }
            Say("Удалено файлов: " + deleted + " из " + total);

            // ---- ярлыки и меню ----
            foreach (string l in links)
                try { if (File.Exists(l)) File.Delete(l); } catch { }
            foreach (string m in menus)
                try { if (Directory.Exists(m)) Directory.Delete(m, true); } catch { }
            if (links.Count + menus.Count > 0) Say("Ярлыки убраны");
            Step();

            // ---- переменные среды ----
            if (envVar)
            {
                try
                {
                    Environment.SetEnvironmentVariable(Program.EnvVar, null, EnvironmentVariableTarget.User);
                    Say("Переменная " + Program.EnvVar + " снята");
                }
                catch { }
            }
            if (pathEntry != null) RemoveFromPath(pathEntry);
            Step();

            // ---- запись в списке программ ----
            try
            {
                RegistryKey root = regRoot == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
                root.DeleteSubKeyTree(Program.RegPath, false);
                Say("Запись в списке программ удалена");
            }
            catch { }
            Step();

            // ---- настройки пользователя ----
            if (_dropSettings)
            {
                try
                {
                    if (Directory.Exists(_settings)) { Directory.Delete(_settings, true); Say("Настройки удалены"); }
                }
                catch { }
            }
            else if (Directory.Exists(_settings))
                Say("Настройки и шаблоны оставлены: " + _settings);
            Step();

            // ---- пустые подпапки ----
            try
            {
                foreach (string d in Directory.GetDirectories(_dir, "*", SearchOption.AllDirectories))
                    TryRemoveEmpty(d);
            }
            catch { }
            Step();

            // Сам деинсталлятор и опись удалить на ходу нельзя — файл занят.
            // Их сносит короткий пакетный файл уже после нашего выхода.
            ScheduleSelfDelete();

            if (!_silent)
            {
                _bar.Value = _bar.Maximum;
                Say("");
                Say(failed == 0
                    ? "Готово. Программа удалена."
                    : "Готово, но " + failed + " файл(ов) удалить не удалось — возможно, они заняты.");
                _close.Enabled = true;
            }
        }

        private void Step()
        {
            if (_silent) return;
            if (_bar.Value < _bar.Maximum) _bar.Value++;
            Application.DoEvents();
        }

        private static void TryRemoveEmpty(string dir)
        {
            try
            {
                if (Directory.Exists(dir) &&
                    Directory.GetFiles(dir).Length == 0 &&
                    Directory.GetDirectories(dir).Length == 0)
                    Directory.Delete(dir);
            }
            catch { }
        }

        private static void RemoveFromPath(string dir)
        {
            try
            {
                string cur = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
                if (string.IsNullOrEmpty(cur)) return;

                var kept = new List<string>();
                foreach (string part in cur.Split(';'))
                {
                    if (part.Trim().Length == 0) continue;
                    if (part.Trim().TrimEnd('\\').Equals(dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        continue;
                    kept.Add(part);
                }
                Environment.SetEnvironmentVariable("PATH", string.Join(";", kept.ToArray()),
                                                   EnvironmentVariableTarget.User);
            }
            catch { }
        }

        private void ScheduleSelfDelete()
        {
            try
            {
                string me = Assembly.GetExecutingAssembly().Location;
                string bat = Path.Combine(Path.GetTempPath(), "nwd2dwg_uninstall_" +
                                          Guid.NewGuid().ToString("N").Substring(0, 8) + ".cmd");

                var sb = new StringBuilder();
                sb.AppendLine("@echo off");
                sb.AppendLine("ping 127.0.0.1 -n 4 >nul");            // дать процессу закрыться
                sb.AppendLine("del \"" + _list + "\" >nul 2>&1");
                sb.AppendLine("del \"" + me + "\" >nul 2>&1");
                sb.AppendLine("rd \"" + _dir + "\" >nul 2>&1");
                sb.AppendLine("del \"%~f0\" >nul 2>&1");

                // Пакетный файл читается в кодировке консоли, а не UTF-8:
                // в путях бывает кириллица (например, имя пользователя).
                File.WriteAllText(bat, sb.ToString(),
                                  Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage));

                Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + bat + "\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch { }
        }
    }
}
