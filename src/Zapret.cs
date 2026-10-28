using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace ZapretLauncher
{
    // Всё, что касается самого zapret: стратегии, winws, служба, обновления.
    // Логику zapret не переписываем: служба ставится/удаляется оригинальным service.bat,
    // аргументы winws берутся из выбранного general*.bat.
    public class Zapret
    {
        public const string Repo = "Flowseal/zapret-discord-youtube";
        public const string ServiceName = "zapret";

        public string Dir;
        public Action<string> Log = delegate { };

        public Zapret(string dir) { Dir = dir; }

        public string BinDir { get { return Path.Combine(Dir, "bin") + "\\"; } }
        public string ListsDir { get { return Path.Combine(Dir, "lists") + "\\"; } }
        public string ServiceBat { get { return Path.Combine(Dir, "service.bat"); } }

        public static bool IsZapretDir(string d)
        {
            return !string.IsNullOrEmpty(d) && File.Exists(Path.Combine(d, "service.bat"))
                && File.Exists(Path.Combine(d, "bin", "winws.exe"));
        }

        public static bool IsAdmin()
        {
            return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        }

        public static string WinwsLogPath
        {
            get
            {
                string d = Path.Combine(Path.GetTempPath(), "ZapretLauncher");
                Directory.CreateDirectory(d);
                return Path.Combine(d, "winws.log");
            }
        }

        // ---------- Версия и стратегии ----------

        public string LocalVersion()
        {
            try
            {
                var m = Regex.Match(File.ReadAllText(ServiceBat), "set \"LOCAL_VERSION=([^\"]+)\"");
                return m.Success ? m.Groups[1].Value.Trim() : null;
            }
            catch { return null; }
        }

        public List<string> Strategies()
        {
            // Те же файлы, что показывает service.bat: *.bat кроме service*, естественная сортировка.
            return Directory.GetFiles(Dir, "*.bat")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !n.StartsWith("service", StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => Regex.Replace(n.ToLowerInvariant(), @"\d+", m => m.Value.PadLeft(8, '0')), StringComparer.Ordinal)
                .ToList();
        }

        // ---------- Состояние ----------

        public static bool WinwsRunning()
        {
            var ps = Process.GetProcessesByName("winws");
            foreach (var p in ps) p.Dispose();
            return ps.Length > 0;
        }

        // null — служба не установлена
        public static string ServiceStatus()
        {
            try
            {
                using (var sc = new ServiceController(ServiceName)) return sc.Status.ToString();
            }
            catch { return null; }
        }

        public static string ServiceStrategy()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"System\CurrentControlSet\Services\zapret"))
                    return k == null ? null : k.GetValue("zapret-discord-youtube") as string;
            }
            catch { return null; }
        }

        // ---------- Запуск winws ----------

        // Возвращает PID запущенного winws.exe
        public int Start(string strategy)
        {
            string bat = Path.Combine(Dir, strategy + ".bat");
            if (!File.Exists(bat)) throw new Exception("Не найден файл стратегии " + strategy + ".bat");

            // Подготовка средствами самого service.bat: tcp timestamps, user-списки, Game Filter.
            string prep = RunCapture("call service.bat status_zapret & call service.bat load_user_lists & call service.bat load_game_filter & set GameFilter", 20000);
            var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Regex.Matches(prep, @"^(GameFilter\w*)=(.*)$", RegexOptions.Multiline))
                vars[m.Groups[1].Value] = m.Groups[2].Value.Trim();

            string args = BuildArgs(bat, vars);
            Log("Стратегия: " + strategy + (vars.ContainsKey("GameFilterTCP") ? ", Game Filter TCP=" + vars["GameFilterTCP"] + " UDP=" + vars["GameFilterUDP"] : ""));
            return Native.StartHidden(Path.Combine(BinDir, "winws.exe"), args, BinDir, WinwsLogPath);
        }

        public string BuildArgs(string bat, Dictionary<string, string> vars)
        {
            string text = File.ReadAllText(bat, Encoding.UTF8);
            text = Regex.Replace(text, @"\^[ \t]*\r?\n", "");   // продолжения строк cmd
            int i = text.IndexOf("winws.exe\"", StringComparison.OrdinalIgnoreCase);
            if (i < 0) throw new Exception("В " + Path.GetFileName(bat) + " не найден вызов winws.exe");
            string line = text.Substring(i + "winws.exe\"".Length);
            int nl = line.IndexOfAny(new[] { '\r', '\n' });
            if (nl >= 0) line = line.Substring(0, nl);

            line = line.Replace("%BIN%", BinDir).Replace("%LISTS%", ListsDir).Replace("%~dp0", Dir.TrimEnd('\\') + "\\");
            line = Regex.Replace(line, @"%(\w+)%", m =>
            {
                string v;
                if (vars.TryGetValue(m.Groups[1].Value, out v)) return v;
                Log("Внимание: переменная " + m.Value + " не определена");
                return "";
            });
            return line.Trim();
        }

        public static void StopWinws()
        {
            foreach (var p in Process.GetProcessesByName("winws"))
            {
                try { p.Kill(); p.WaitForExit(3000); } catch (Exception e) { throw new Exception("Не удалось остановить winws: " + e.Message); }
                finally { p.Dispose(); }
            }
        }

        // ---------- service.bat ----------

        // Системные утилиты (find, findstr, sc) — первыми в PATH, чтобы bat не подхватил
        // одноимённые программы из Git/Cygwin.
        static ProcessStartInfo Cmd(string args, string dir)
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), args)
            {
                WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
            };
            string sys = Environment.SystemDirectory, win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            psi.EnvironmentVariables["PATH"] = sys + ";" + win + ";" + Path.Combine(sys, "Wbem") + ";"
                + Path.Combine(sys, @"WindowsPowerShell\v1.0") + ";" + Environment.GetEnvironmentVariable("PATH");
            return psi;
        }

        string RunCapture(string command, int timeoutMs)
        {
            var psi = Cmd("/d /s /c \"" + command + "\"", Dir);
            using (var p = Process.Start(psi))
            {
                p.StandardInput.Close();   // никаких pause/ожидания ввода
                var err = p.StandardError.ReadToEndAsync();
                string o = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } }
                return o + err.Result;
            }
        }

        class Step
        {
            public string Wait;
            public Func<string, string> Reply;
            public Step(string w, Func<string, string> r) { Wait = w; Reply = r; }
        }

        // Ведём интерактивное меню service.bat: ждём подсказку — отвечаем.
        // set /p читает из пайпа весь буфер сразу, поэтому каждый ответ пишем отдельно и только
        // когда bat его ждёт. pause (текст локализован) узнаём по затихшему выводу.
        void DriveServiceBat(List<Step> steps)
        {
            var psi = Cmd("/d /s /c \"\"" + ServiceBat + "\" admin\"", Dir);
            psi.StandardOutputEncoding = Encoding.UTF8;
            var buf = new StringBuilder();
            var gate = new object();
            DateTime lastOut = DateTime.UtcNow;
            using (var p = Process.Start(psi))
            {
                var reader = new Thread(() =>
                {
                    var chunk = new char[1024];
                    int n;
                    while ((n = p.StandardOutput.Read(chunk, 0, chunk.Length)) > 0)
                        lock (gate) { buf.Append(chunk, 0, n); lastOut = DateTime.UtcNow; Monitor.PulseAll(gate); }
                    lock (gate) Monitor.PulseAll(gate);
                }) { IsBackground = true };
                reader.Start();
                p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) Log(e.Data); };
                p.BeginErrorReadLine();

                int from = 0, logged = 0, step = 0;
                DateTime lastIn = DateTime.UtcNow, deadline = DateTime.UtcNow.AddSeconds(90);
                try
                {
                    while (!p.HasExited)
                    {
                        if (DateTime.UtcNow > deadline) throw new Exception("service.bat не завершился за 90 секунд");
                        string reply = null;
                        lock (gate)
                        {
                            Monitor.Wait(gate, 300);
                            string all = buf.ToString();
                            string token = step < steps.Count ? steps[step].Wait : "Select option";
                            int idx = all.IndexOf(token, from, StringComparison.OrdinalIgnoreCase);
                            if (idx >= 0)
                            {
                                from = idx + token.Length;
                                reply = step < steps.Count ? steps[step].Reply(all) : "0\r\n";   // после шагов — выход из меню
                                if (reply == null) throw new Exception("Стратегия не найдена в меню service.bat");
                                step++;
                            }
                            else if (step > 0 && lastOut > lastIn && (DateTime.UtcNow - lastOut).TotalMilliseconds > 1500)
                                reply = "\r\n";   // вероятно, pause
                            logged = FlushLog(all, logged);
                        }
                        if (reply != null)
                        {
                            p.StandardInput.Write(reply);
                            p.StandardInput.Flush();
                            lastIn = DateTime.UtcNow;
                        }
                    }
                }
                catch
                {
                    try { p.Kill(); } catch { }
                    throw;
                }
                finally
                {
                    reader.Join(2000);
                    lock (gate) FlushLog(buf.ToString(), logged);
                }
                if (step < steps.Count) throw new Exception("service.bat завершился раньше времени");
            }
        }

        int FlushLog(string all, int from)
        {
            if (from >= all.Length) return from;
            foreach (var l in all.Substring(from).Replace("\f", "").Split('\n'))
            {
                string t = l.TrimEnd('\r', ' ');
                if (t.Length > 0 && t.IndexOf('�') < 0 && !t.StartsWith("Select option") && !t.StartsWith("Input option")) Log("  " + t);
            }
            return all.Length;
        }

        public void InstallService(string strategy)
        {
            string file = strategy + ".bat";
            Log("Установка службы: " + file);
            DriveServiceBat(new List<Step>
            {
                new Step("Select option", s => "1\r\n"),
                new Step("Input option", s =>
                {
                    var m = Regex.Match(s, @"^\s*(\d+)\.\s+" + Regex.Escape(file) + @"\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                    return m.Success ? m.Groups[1].Value + "\r\n" : null;
                }),
            });
            if (ServiceStatus() == null) throw new Exception("Служба не появилась — смотрите журнал");
        }

        public void RemoveService()
        {
            Log("Удаление службы zapret");
            DriveServiceBat(new List<Step>
            {
                new Step("Select option", s => "2\r\n"),
            });
        }

        // ---------- Обновления ----------

        public class Release
        {
            public string Tag, Body, Url, ZipUrl, ZipName, Date;
        }

        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var wc = new WebClient { Encoding = Encoding.UTF8 };
            wc.Headers[HttpRequestHeader.UserAgent] = "ZapretLauncher";
            wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
            return wc;
        }

        public static Release Latest()
        {
            string json;
            using (var wc = Client()) json = wc.DownloadString("https://api.github.com/repos/" + Repo + "/releases/latest");
            var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            var r = new Release
            {
                Tag = (d["tag_name"] as string ?? "").Trim(),
                Body = d.ContainsKey("body") ? d["body"] as string : "",
                Url = d["html_url"] as string,
                Date = d.ContainsKey("published_at") ? d["published_at"] as string : ""
            };
            foreach (var o in (IEnumerable)d["assets"])
            {
                var a = (Dictionary<string, object>)o;
                string name = a["name"] as string, url = a["browser_download_url"] as string;
                if (name != null && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && IsTrustedUrl(url))
                {
                    r.ZipName = name; r.ZipUrl = url; break;
                }
            }
            return r;
        }

        // Скачиваем только с github.com/Flowseal/zapret-discord-youtube
        public static bool IsTrustedUrl(string url)
        {
            Uri u;
            return Uri.TryCreate(url, UriKind.Absolute, out u) && u.Scheme == "https"
                && u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                && u.AbsolutePath.StartsWith("/" + Repo + "/releases/download/", StringComparison.OrdinalIgnoreCase);
        }

        // «Пинг» сервиса: время HTTPS-запроса до ответа сервера, мс; -1 — нет ответа.
        // ICMP тут бесполезен: DPI режет TLS, а не ping.
        public static int Probe(string url)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var sw = Stopwatch.StartNew();
            var rq = (HttpWebRequest)WebRequest.Create(url);
            rq.Method = "HEAD";
            rq.Timeout = rq.ReadWriteTimeout = 6000;
            rq.KeepAlive = false;
            rq.AllowAutoRedirect = false;
            rq.UserAgent = "Mozilla/5.0 ZapretLauncher";
            try { using (rq.GetResponse()) { } }
            catch (WebException e)
            {
                if (e.Response == null) return -1;   // таймаут/сброс соединения
                e.Response.Close();                  // любой HTTP-ответ — сервер доступен
            }
            catch { return -1; }
            return (int)sw.ElapsedMilliseconds;
        }

        public static int CompareVersions(string a, string b)
        {
            var pa = Regex.Matches(a ?? "", @"\d+").Cast<Match>().Select(m => long.Parse(m.Value)).ToList();
            var pb = Regex.Matches(b ?? "", @"\d+").Cast<Match>().Select(m => long.Parse(m.Value)).ToList();
            for (int i = 0; i < Math.Max(pa.Count, pb.Count); i++)
            {
                long x = i < pa.Count ? pa[i] : 0, y = i < pb.Count ? pb[i] : 0;
                if (x != y) return x.CompareTo(y);
            }
            return 0;
        }

        public static string Download(Release r, Action<int> progress)
        {
            if (!IsTrustedUrl(r.ZipUrl)) throw new Exception("Недоверенный адрес загрузки");
            string path = Path.Combine(Path.GetTempPath(), "ZapretLauncher", r.ZipName);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var wc = Client())
            {
                wc.DownloadProgressChanged += (s, e) => progress(e.ProgressPercentage);
                wc.DownloadFileTaskAsync(new Uri(r.ZipUrl), path).Wait();
            }
            return path;
        }

        static bool Preserve(string rel)
        {
            rel = rel.Replace('/', '\\').ToLowerInvariant();
            return rel.EndsWith("-user.txt") || (rel.StartsWith("utils\\") && rel.EndsWith(".enabled"));
        }

        // Распаковка релиза поверх папки: пользовательские списки и флаги сохраняются.
        public static void ApplyZip(string zip, string dir)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "ZapretLauncher", "upd-" + Guid.NewGuid().ToString("N"));
            ZipFile.ExtractToDirectory(zip, tmp);
            try
            {
                string root = tmp;
                var dirs = Directory.GetDirectories(tmp);
                if (Directory.GetFiles(tmp).Length == 0 && dirs.Length == 1) root = dirs[0];
                if (!File.Exists(Path.Combine(root, "service.bat"))) throw new Exception("В архиве нет service.bat");

                Directory.CreateDirectory(dir);
                // Стратегии, удалённые в новой версии, убираем, чтобы не висели в списке
                var fresh = new HashSet<string>(Directory.GetFiles(root, "*.bat").Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
                foreach (var old in Directory.GetFiles(dir, "*.bat"))
                    if (!fresh.Contains(Path.GetFileName(old))) File.Delete(old);

                foreach (var src in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                {
                    string rel = src.Substring(root.Length).TrimStart('\\');
                    string dst = Path.Combine(dir, rel);
                    if (File.Exists(dst) && (Preserve(rel) || SameFile(src, dst))) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.Copy(src, dst, true);
                }
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }

        static bool SameFile(string a, string b)
        {
            var fa = new FileInfo(a); var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            return File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b));
        }

        public static void StopDriver()
        {
            foreach (var n in new[] { "WinDivert", "WinDivert14" })
            {
                try
                {
                    using (var sc = new ServiceController(n))
                        if (sc.Status != ServiceControllerStatus.Stopped) { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(5)); }
                }
                catch { }
            }
        }

        public static void StopService()
        {
            using (var sc = new ServiceController(ServiceName))
                if (sc.Status != ServiceControllerStatus.Stopped) { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10)); }
        }
    }

    static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct STARTUPINFO
        {
            public int cb;
            public string lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CreateProcess(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit,
            int flags, IntPtr env, string cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] static extern bool SetHandleInformation(IntPtr h, int mask, int flags);

        // winws без окна; вывод — в файл (а не в пайп), чтобы процесс жил и после закрытия лаунчера.
        public static int StartHidden(string exe, string args, string cwd, string logFile)
        {
            using (var fs = new FileStream(logFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                IntPtr h = fs.SafeFileHandle.DangerousGetHandle();
                SetHandleInformation(h, 1, 1);   // HANDLE_FLAG_INHERIT
                var si = new STARTUPINFO { cb = Marshal.SizeOf(typeof(STARTUPINFO)), dwFlags = 0x100, hStdOutput = h, hStdError = h };
                PROCESS_INFORMATION pi;
                var cmd = new StringBuilder("\"" + exe + "\" " + args);
                const int CREATE_NO_WINDOW = 0x08000000;
                if (!CreateProcess(exe, cmd, IntPtr.Zero, IntPtr.Zero, true, CREATE_NO_WINDOW, IntPtr.Zero, cwd, ref si, out pi))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
                return pi.dwProcessId;
            }
        }
    }
}
