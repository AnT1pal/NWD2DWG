// Мастер установки NWD2DWG.
//
// Отдельная маленькая программа: несёт в себе всю поставку одним zip-ресурсом,
// раскладывает её в выбранную папку, заводит запись в «Установка и удаление
// программ» и кладёт рядом деинсталлятор со списком того, что поставила.
//
// Никаких сторонних сборщиков (Inno Setup, WiX) — как и всё в проекте,
// собирается компилятором Roslyn напрямую.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NWD2DWG.SetupApp
{
    internal static class Program
    {
        internal const string AppName     = "NWD2DWG";
        internal const string DisplayName = "NWD2DWG — конвертер моделей Navisworks";
        internal const string Publisher   = "BaidurovLabs";
        internal const string Site        = "https://baidurovlabs.ru";
        internal const string RegPath     = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\NWD2DWG";
        internal const string EnvVar      = "NWD2DWG_EXE";

        internal static string Version = "3.5";

        [STAThread]
        static int Main(string[] argv)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Version = Res.Text("version.txt", "3.5").Trim();

            // /dir= приходит от самого себя при перезапуске с правами администратора
            // и от тихой установки: NWD2DWG_Setup.exe /silent /dir="C:\..." —
            // так программу раскатывают на парк машин без участия человека.
            string dir = null;
            bool silent = false, noShortcuts = false;
            foreach (string a in argv)
            {
                if (a.StartsWith("/dir=", StringComparison.OrdinalIgnoreCase))
                    dir = a.Substring(5).Trim('"');
                else if (a.Equals("/silent", StringComparison.OrdinalIgnoreCase) ||
                         a.Equals("/S", StringComparison.OrdinalIgnoreCase))
                    silent = true;
                else if (a.Equals("/noshortcuts", StringComparison.OrdinalIgnoreCase))
                    noShortcuts = true;
                else if (a.Equals("/?", StringComparison.Ordinal) ||
                         a.Equals("/help", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(
                        "Установка NWD2DWG\r\n\r\n" +
                        "Без ключей — обычная установка с мастером.\r\n\r\n" +
                        "  /silent           установить без вопросов\r\n" +
                        "  /dir=\"путь\"       куда установить\r\n" +
                        "  /noshortcuts      не создавать ярлыки\r\n\r\n" +
                        "Тихая установка означает согласие с лицензией GPL v3.\r\n" +
                        "Отчёт пишется в %TEMP%\\NWD2DWG_setup.log",
                        "NWD2DWG", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }
            }

            try
            {
                if (silent)
                {
                    using (var f = new SetupForm(dir, true, !noShortcuts)) return f.RunSilent();
                }
                using (var f = new SetupForm(dir)) Application.Run(f);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Установка прервана: " + ex.Message, "NWD2DWG",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            return 0;
        }

        internal static bool IsAdmin()
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    /// <summary>Доступ к вложенным в установщик ресурсам.</summary>
    internal static class Res
    {
        internal static Stream Open(string name)
        {
            return Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        }

        internal static string Text(string name, string fallback)
        {
            try
            {
                using (Stream s = Open(name))
                {
                    if (s == null) return fallback;
                    using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
                }
            }
            catch { return fallback; }
        }

        internal static Icon AppIcon()
        {
            try
            {
                using (Stream s = Open("app.ico")) { if (s != null) return new Icon(s); }
            }
            catch { }
            return null;
        }
    }

    internal class SetupForm : Form
    {
        // ---- страницы мастера ----
        private const int PWelcome = 0, PLicense = 1, PFolder = 2, POptions = 3, PInstall = 4, PDone = 5;
        private int _page = PWelcome;

        private readonly Panel _head = new Panel();
        private readonly Label _title = new Label();
        private readonly Label _subtitle = new Label();
        private readonly Panel _body = new Panel();
        private readonly Panel _foot = new Panel();
        private readonly Button _back = new Button();
        private readonly Button _next = new Button();
        private readonly Button _cancel = new Button();

        private readonly Panel[] _pages = new Panel[6];

        // ---- выбор пользователя ----
        private TextBox _tbDir;
        private Label _lbSpace;
        private RadioButton _rbAccept, _rbDecline;
        private CheckBox _cbDesktop, _cbStartMenu, _cbEnv, _cbPath;
        private CheckBox _cbRun, _cbManual;
        private ProgressBar _bar;
        private ListBox _log;
        private Label _lbDone;

        private bool _installed;
        private string _installedDir;
        private readonly string _forcedDir;
        private readonly bool _silent;
        private readonly bool _wantShortcuts;
        private readonly List<string> _logLines = new List<string>();

        internal SetupForm(string forcedDir) : this(forcedDir, false, true) { }

        internal SetupForm(string forcedDir, bool silent, bool wantShortcuts)
        {
            _forcedDir = forcedDir;
            _silent = silent;
            _wantShortcuts = wantShortcuts;

            Text = "Установка — " + Program.DisplayName;
            Icon = Res.AppIcon();
            ClientSize = new Size(640, 470);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);
            BackColor = Color.White;

            BuildChrome();
            BuildPages();
            Show(PWelcome);
        }

        // ------------------------------------------------------------------
        private void BuildChrome()
        {
            _head.Dock = DockStyle.Top;
            _head.Height = 62;
            _head.BackColor = Color.White;
            _head.Paint += (s, e) =>
                e.Graphics.DrawLine(SystemPens.ControlDark, 0, _head.Height - 1, _head.Width, _head.Height - 1);

            _title.Font = new Font("Segoe UI Semibold", 11f, FontStyle.Bold);
            _title.Location = new Point(20, 12);
            _title.AutoSize = true;

            _subtitle.ForeColor = Color.FromArgb(90, 90, 90);
            _subtitle.Location = new Point(22, 34);
            _subtitle.Size = new Size(600, 20);

            _head.Controls.Add(_title);
            _head.Controls.Add(_subtitle);

            _body.Dock = DockStyle.Fill;
            _body.BackColor = Color.White;
            _body.Padding = new Padding(20, 14, 20, 6);

            _foot.Dock = DockStyle.Bottom;
            _foot.Height = 56;
            _foot.BackColor = SystemColors.Control;
            _foot.Paint += (s, e) => e.Graphics.DrawLine(SystemPens.ControlDark, 0, 0, _foot.Width, 0);

            _back.Text = "Назад";      _back.Size = new Size(96, 28);   _back.Location = new Point(330, 14);
            _next.Text = "Далее";      _next.Size = new Size(96, 28);   _next.Location = new Point(432, 14);
            _cancel.Text = "Отмена";   _cancel.Size = new Size(96, 28); _cancel.Location = new Point(534, 14);

            _back.Click += (s, e) => Back();
            _next.Click += (s, e) => Next();
            _cancel.Click += (s, e) => CancelSetup();

            _foot.Controls.AddRange(new Control[] { _back, _next, _cancel });

            Controls.Add(_body);
            Controls.Add(_head);
            Controls.Add(_foot);
        }

        private Panel NewPage()
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Visible = false };
            _body.Controls.Add(p);
            return p;
        }

        private void BuildPages()
        {
            BuildWelcome();
            BuildLicense();
            BuildFolder();
            BuildOptions();
            BuildInstall();
            BuildDone();
        }

        // ---- 0. приветствие ----------------------------------------------
        private void BuildWelcome()
        {
            var p = _pages[PWelcome] = NewPage();

            var l = new Label
            {
                Location = new Point(4, 6),
                Size = new Size(580, 300),
                Text =
                    "Программа переводит модели Navisworks (.nwd, .nwc, .nwf) в чертёжные и " +
                    "обменные форматы: DXF, DWG, IFC, glTF — и собирает по модели ведомости, " +
                    "спецификации и протоколы выдачи по российским нормам.\r\n\r\n" +
                    "Будет установлено:\r\n" +
                    "     •  NWD2DWG — сама программа (окно и командная строка)\r\n" +
                    "     •  Руководство пользователя и сценарии работы в PDF\r\n" +
                    "     •  MCP-сервер для управления программой извне\r\n" +
                    "     •  Скрипт самопроверки и деинсталлятор\r\n\r\n" +
                    "Для работы нужен установленный Autodesk Navisworks Manage или Simulate " +
                    "версий 2020–2026. Сама установка Navisworks не требует.\r\n\r\n" +
                    "Перед продолжением закройте окна NWD2DWG, если они открыты."
            };
            p.Controls.Add(l);

            // Уже установленную версию находим заранее и честно об этом говорим.
            string prev = FindInstalled();
            if (prev != null)
            {
                var warn = new Label
                {
                    Location = new Point(4, 316),
                    Size = new Size(580, 44),
                    ForeColor = Color.FromArgb(150, 90, 0),
                    Text = "Найдена установленная версия в папке:\r\n" + prev +
                           "\r\nОна будет обновлена — настройки и шаблоны сохранятся."
                };
                p.Controls.Add(warn);
            }
        }

        // ---- 1. лицензия --------------------------------------------------
        private void BuildLicense()
        {
            var p = _pages[PLicense] = NewPage();

            var tb = new TextBox
            {
                Location = new Point(4, 4),
                Size = new Size(586, 268),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 8.5f),
                BackColor = Color.White,
                Text = Res.Text("LICENSE.txt", "Текст лицензии не найден.").Replace("\n", "\r\n").Replace("\r\r", "\r")
            };

            var hint = new Label
            {
                Location = new Point(4, 278),
                Size = new Size(586, 34),
                ForeColor = Color.FromArgb(90, 90, 90),
                Text = "GNU General Public License v3. Программу можно свободно использовать, изучать, " +
                       "изменять и передавать дальше — вместе с исходным кодом."
            };

            _rbAccept  = new RadioButton { Location = new Point(4, 316), Size = new Size(400, 22),
                                           Text = "Я принимаю условия лицензионного соглашения" };
            _rbDecline = new RadioButton { Location = new Point(4, 340), Size = new Size(400, 22),
                                           Text = "Я не принимаю условия соглашения", Checked = true };

            _rbAccept.CheckedChanged += (s, e) => _next.Enabled = _rbAccept.Checked;

            p.Controls.AddRange(new Control[] { tb, hint, _rbAccept, _rbDecline });
        }

        // ---- 2. папка установки -------------------------------------------
        private void BuildFolder()
        {
            var p = _pages[PFolder] = NewPage();

            var l = new Label
            {
                Location = new Point(4, 6), Size = new Size(586, 40),
                Text = "Программа будет установлена в указанную папку. Чтобы выбрать другую, " +
                       "нажмите «Обзор»."
            };

            _tbDir = new TextBox { Location = new Point(4, 54), Size = new Size(480, 24),
                                   Text = string.IsNullOrEmpty(_forcedDir) ? DefaultDir() : _forcedDir };
            _tbDir.TextChanged += (s, e) => UpdateSpace();

            var browse = new Button { Location = new Point(492, 53), Size = new Size(98, 26), Text = "Обзор..." };
            browse.Click += (s, e) =>
            {
                using (var d = new FolderBrowserDialog())
                {
                    d.Description = "Куда установить NWD2DWG";
                    d.SelectedPath = SafeExistingPart(_tbDir.Text);
                    if (d.ShowDialog(this) == DialogResult.OK)
                        _tbDir.Text = Path.Combine(d.SelectedPath, Program.AppName);
                }
            };

            _lbSpace = new Label { Location = new Point(4, 90), Size = new Size(586, 40),
                                   ForeColor = Color.FromArgb(90, 90, 90) };

            var note = new Label
            {
                Location = new Point(4, 140), Size = new Size(586, 96),
                ForeColor = Color.FromArgb(90, 90, 90),
                Text = Program.IsAdmin()
                    ? "Установка идёт с правами администратора — программа будет доступна всем " +
                      "пользователям компьютера."
                    : "Установка идёт для текущего пользователя и прав администратора не требует.\r\n\r\n" +
                      "Если выбрать папку в «Program Files», понадобятся права администратора — " +
                      "программа предложит перезапуститься."
            };

            p.Controls.AddRange(new Control[] { l, _tbDir, browse, _lbSpace, note });
            UpdateSpace();
        }

        // ---- 3. дополнительно ---------------------------------------------
        private void BuildOptions()
        {
            var p = _pages[POptions] = NewPage();

            _cbDesktop   = new CheckBox { Location = new Point(4, 10),  Size = new Size(560, 22),
                                          Text = "Создать ярлык на рабочем столе", Checked = true };
            _cbStartMenu = new CheckBox { Location = new Point(4, 38),  Size = new Size(560, 22),
                                          Text = "Создать папку в меню «Пуск» (программа, руководство, удаление)", Checked = true };
            _cbEnv       = new CheckBox { Location = new Point(4, 66),  Size = new Size(560, 22),
                                          Text = "Завести переменную среды NWD2DWG_EXE с путём к программе", Checked = true };
            _cbPath      = new CheckBox { Location = new Point(4, 94),  Size = new Size(560, 22),
                                          Text = "Добавить папку установки в PATH (вызов nwd2dwg из любой консоли)" };

            var note = new Label
            {
                Location = new Point(4, 130), Size = new Size(586, 150),
                ForeColor = Color.FromArgb(90, 90, 90),
                Text =
                    "Переменная NWD2DWG_EXE нужна MCP-серверу и сценариям автоматизации: они " +
                    "находят программу по ней, не завися от места установки.\r\n\r\n" +
                    "PATH пригодится, если планируете вызывать конвертацию из скриптов и " +
                    "планировщика задач. Обе настройки заводятся для текущего пользователя " +
                    "и снимаются при удалении программы."
            };

            p.Controls.AddRange(new Control[] { _cbDesktop, _cbStartMenu, _cbEnv, _cbPath, note });
        }

        // ---- 4. установка --------------------------------------------------
        private void BuildInstall()
        {
            var p = _pages[PInstall] = NewPage();

            _bar = new ProgressBar { Location = new Point(4, 10), Size = new Size(586, 20) };
            _log = new ListBox
            {
                Location = new Point(4, 42), Size = new Size(586, 320),
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 8.5f),
                IntegralHeight = false
            };

            p.Controls.AddRange(new Control[] { _bar, _log });
        }

        // ---- 5. готово ------------------------------------------------------
        private void BuildDone()
        {
            var p = _pages[PDone] = NewPage();

            _lbDone = new Label { Location = new Point(4, 6), Size = new Size(586, 130) };

            _cbRun    = new CheckBox { Location = new Point(4, 150), Size = new Size(560, 22),
                                       Text = "Запустить NWD2DWG", Checked = true };
            _cbManual = new CheckBox { Location = new Point(4, 178), Size = new Size(560, 22),
                                       Text = "Открыть руководство пользователя" };

            p.Controls.AddRange(new Control[] { _lbDone, _cbRun, _cbManual });
        }

        // ------------------------------------------------------------------
        private void Show(int page)
        {
            _page = page;
            for (int i = 0; i < _pages.Length; i++)
                if (_pages[i] != null) _pages[i].Visible = (i == page);

            switch (page)
            {
                case PWelcome:
                    _title.Text = "Установка " + Program.AppName + " " + Program.Version;
                    _subtitle.Text = "Конвертер моделей Navisworks в чертежи и ведомости";
                    _back.Enabled = false; _next.Enabled = true; _next.Text = "Далее";
                    break;
                case PLicense:
                    _title.Text = "Лицензионное соглашение";
                    _subtitle.Text = "Прочитайте условия перед установкой";
                    _back.Enabled = true; _next.Enabled = _rbAccept.Checked; _next.Text = "Далее";
                    break;
                case PFolder:
                    _title.Text = "Папка установки";
                    _subtitle.Text = "Куда положить программу";
                    _back.Enabled = true; _next.Enabled = true; _next.Text = "Далее";
                    UpdateSpace();
                    break;
                case POptions:
                    _title.Text = "Дополнительные задачи";
                    _subtitle.Text = "Ярлыки и переменные среды";
                    _back.Enabled = true; _next.Enabled = true; _next.Text = "Установить";
                    break;
                case PInstall:
                    _title.Text = "Установка";
                    _subtitle.Text = "Идёт распаковка файлов...";
                    _back.Enabled = false; _next.Enabled = false; _cancel.Enabled = false;
                    break;
                case PDone:
                    _title.Text = "Установка завершена";
                    _subtitle.Text = Program.DisplayName + " готов к работе";
                    _back.Enabled = false; _next.Enabled = true; _next.Text = "Готово";
                    _cancel.Enabled = false;
                    break;
            }
        }

        private void Back()
        {
            if (_page > PWelcome) Show(_page - 1);
        }

        private void Next()
        {
            switch (_page)
            {
                case PFolder:
                    if (!CheckFolder()) return;
                    Show(POptions);
                    return;

                case POptions:
                    Show(PInstall);
                    Application.DoEvents();
                    RunInstall();
                    return;

                case PDone:
                    Finish();
                    return;

                default:
                    Show(_page + 1);
                    return;
            }
        }

        private void CancelSetup()
        {
            if (MessageBox.Show("Прервать установку?", "NWD2DWG",
                                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                Close();
        }

        // ------------------------------------------------------------------
        private static string DefaultDir()
        {
            string baseDir = Program.IsAdmin()
                ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
            return Path.Combine(baseDir, Program.AppName);
        }

        private static string SafeExistingPart(string path)
        {
            try
            {
                string d = Path.GetFullPath(path);
                while (!string.IsNullOrEmpty(d) && !Directory.Exists(d))
                    d = Path.GetDirectoryName(d);
                return string.IsNullOrEmpty(d) ? Environment.GetFolderPath(Environment.SpecialFolder.MyComputer) : d;
            }
            catch { return ""; }
        }

        /// <summary>Путь уже установленной копии, если она есть.</summary>
        private static string FindInstalled()
        {
            foreach (RegistryKey root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using (RegistryKey k = root.OpenSubKey(Program.RegPath))
                    {
                        if (k == null) continue;
                        string loc = k.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrEmpty(loc)) return loc;
                    }
                }
                catch { }
            }
            return null;
        }

        private void UpdateSpace()
        {
            if (_lbSpace == null) return;
            try
            {
                long need = PayloadSize();
                string root = Path.GetPathRoot(Path.GetFullPath(_tbDir.Text));
                long free = new DriveInfo(root).AvailableFreeSpace;
                _lbSpace.Text = "Требуется на диске: " + Mb(need) + "     Свободно на " + root + ": " + Mb(free);
                _lbSpace.ForeColor = free > need * 2
                    ? Color.FromArgb(90, 90, 90) : Color.FromArgb(170, 60, 60);
            }
            catch { _lbSpace.Text = ""; }
        }

        private static string Mb(long bytes)
        {
            return bytes >= (1L << 20)
                ? (bytes / (1024.0 * 1024.0)).ToString("0.0") + " МБ"
                : (bytes / 1024.0).ToString("0") + " КБ";
        }

        private static long PayloadSize()
        {
            try
            {
                using (Stream s = Res.Open("payload.zip"))
                using (var z = new ZipArchive(s, ZipArchiveMode.Read))
                {
                    long n = 0;
                    foreach (var e in z.Entries) n += e.Length;
                    return n;
                }
            }
            catch { return 3L << 20; }
        }

        /// <summary>
        /// Проверка выбранной папки: право записи и, если его нет, — предложение
        /// перезапуститься с правами администратора. Проверяем именно записью:
        /// атрибуты и права врут и на сетевых дисках, и в системных папках.
        /// </summary>
        private bool CheckFolder()
        {
            string dir;
            try { dir = Path.GetFullPath(_tbDir.Text.Trim().Trim('"')); }
            catch
            {
                MessageBox.Show("Путь записан неверно.", "NWD2DWG",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            if (dir.Length < 4)
            {
                MessageBox.Show("Не ставьте программу в корень диска — выберите отдельную папку.",
                                "NWD2DWG", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            try
            {
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, "setup_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write)) { }
                File.Delete(probe);
            }
            catch (Exception ex)
            {
                if (Program.IsAdmin())
                {
                    MessageBox.Show("В эту папку не удаётся писать даже с правами администратора:\r\n" +
                                    ex.Message, "NWD2DWG", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }

                if (MessageBox.Show(
                        "Для установки в эту папку нужны права администратора.\r\n\r\n" +
                        "Перезапустить установку с правами администратора?",
                        "NWD2DWG", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    Elevate(dir);
                return false;
            }

            // Обновление поверх работающей программы кончится ошибкой доступа —
            // лучше сказать об этом сейчас, чем на середине распаковки.
            string exe = Path.Combine(dir, "NWD2DWG.exe");
            if (File.Exists(exe) && IsLocked(exe))
            {
                MessageBox.Show("Файл NWD2DWG.exe занят — программа сейчас запущена.\r\n" +
                                "Закройте её и повторите.", "NWD2DWG",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            _tbDir.Text = dir;
            return true;
        }

        private static bool IsLocked(string path)
        {
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                return false;
            }
            catch (IOException) { return true; }
            catch { return false; }
        }

        private void Elevate(string dir)
        {
            try
            {
                var psi = new ProcessStartInfo(Application.ExecutablePath)
                {
                    Verb = "runas",
                    UseShellExecute = true,
                    Arguments = "/dir=\"" + dir + "\""
                };
                Process.Start(psi);
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось перезапустить с правами администратора: " + ex.Message,
                                "NWD2DWG", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ------------------------------------------------------------------
        private void Say(string s)
        {
            _logLines.Add(s);
            if (_silent) return;
            _log.Items.Add(s);
            _log.TopIndex = _log.Items.Count - 1;
            Application.DoEvents();
        }

        /// <summary>
        /// Установка без единого окна: для раскатки на парк машин.
        /// Отчёт остаётся в %TEMP%\NWD2DWG_setup.log — по нему видно, что стало.
        /// </summary>
        internal int RunSilent()
        {
            _cbDesktop.Checked = _wantShortcuts;
            _cbStartMenu.Checked = _wantShortcuts;
            _cbEnv.Checked = true;
            _cbPath.Checked = false;

            RunInstall();

            try
            {
                _logLines.Insert(0, "NWD2DWG " + Program.Version + " — тихая установка " +
                                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                File.WriteAllLines(Path.Combine(Path.GetTempPath(), "NWD2DWG_setup.log"),
                                   _logLines.ToArray(), new UTF8Encoding(true));
            }
            catch { }

            return _installed ? 0 : 1;
        }

        private void RunInstall()
        {
            string dir = _tbDir.Text;
            var manifest = new List<string>();

            try
            {
                manifest.Add("[версия] " + Program.Version);
                manifest.Add("[корень] " + dir);
                manifest.Add("[реестр] " + (Program.IsAdmin() ? "HKLM" : "HKCU"));

                Say("Папка установки: " + dir);
                Directory.CreateDirectory(dir);

                // ---- распаковка ----
                long written = 0;
                using (Stream s = Res.Open("payload.zip"))
                using (var z = new ZipArchive(s, ZipArchiveMode.Read))
                {
                    _bar.Maximum = Math.Max(1, z.Entries.Count);
                    _bar.Value = 0;

                    foreach (ZipArchiveEntry e in z.Entries)
                    {
                        if (string.IsNullOrEmpty(e.Name)) continue;   // папка

                        string target = Path.Combine(dir, e.FullName.Replace('/', '\\'));
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        e.ExtractToFile(target, true);

                        written += e.Length;
                        manifest.Add("[файл] " + e.FullName.Replace('/', '\\'));
                        Say("  " + e.FullName);
                        _bar.Value = Math.Min(_bar.Maximum, _bar.Value + 1);
                    }
                }
                Say("Распаковано: " + Mb(written));

                string exe = Path.Combine(dir, "NWD2DWG.exe");
                string manual = Path.Combine(dir, @"Документация\Руководство_пользователя.pdf");
                string uninst = Path.Combine(dir, "Удаление NWD2DWG.exe");

                // ---- ярлыки ----
                if (_cbDesktop.Checked)
                {
                    string lnk = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                        "NWD2DWG.lnk");
                    if (MakeShortcut(lnk, exe, dir, "Конвертер моделей Navisworks"))
                    {
                        manifest.Add("[ярлык] " + lnk);
                        Say("Ярлык на рабочем столе");
                    }
                }

                if (_cbStartMenu.Checked)
                {
                    string menu = Path.Combine(
                        Environment.GetFolderPath(Program.IsAdmin()
                            ? Environment.SpecialFolder.CommonPrograms
                            : Environment.SpecialFolder.Programs),
                        "NWD2DWG");
                    Directory.CreateDirectory(menu);
                    MakeShortcut(Path.Combine(menu, "NWD2DWG.lnk"), exe, dir, "Конвертер моделей Navisworks");
                    if (File.Exists(manual))
                        MakeShortcut(Path.Combine(menu, "Руководство пользователя.lnk"), manual, dir, "Руководство пользователя");
                    if (File.Exists(uninst))
                        MakeShortcut(Path.Combine(menu, "Удаление NWD2DWG.lnk"), uninst, dir, "Удалить NWD2DWG");
                    manifest.Add("[меню] " + menu);
                    Say("Папка в меню «Пуск»");
                }

                // ---- переменные среды ----
                if (_cbEnv.Checked)
                {
                    Environment.SetEnvironmentVariable(Program.EnvVar, exe, EnvironmentVariableTarget.User);
                    manifest.Add("[переменная] " + Program.EnvVar);
                    Say("Переменная среды " + Program.EnvVar);
                }

                if (_cbPath.Checked && AddToPath(dir))
                {
                    manifest.Add("[путь] " + dir);
                    Say("Папка добавлена в PATH");
                }

                // ---- запись в «Установка и удаление программ» ----
                WriteUninstallEntry(dir, exe, uninst, written);
                Say("Запись в «Установка и удаление программ»");

                // ---- опись установленного: по ней и удаляем ----
                string list = Path.Combine(dir, "установлено.txt");
                File.WriteAllLines(list, manifest.ToArray(), new UTF8Encoding(true));

                _bar.Value = _bar.Maximum;
                _installed = true;
                _installedDir = dir;

                _lbDone.Text =
                    "Программа установлена в папку:\r\n" + dir + "\r\n\r\n" +
                    "Руководство пользователя и сценарии работы лежат в подпапке «Документация».\r\n\r\n" +
                    "Удалить программу можно через «Параметры → Приложения» или файлом " +
                    "«Удаление NWD2DWG.exe» в папке установки.\r\n\r\n" +
                    "Для работы нужен установленный Navisworks 2020–2026.";

                Show(PDone);
            }
            catch (Exception ex)
            {
                Say("ОШИБКА: " + ex.Message);
                if (_silent) return;
                MessageBox.Show("Установка не завершена: " + ex.Message, "NWD2DWG",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                _cancel.Enabled = true;
                _cancel.Text = "Закрыть";
            }
        }

        /// <summary>Ярлык через WScript.Shell — без сторонних библиотек.</summary>
        private static bool MakeShortcut(string linkPath, string target, string workDir, string description)
        {
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return false;
                dynamic shell = Activator.CreateInstance(t);
                dynamic link = shell.CreateShortcut(linkPath);
                link.TargetPath = target;
                link.WorkingDirectory = workDir;
                link.Description = description;
                if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    link.IconLocation = target + ",0";
                link.Save();
                return true;
            }
            catch { return false; }
        }

        private static bool AddToPath(string dir)
        {
            try
            {
                string cur = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
                foreach (string part in cur.Split(';'))
                    if (part.Trim().TrimEnd('\\').Equals(dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        return false;                       // уже есть
                string next = cur.Length == 0 ? dir : cur.TrimEnd(';') + ";" + dir;
                Environment.SetEnvironmentVariable("PATH", next, EnvironmentVariableTarget.User);
                return true;
            }
            catch { return false; }
        }

        private static void WriteUninstallEntry(string dir, string exe, string uninst, long size)
        {
            RegistryKey root = Program.IsAdmin() ? Registry.LocalMachine : Registry.CurrentUser;
            using (RegistryKey k = root.CreateSubKey(Program.RegPath))
            {
                if (k == null) return;
                k.SetValue("DisplayName", Program.DisplayName);
                k.SetValue("DisplayVersion", Program.Version);
                k.SetValue("Publisher", Program.Publisher);
                k.SetValue("DisplayIcon", exe);
                k.SetValue("InstallLocation", dir);
                k.SetValue("UninstallString", "\"" + uninst + "\"");
                k.SetValue("QuietUninstallString", "\"" + uninst + "\" /silent");
                k.SetValue("URLInfoAbout", Program.Site);
                k.SetValue("HelpLink", Program.Site);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("EstimatedSize", (int)(size / 1024), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        private void Finish()
        {
            if (_installed)
            {
                try
                {
                    if (_cbManual.Checked)
                    {
                        string manual = Path.Combine(_installedDir, @"Документация\Руководство_пользователя.pdf");
                        if (File.Exists(manual)) Process.Start(new ProcessStartInfo(manual) { UseShellExecute = true });
                    }
                    if (_cbRun.Checked)
                    {
                        string exe = Path.Combine(_installedDir, "NWD2DWG.exe");
                        if (File.Exists(exe))
                            Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = _installedDir, UseShellExecute = true });
                    }
                }
                catch { }
            }
            Close();
        }
    }
}
