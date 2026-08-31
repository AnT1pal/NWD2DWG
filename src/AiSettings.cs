// ============================================================================
//  AiSettings.cs — подключение языковых моделей и маршрутизатор провайдеров
//  NWD2DWG | namespace NWD2DWG.Plugin
//
//  Назначение: дать программе доступ к языковой модели там, где она реально
//  помогает, не превращая инженерный инструмент в чат.
//
//  Принципы, заложенные в конструкцию:
//    * По умолчанию всё выключено. Ни один байт модели никуда не уходит,
//      пока пользователь явно не включил и не настроил провайдера.
//    * Режим «только локальные адреса» запрещает обращения куда-либо кроме
//      localhost — для работы в закрытом контуре это не пожелание, а условие.
//    * Ключи хранятся зашифрованными средствами Windows (DPAPI) под текущим
//      пользователем, а не открытым текстом в JSON.
//    * Маршрутизатор перебирает провайдеров по порядку: первый ответивший
//      выигрывает. Это позволяет держать локальную модель основной,
//      а облачную — резервной, или наоборот.
//
//  Протокол общения — OpenAI-совместимый chat/completions. На нём говорят
//  Ollama, LM Studio, vLLM, llama.cpp server и большинство шлюзов, поэтому
//  один клиент покрывает и локальные, и облачные варианты.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace NWD2DWG.Plugin
{
    public class AiProvider
    {
        public string Name = "";
        public string BaseUrl = "";        // например http://localhost:11434/v1
        public string Model = "";
        public string KeyProtected = "";   // DPAPI, base64; пусто для локальных
        public bool Enabled = false;
        public int TimeoutSec = 60;

        public bool IsLocal
        {
            get
            {
                if (string.IsNullOrEmpty(BaseUrl)) return false;
                try
                {
                    var u = new Uri(BaseUrl);
                    string h = u.Host.ToLowerInvariant();
                    return h == "localhost" || h == "127.0.0.1" || h == "::1" || h == "[::1]";
                }
                catch { return false; }
            }
        }

        // --- ключ хранится зашифрованным под текущим пользователем Windows ---
        public void SetKey(string plain)
        {
            if (string.IsNullOrEmpty(plain)) { KeyProtected = ""; return; }
            try
            {
                byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain),
                                                   null, DataProtectionScope.CurrentUser);
                KeyProtected = Convert.ToBase64String(enc);
            }
            catch { KeyProtected = ""; }
        }

        public string GetKey()
        {
            if (string.IsNullOrEmpty(KeyProtected)) return "";
            try
            {
                byte[] dec = ProtectedData.Unprotect(Convert.FromBase64String(KeyProtected),
                                                     null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(dec);
            }
            catch { return ""; }
        }

        public bool HasKey { get { return !string.IsNullOrEmpty(KeyProtected); } }
    }

    public class AiSettings
    {
        public bool Enabled = false;             // главный выключатель
        public bool LocalOnly = true;            // запрет обращений за пределы машины
        public bool AllowModelData = false;      // разрешить передавать имена элементов
        public int  MaxNamesPerRequest = 200;    // ограничение объёма выгружаемых данных

        public readonly List<AiProvider> Providers = new List<AiProvider>();

        public AiSettings()
        {
            // Порядок задаёт маршрут: локальная модель первой, облако — резерв.
            Providers.Add(new AiProvider { Name = "Локальная (Ollama / LM Studio)", BaseUrl = "http://localhost:11434/v1", Model = "qwen2.5:14b" });
            Providers.Add(new AiProvider { Name = "Локальный сервер бюро",          BaseUrl = "", Model = "" });
            Providers.Add(new AiProvider { Name = "Внешний шлюз",                   BaseUrl = "", Model = "" });
        }

        // --------------------------------------------------------------------
        // Маршрутизатор: провайдеры в порядке приоритета с учётом ограничений
        // --------------------------------------------------------------------
        public List<AiProvider> Route(out string reason)
        {
            var res = new List<AiProvider>();
            reason = "";
            if (!Enabled) { reason = "ИИ-помощник выключен в настройках"; return res; }

            foreach (var p in Providers)
            {
                if (!p.Enabled) continue;
                if (string.IsNullOrEmpty(p.BaseUrl) || string.IsNullOrEmpty(p.Model)) continue;
                if (LocalOnly && !p.IsLocal) continue;
                res.Add(p);
            }
            if (res.Count == 0)
                reason = LocalOnly
                    ? "нет включённых локальных провайдеров (режим «только локальные адреса»)"
                    : "нет включённых провайдеров с заданными адресом и моделью";
            return res;
        }

        // --------------------------------------------------------------------
        // Хранилище
        // --------------------------------------------------------------------
        public static string DefaultFile
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NWD2DWG");
                if (!Directory.Exists(dir)) { try { Directory.CreateDirectory(dir); } catch { } }
                return Path.Combine(dir, "ai.json");
            }
        }

        public static AiSettings Load() { return LoadFrom(DefaultFile); }

        public static AiSettings LoadFrom(string path)
        {
            var s = new AiSettings();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return s;
            try
            {
                var inv = CultureInfo.InvariantCulture;
                int idx = -1;
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim().TrimEnd(',');
                    int c = line.IndexOf(':');
                    if (c < 0) { if (line.StartsWith("{ \"provider")) idx++; continue; }
                    string k = line.Substring(0, c).Trim().Trim('"');
                    string v = line.Substring(c + 1).Trim();
                    if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"') v = v.Substring(1, v.Length - 2);

                    if (k == "Enabled") { bool b; if (bool.TryParse(v, out b)) s.Enabled = b; continue; }
                    if (k == "LocalOnly") { bool b; if (bool.TryParse(v, out b)) s.LocalOnly = b; continue; }
                    if (k == "AllowModelData") { bool b; if (bool.TryParse(v, out b)) s.AllowModelData = b; continue; }
                    if (k == "MaxNamesPerRequest") { int n; if (int.TryParse(v, NumberStyles.Integer, inv, out n)) s.MaxNamesPerRequest = n; continue; }

                    // provider0.Field
                    if (k.StartsWith("provider", StringComparison.Ordinal))
                    {
                        int dot = k.IndexOf('.');
                        if (dot < 0) continue;
                        int pi;
                        if (!int.TryParse(k.Substring(8, dot - 8), out pi)) continue;
                        while (s.Providers.Count <= pi) s.Providers.Add(new AiProvider());
                        var p = s.Providers[pi];
                        string f = k.Substring(dot + 1);
                        switch (f)
                        {
                            case "Name": p.Name = v; break;
                            case "BaseUrl": p.BaseUrl = v; break;
                            case "Model": p.Model = v; break;
                            case "KeyProtected": p.KeyProtected = v; break;
                            case "Enabled": { bool b; if (bool.TryParse(v, out b)) p.Enabled = b; break; }
                            case "TimeoutSec": { int n; if (int.TryParse(v, out n)) p.TimeoutSec = n; break; }
                        }
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save() { SaveTo(DefaultFile); }

        public void SaveTo(string path)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"Enabled\": " + (Enabled ? "true" : "false") + ",");
                sb.AppendLine("  \"LocalOnly\": " + (LocalOnly ? "true" : "false") + ",");
                sb.AppendLine("  \"AllowModelData\": " + (AllowModelData ? "true" : "false") + ",");
                sb.AppendLine("  \"MaxNamesPerRequest\": " + MaxNamesPerRequest.ToString(CultureInfo.InvariantCulture) + ",");
                for (int i = 0; i < Providers.Count; i++)
                {
                    var p = Providers[i];
                    string pre = "  \"provider" + i.ToString(CultureInfo.InvariantCulture) + ".";
                    sb.AppendLine(pre + "Name\": \"" + Esc(p.Name) + "\",");
                    sb.AppendLine(pre + "BaseUrl\": \"" + Esc(p.BaseUrl) + "\",");
                    sb.AppendLine(pre + "Model\": \"" + Esc(p.Model) + "\",");
                    sb.AppendLine(pre + "KeyProtected\": \"" + p.KeyProtected + "\",");
                    sb.AppendLine(pre + "Enabled\": " + (p.Enabled ? "true" : "false") + ",");
                    sb.AppendLine(pre + "TimeoutSec\": " + p.TimeoutSec.ToString(CultureInfo.InvariantCulture)
                                  + (i < Providers.Count - 1 ? "," : ""));
                }
                sb.AppendLine("}");
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ");
        }
    }

    // ------------------------------------------------------------------------
    // Клиент OpenAI-совместимого chat/completions с перебором провайдеров.
    //
    // Сетевой код намеренно минимален и без внешних зависимостей: работает
    // на HttpWebRequest из состава .NET Framework.
    // ------------------------------------------------------------------------
    public static class AiClient
    {
        /// <summary>Короткий запрос к первому ответившему провайдеру.</summary>
        public static bool Ask(AiSettings settings, string system, string user,
                               out string answer, out string usedProvider, out string error)
        {
            answer = ""; usedProvider = ""; error = "";

            string reason;
            var route = settings.Route(out reason);
            if (route.Count == 0) { error = reason; return false; }

            var problems = new List<string>();
            foreach (var p in route)
            {
                try
                {
                    answer = Call(p, system, user);
                    usedProvider = p.Name;
                    return true;
                }
                catch (Exception ex)
                {
                    problems.Add(p.Name + ": " + ex.Message);
                }
            }
            error = "все провайдеры недоступны — " + string.Join("; ", problems.ToArray());
            return false;
        }

        /// <summary>Проверка подключения: спрашивает у модели одно слово.</summary>
        public static bool Test(AiSettings settings, AiProvider p, out string info)
        {
            info = "";
            if (settings.LocalOnly && !p.IsLocal)
            {
                info = "адрес не локальный, а включён режим «только локальные адреса»";
                return false;
            }
            if (string.IsNullOrEmpty(p.BaseUrl) || string.IsNullOrEmpty(p.Model))
            {
                info = "не заданы адрес или модель";
                return false;
            }
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string r = Call(p, "Отвечай одним словом.", "Скажи: готов");
                sw.Stop();
                info = string.Format(CultureInfo.InvariantCulture,
                    "ответ получен за {0:F1} с: {1}", sw.Elapsed.TotalSeconds,
                    r.Length > 60 ? r.Substring(0, 60) + "…" : r);
                return true;
            }
            catch (Exception ex)
            {
                info = ex.Message;
                return false;
            }
        }

        private static string Call(AiProvider p, string system, string user)
        {
            string url = p.BaseUrl.TrimEnd('/') + "/chat/completions";
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Timeout = Math.Max(5, p.TimeoutSec) * 1000;
            req.ReadWriteTimeout = req.Timeout;
            string key = p.GetKey();
            if (!string.IsNullOrEmpty(key)) req.Headers["Authorization"] = "Bearer " + key;

            string body = "{\"model\":\"" + J(p.Model) + "\",\"temperature\":0,\"messages\":["
                        + "{\"role\":\"system\",\"content\":\"" + J(system) + "\"},"
                        + "{\"role\":\"user\",\"content\":\"" + J(user) + "\"}]}";
            byte[] data = Encoding.UTF8.GetBytes(body);
            req.ContentLength = data.Length;
            using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);

            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                return ExtractContent(rd.ReadToEnd());
            }
        }

        // Разбор ответа без внешнего парсера JSON: берём значение первого
        // поля "content" — этого достаточно для chat/completions.
        private static string ExtractContent(string json)
        {
            if (string.IsNullOrEmpty(json)) return "";
            int i = json.IndexOf("\"content\"", StringComparison.Ordinal);
            if (i < 0) return json.Length > 400 ? json.Substring(0, 400) : json;
            i = json.IndexOf('"', i + 9);
            if (i < 0) return "";
            i = json.IndexOf('"', i + 1);
            int start = i + 1;
            var sb = new StringBuilder();
            for (int k = start; k < json.Length; k++)
            {
                char c = json[k];
                if (c == '\\' && k + 1 < json.Length)
                {
                    char n = json[++k];
                    if (n == 'n') sb.Append('\n');
                    else if (n == 't') sb.Append('\t');
                    else if (n == 'u' && k + 4 < json.Length)
                    {
                        int code;
                        if (int.TryParse(json.Substring(k + 1, 4), NumberStyles.HexNumber,
                                         CultureInfo.InvariantCulture, out code))
                        { sb.Append((char)code); k += 4; }
                    }
                    else sb.Append(n);
                    continue;
                }
                if (c == '"') break;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string J(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 16);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
