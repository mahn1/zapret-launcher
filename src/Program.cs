using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ZapretLauncher
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool fresh;
            var mutex = new Mutex(true, "Local\\ZapretLauncher.Single", out fresh);
            if (!fresh)
            {
                // после перезапуска с правами администратора ждём, пока старый экземпляр закроется
                bool got = false;
                try { got = mutex.WaitOne(args.Length > 0 ? 6000 : 0); } catch (AbandonedMutexException) { got = true; }
                if (!got)
                {
                    // уже запущен (может, в трее) — просим его показать окно
                    if (args.Length == 0 || args[0] != "--tray")
                        try { EventWaitHandle.OpenExisting(Ui.ShowEvent).Set(); } catch { }
                    return;
                }
            }
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var ui = new Ui(args);
            app.Run(ui.W);
            GC.KeepAlive(mutex);
        }
    }

    enum State { Off, Starting, On, Service, Stopping }

    class Ui
    {
        public Window W;
        readonly string[] args;
        Zapret z;
        State state = State.Off;
        Zapret.Release release;
        bool busy;
        long logPos;
        FrameworkElement page;
        DateTime? since;     // когда включили обход (для таймера)
        bool pinging, justPinged;
        bool picking, pickCancel;               // автоподбор стратегии
        bool expectRun;                          // сторож: winws должен работать
        string runStrategy;
        readonly List<DateTime> restarts = new List<DateTime>();
        int ytFails, dcFails;
        Tray tray;
        bool exiting;
        public const string ShowEvent = "Local\\ZapretLauncher.Show";
        int clockTicks;

        static readonly Color Mint = C("#2EE6A8"), Blue = C("#3B82F6"), Warn = C("#F5B14C"), Danger = C("#F0506E"), Grey = C("#8B93A7");
        static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        readonly GradientStop core1 = new GradientStop(C("#262B3A"), 0), core2 = new GradientStop(C("#171B25"), 1);
        readonly SolidColorBrush glyph = new SolidColorBrush(C("#7D8699"));
        readonly ScaleTransform powerScale = new ScaleTransform(1, 1), glowScale = new ScaleTransform(1, 1);
        readonly RotateTransform spin = new RotateTransform();
        readonly TranslateTransform toastShift = new TranslateTransform(0, 16);
        readonly DispatcherTimer toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.2) };

        T F<T>(string name) where T : class { return (T)W.FindName(name); }
        static Color C(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }

        public Ui(string[] a)
        {
            args = a;
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ZapretLauncher.MainWindow.xaml"))
                W = (Window)XamlReader.Load(s);

            // кнопка питания
            F<Ellipse>("Core").Fill = new LinearGradientBrush(new GradientStopCollection { core1, core2 }, new Point(0, 0), new Point(1, 1));
            F<TextBlock>("PowerGlyph").Foreground = glyph;
            F<Grid>("PowerBtn").RenderTransform = powerScale;
            F<Ellipse>("Glow").RenderTransform = glowScale;
            F<Ellipse>("Spinner").RenderTransform = spin;
            F<Border>("Toast").RenderTransform = toastShift;
            var pb = F<Grid>("PowerBtn");
            pb.MouseEnter += (s, e) => Scale(powerScale, 1.03, 250);
            pb.MouseLeave += (s, e) => Scale(powerScale, 1.0, 300);
            pb.MouseLeftButtonDown += (s, e) => { Scale(powerScale, 0.93, 90); pb.CaptureMouse(); };
            pb.MouseLeftButtonUp += (s, e) =>
            {
                pb.ReleaseMouseCapture();
                Scale(powerScale, pb.IsMouseOver ? 1.03 : 1.0, 450, new ElasticEase { Oscillations = 1, Springiness = 4 });
                if (pb.IsMouseOver) TogglePower();
            };

            // навигация
            Nav("NavHome", "PageHome");
            Nav("NavService", "PageService");
            Nav("NavUpdate", "PageUpdate");
            Nav("NavLog", "PageLog");
            Nav("NavTg", "PageTg");
            F<RadioButton>("NavTg").Checked += (s, e) => RefreshTg();
            F<Button>("BtnTgFind").Click += (s, e) => FindProxies();
            F<Button>("BtnTgConnect").Click += (s, e) => ConnectProxy();
            F<Button>("BtnTgCopy").Click += (s, e) => CopyProxy();
            F<ListBox>("TgList").MouseDoubleClick += (s, e) => ConnectProxy();
            F<ListBox>("TgList").SelectionChanged += (s, e) =>
            {
                bool any = SelectedProxy != null;
                F<Button>("BtnTgConnect").IsEnabled = any;
                F<Button>("BtnTgCopy").IsEnabled = any;
            };
            page = F<FrameworkElement>("PageHome");

            F<Button>("BtnMin").Click += (s, e) => HideToTray();
            F<Button>("BtnClose").Click += (s, e) => HideToTray();
            F<Button>("BtnClose").ToolTip = "Свернуть в трей (выход — через меню значка)";
            W.StateChanged += (s, e) => { if (W.WindowState == WindowState.Minimized) HideToTray(); };
            F<Button>("BtnSvcInstall").Click += (s, e) => InstallService();
            F<Button>("BtnSvcRemove").Click += (s, e) => RemoveService();
            F<Button>("BtnSvcMenu").Click += (s, e) => { if (z != null) Shell(z.ServiceBat); };
            F<Button>("BtnCheck").Click += (s, e) => CheckUpdates(false);
            F<Button>("BtnUpdate").Click += (s, e) => InstallUpdate();
            F<Button>("BtnReleasePage").Click += (s, e) => Shell(release != null && release.Url != null ? release.Url : "https://github.com/" + Zapret.Repo + "/releases/latest");
            F<Button>("BtnLogClear").Click += (s, e) => F<TextBox>("LogBox").Clear();
            F<Button>("BtnLogFolder").Click += (s, e) => { if (z != null) Shell(z.Dir); };
            F<Button>("BtnPickDir").Click += (s, e) => PickDir();
            F<Button>("BtnFirstDownload").Click += (s, e) => InstallUpdate();
            F<ListBox>("StratList").SelectionChanged += (s, e) => { Settings.Set("strategy", Selected); UpdateHints(); };
            F<ListBox>("StratList").MouseDoubleClick += (s, e) => { if (Selected != null && state == State.Off) TogglePower(); };

            F<Button>("BtnPick").Click += (s, e) => AutoPick();

            // настройки запуска и сторожа
            var tAuto = F<CheckBox>("TglAutostart");
            var tOn = F<CheckBox>("TglAutoOn");
            var tWatch = F<CheckBox>("TglWatch");
            tOn.IsChecked = Settings.Get("autoon") == "1";
            tWatch.IsChecked = Settings.Get("watch") != "0";
            tAuto.Click += (s, e) => ToggleAutostart(tAuto.IsChecked == true);
            tOn.Click += (s, e) => Settings.Set("autoon", tOn.IsChecked == true ? "1" : "0");
            tWatch.Click += (s, e) => Settings.Set("watch", tWatch.IsChecked == true ? "1" : "0");
            F<RadioButton>("NavService").Checked += (s, e) => { SyncAutostart(); RefreshDomains(); };

            // свои сайты
            F<Button>("BtnDomAdd").Click += (s, e) => AddDomain(false);
            F<Button>("BtnDomExclude").Click += (s, e) => AddDomain(true);
            F<Button>("BtnDomRemove").Click += (s, e) => RemoveDomain();
            F<Button>("BtnPresetRoblox").Click += (s, e) => AddPreset("Roblox", new[] { "roblox.com", "rbxcdn.com", "robloxlabs.com" }, false);
            F<Button>("BtnPresetPhoton").Click += (s, e) => AddPreset("Photon", new[] { "photonengine.com", "photonengine.io", "exitgames.com" }, true);
            F<TextBox>("DomInput").KeyDown += (s, e) => { if (e.Key == Key.Enter) AddDomain(false); };
            F<ListBox>("DomList").SelectionChanged += (s, e) => F<Button>("BtnDomRemove").IsEnabled = F<ListBox>("DomList").SelectedItem != null;

            // запуск из автозагрузки: окно сразу уходит в трей
            if (args.Length > 0 && args[0] == "--tray")
            {
                W.WindowStartupLocation = WindowStartupLocation.Manual;
                W.Left = W.Top = -30000;
                W.ShowInTaskbar = false;
                W.ShowActivated = false;
            }

            bool admin = Zapret.IsAdmin();
            F<TextBlock>("ChipAdmin").Text = admin ? "администратор" : "без прав";
            F<Ellipse>("ChipAdminDot").Fill = new SolidColorBrush(admin ? Mint : Warn);

            W.Loaded += (s, e) => OnLoaded();
            W.Closing += (s, e) =>
            {
                if (!exiting) { e.Cancel = true; HideToTray(); return; }
                Settings.Save();
                if (tray != null) tray.Dispose();
            };

            tray = new Tray(ShowFromTray, () => { ShowFromTray(); TogglePower(); }, ExitApp);
            UpdateTray();
            // второй запуск лаунчера разворачивает это окно
            var ev = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEvent);
            new Thread(() => { while (ev.WaitOne()) W.Dispatcher.BeginInvoke((Action)ShowFromTray); }) { IsBackground = true }.Start();
        }

        // ---------- трей ----------

        void HideToTray()
        {
            W.Hide();
            if (Settings.Get("trayhint") == null)
            {
                tray.Balloon("Zapret Launcher в трее", "Обход продолжает работать. Выход — правой кнопкой по значку.");
                Settings.Set("trayhint", "1");
            }
        }

        void ShowFromTray()
        {
            W.Show();
            if (W.WindowState == WindowState.Minimized) W.WindowState = WindowState.Normal;
            W.Activate();
            W.Topmost = true; W.Topmost = false;   // поверх остальных окон
            FadeIn((FrameworkElement)W.Content, 10);
        }

        void ExitApp()
        {
            exiting = true;
            W.Close();
        }

        void UpdateTray()
        {
            if (tray == null) return;
            bool lit = state == State.On || state == State.Service;
            string tip = z == null ? "Zapret Launcher · zapret не найден"
                : state == State.Off ? "Zapret · выключено"
                : state == State.Starting ? "Zapret · запуск…"
                : state == State.Stopping ? "Zapret · остановка…"
                : "Zapret · " + (state == State.Service ? "служба" : "работает") + " · " + F<TextBlock>("Uptime").Text;
            tray.Update(lit, z != null && (state == State.Off || state == State.On), tip);
        }

        // ---------- запуск ----------

        void OnLoaded()
        {
            if (args.Length > 0 && args[0] == "--tray")
            {
                // скрываем после того, как WPF закончит показ окна (внутри Loaded Hide не срабатывает)
                W.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, (Action)(() =>
                {
                    W.Hide();
                    W.ShowInTaskbar = true;
                    var wa = SystemParameters.WorkArea;
                    W.Left = wa.Left + (wa.Width - W.Width) / 2;
                    W.Top = wa.Top + (wa.Height - W.Height) / 2;
                }));
            }
            LoadDir(FindDir());
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            t.Tick += (s, e) => { Refresh(); TailWinwsLog(); if (page != null && page.Name == "PageTg" && W.IsVisible) RefreshTg(); };
            t.Start();
            var clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            clock.Tick += (s, e) => TickClock();
            clock.Start();
            toastTimer.Tick += (s, e) => { toastTimer.Stop(); HideToast(); };

            // действие после перезапуска с правами администратора
            string act = args.Length > 0 ? args[0] : null, arg = args.Length > 1 ? args[1] : null;
            if (act == "--start" && arg != null) { Select(arg); Start(); }
            else if (act == "--stop") Stop();
            else if (act == "--install" && arg != null) { Select(arg); Go("NavService"); InstallService(); }
            else if (act == "--remove") { Go("NavService"); RemoveService(); }
            else if (act == "--update") { Go("NavUpdate"); CheckUpdates(false, true); }
            else if (act == "--autopick") AutoPick();
            else if (act == "--autostart" && arg != null) { Go("NavService"); ToggleAutostart(arg == "on"); }
            else
            {
                // «включать обход при запуске»
                if (z != null && Settings.Get("autoon") == "1" && state == State.Off && Zapret.ServiceStatus() == null && Selected != null)
                {
                    Log("Автовключение: " + Selected);
                    Start();
                }
                if (z != null) CheckUpdates(true);   // тихая проверка: только запрос версии, без загрузки
            }
        }

        static string ExeDir { get { return System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); } }

        string FindDir()
        {
            foreach (var d in new[] { ExeDir, Settings.Get("dir"), System.IO.Path.Combine(ExeDir, "zapret-discord-youtube") })
                if (Zapret.IsZapretDir(d)) return d;
            return null;
        }

        void LoadDir(string dir)
        {
            z = dir == null ? null : new Zapret(dir) { Log = Log };
            F<Border>("NotFound").Visibility = z == null ? Visibility.Visible : Visibility.Collapsed;
            if (z == null)
            {
                F<TextBlock>("ChipVer").Text = "zapret не найден";
                F<TextBlock>("VerLocal").Text = "—";
                F<Button>("BtnUpdate").IsEnabled = false;
                Log("Папка zapret не найдена");
                return;
            }
            Settings.Set("dir", dir);
            string v = z.LocalVersion() ?? "?";
            F<TextBlock>("ChipVer").Text = "zapret " + v;
            F<TextBlock>("VerLocal").Text = v;
            Log("Папка zapret: " + dir + " (версия " + v + ")");

            var list = z.Strategies();
            var lb = F<ListBox>("StratList");
            lb.ItemsSource = list;
            F<TextBlock>("StratCount").Text = list.Count.ToString();
            Select(Settings.Get("strategy") ?? "general");
            if (lb.SelectedIndex < 0 && list.Count > 0) lb.SelectedIndex = 0;
            Refresh();
            UpdateHints();
            UpdateTray();
        }

        void PickDir()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Выберите service.bat в папке zapret", Filter = "service.bat|service.bat" };
            if (dlg.ShowDialog(W) == true)
            {
                string d = System.IO.Path.GetDirectoryName(dlg.FileName);
                if (Zapret.IsZapretDir(d)) LoadDir(d); else Toast("В этой папке нет bin\\winws.exe", Danger);
            }
        }

        string Selected { get { return F<ListBox>("StratList").SelectedItem as string; } }

        void Select(string name)
        {
            var lb = F<ListBox>("StratList");
            if (lb.ItemsSource == null) return;
            var item = ((List<string>)lb.ItemsSource).FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (item != null) { lb.SelectedItem = item; lb.ScrollIntoView(item); }
        }

        void UpdateHints()
        {
            F<TextBlock>("SvcHint").Text = Selected == null ? "" : "Служба будет установлена со стратегией, выбранной на главной: " + Selected;
        }

        // ---------- права администратора ----------

        bool EnsureAdmin(string action, string arg)
        {
            if (Zapret.IsAdmin()) return true;
            try
            {
                string a = action + (arg != null ? " \"" + arg + "\"" : "");
                Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, a) { UseShellExecute = true, Verb = "runas", WorkingDirectory = ExeDir });
                ExitApp();
            }
            catch (Win32Exception)
            {
                Toast("Нужны права администратора — запрос UAC отклонён", Warn);
            }
            return false;
        }

        // ---------- включение/выключение ----------

        void TogglePower()
        {
            if (picking) { pickCancel = true; Toast("Подбор остановлен — дождитесь окончания текущей проверки", Grey); return; }
            if (busy || z == null) return;
            if (state == State.Off) Start();
            else if (state == State.On) Stop();
            else if (state == State.Service) { Go("NavService"); Toast("zapret работает как служба. Управление — на вкладке «Служба»", Warn); }
        }

        async void Start()
        {
            string s = Selected;
            if (s == null || busy) return;
            if (Zapret.ServiceStatus() == "Running") { Toast("Уже запущена служба zapret — удалите её, чтобы запускать вручную", Warn); return; }
            if (!EnsureAdmin("--start", s)) return;
            busy = true;
            SetState(State.Starting, s);
            Log("Запуск…");
            try
            {
                int pid = await Task.Run(() =>
                {
                    if (Zapret.WinwsRunning()) Zapret.StopWinws();
                    return z.Start(s);
                });
                logPos = 0;
                await Task.Delay(900);
                bool alive = Process.GetProcesses().Any(p => p.Id == pid);
                TailWinwsLog();
                if (!alive) throw new Exception("winws.exe завершился сразу после запуска — смотрите журнал");
                Settings.Set("running", s);
                expectRun = true; runStrategy = s; ytFails = dcFails = 0;
                since = DateTime.Now;
                Settings.Set("since", since.Value.Ticks.ToString());
                Log("winws.exe запущен (PID " + pid + ")");
                F<TextBlock>("StateText").Text = "Проверка связи…";
                F<TextBlock>("StateSub").Text = "YouTube и Discord";
                int[] r = await RunPings();
                justPinged = true;
                SetState(State.On, s);
                Toast("Обход включён · " + s + (r[0] < 0 || r[1] < 0 ? " · не всё отвечает, попробуйте другую стратегию" : ""), r[0] < 0 || r[1] < 0 ? Warn : Mint);
            }
            catch (Exception e)
            {
                Log("Ошибка: " + e.Message);
                SetState(State.Off, null);
                Toast(e.Message, Danger);
            }
            busy = false;
        }

        async void Stop()
        {
            if (busy) return;
            if (!EnsureAdmin("--stop", null)) return;
            busy = true;
            expectRun = false;
            SetState(State.Stopping, null);
            try
            {
                await Task.Run(() => Zapret.StopWinws());
                Log("winws.exe остановлен");
                SetState(State.Off, null);
                Toast("Обход выключен", Grey);
            }
            catch (Exception e) { Log(e.Message); Toast(e.Message, Danger); }
            busy = false;
            Refresh();
        }

        void Refresh()
        {
            if (z == null || busy) return;
            string svc = Zapret.ServiceStatus();
            bool run = Zapret.WinwsRunning();
            if (svc == "Running") SetState(State.Service, Zapret.ServiceStrategy());
            else if (run)
            {
                if (!expectRun) { expectRun = true; runStrategy = Settings.Get("running"); }   // запущен до лаунчера
                SetState(State.On, Settings.Get("running"));
            }
            else if (expectRun && state == State.On) { WinwsDied(); return; }
            else SetState(State.Off, null);

            string strat = Zapret.ServiceStrategy();
            Color c = svc == "Running" ? Mint : svc == null ? Grey : Warn;
            F<TextBlock>("SvcState").Text = svc == null ? "Не установлена" : svc == "Running" ? "Работает" : "Установлена · " + RuStatus(svc);
            F<TextBlock>("SvcStrategy").Text = svc == null ? "Автозапуск выключен" : "Стратегия: " + (strat ?? "неизвестно") + " · запуск вместе с Windows";
            AnimColor(F<Ellipse>("SvcDot"), c);
            AnimColor(F<Ellipse>("SvcHalo"), c);
            F<Button>("BtnSvcRemove").IsEnabled = svc != null;
        }

        static string RuStatus(string s)
        {
            switch (s)
            {
                case "Stopped": return "остановлена";
                case "StartPending": return "запускается";
                case "StopPending": return "останавливается (возможен конфликт)";
                default: return s;
            }
        }

        void SetState(State s, string strategy)
        {
            var text = F<TextBlock>("StateText");
            var sub = F<TextBlock>("StateSub");
            if (s == state)
            {
                if (s == State.On || s == State.Service) sub.Text = SubFor(s, strategy);
                return;
            }
            State prev = state;
            state = s;
            bool lit = s == State.On || s == State.Service;

            // таймер и пинги
            Fade(F<FrameworkElement>("UptimeBox"), lit);
            if (lit)
            {
                if (since == null) since = DetectSince(s);
                TickClock();
                if (!justPinged) RunPings();   // при ручном старте пинги уже прошли в анимации
                justPinged = false;
            }
            else if (s == State.Off)
            {
                since = null;
                Settings.Set("since", null);
                F<TextBlock>("Uptime").Text = "00:00:00";
                Fade(F<FrameworkElement>("Pings"), false);
            }
            bool spinning = s == State.Starting || s == State.Stopping;

            AnimStop(core1, lit ? C("#22D9A0") : spinning ? C("#2B3243") : C("#262B3A"), 500);
            AnimStop(core2, lit ? C("#2F6FF0") : spinning ? C("#1C2130") : C("#171B25"), 500);
            AnimBrush(glyph, lit ? Colors.White : spinning ? C("#C9D1E0") : C("#7D8699"), 400);

            var glow = F<Ellipse>("Glow");
            glow.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(lit ? 1 : 0, TimeSpan.FromMilliseconds(lit ? 700 : 350)) { EasingFunction = Ease });
            if (lit)
            {
                var pulse = new DoubleAnimation(0.94, 1.06, TimeSpan.FromSeconds(1.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
                glowScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
                glowScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
            }
            else
            {
                glowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                glowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            }

            var sp = F<Ellipse>("Spinner");
            sp.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(spinning ? 1 : 0, TimeSpan.FromMilliseconds(250)));
            if (spinning) spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
            else spin.BeginAnimation(RotateTransform.AngleProperty, null);

            text.Text = s == State.Off ? "Выключено" : s == State.Starting ? "Запуск…" : s == State.Stopping ? "Остановка…" : s == State.Service ? "Работает как служба" : "Работает";
            sub.Text = s == State.Off ? "Нажмите, чтобы запустить" : spinning ? "Секунду" : SubFor(s, strategy);
            FadeIn(text, 6); FadeIn(sub, 6);
            UpdateTray();
        }

        // ---------- таймер ----------

        DateTime DetectSince(State s)
        {
            // точнее всего — время старта winws (без прав для чужого процесса недоступно)
            try
            {
                var ps = Process.GetProcessesByName("winws");
                if (ps.Length > 0) return ps.Min(p => p.StartTime);
            }
            catch { }
            string saved = Settings.Get("since");
            long ticks;
            if (s == State.On && saved != null && long.TryParse(saved, out ticks)) return new DateTime(ticks);
            return DateTime.Now;
        }

        void TickClock()
        {
            clockTicks++;
            bool lit = state == State.On || state == State.Service;
            if (!lit || since == null) return;
            var d = DateTime.Now - since.Value;
            if (d < TimeSpan.Zero) d = TimeSpan.Zero;
            F<TextBlock>("Uptime").Text = (d.Days > 0 ? d.Days + "д " : "") + d.ToString(@"hh\:mm\:ss");
            UpdateTray();
            if (clockTicks % 30 == 0 && !busy) PeriodicPing();   // обновляем пинги раз в 30 секунд
        }

        // ---------- пинги ----------

        async Task<int[]> RunPings()
        {
            if (pinging) return new[] { 0, 0 };
            pinging = true;
            Fade(F<FrameworkElement>("Pings"), true);
            var yt = PingOne("https://www.youtube.com/generate_204", "DotYt", "MsYt");
            var dc = PingOne("https://discord.com/api/v9/gateway", "DotDc", "MsDc");
            var r = await Task.WhenAll(yt, dc);
            pinging = false;
            Log("Пинг: YouTube " + (r[0] < 0 ? "нет ответа" : r[0] + " мс") + ", Discord " + (r[1] < 0 ? "нет ответа" : r[1] + " мс"));
            return r;
        }

        async Task<int> PingOne(string url, string dotName, string textName)
        {
            var dot = F<Ellipse>(dotName);
            var txt = F<TextBlock>(textName);
            var brush = dot.Fill as SolidColorBrush;
            if (brush == null || brush.IsFrozen) dot.Fill = brush = new SolidColorBrush(Grey);
            var sc = new ScaleTransform(1, 1);
            dot.RenderTransform = sc;
            // пока ждём — точка «дышит»
            dot.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.2, TimeSpan.FromMilliseconds(450)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            AnimBrush(brush, Grey, 200);
            txt.Text = "…";

            int ms = await Task.Run(() => Zapret.Probe(url));

            dot.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
            AnimBrush(brush, ms < 0 ? Danger : ms < 400 ? Mint : Warn, 400);
            sc.ScaleX = sc.ScaleY = 1.9;   // «хлопок» точки при получении ответа
            Scale(sc, 1, 600, new ElasticEase { Oscillations = 2, Springiness = 3 });
            if (ms < 0) txt.Text = "нет ответа";
            else CountUp(txt, ms);
            return ms;
        }

        // число «набегает» от 0 до значения
        static void CountUp(TextBlock t, int to)
        {
            var sw = Stopwatch.StartNew();
            var tm = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            tm.Tick += (s, e) =>
            {
                double k = Math.Min(1, sw.ElapsedMilliseconds / 600.0);
                t.Text = (int)Math.Round(to * (1 - Math.Pow(1 - k, 3))) + " мс";
                if (k >= 1) tm.Stop();
            };
            tm.Start();
        }

        static void Fade(UIElement e, bool show)
        {
            e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 400 : 250)) { EasingFunction = Ease });
        }

        static string SubFor(State s, string strategy)
        {
            return (strategy ?? "стратегия неизвестна") + (s == State.Service ? " · автозапуск" : " · нажмите, чтобы выключить");
        }

        // ---------- автоподбор стратегии ----------

        // По очереди запускает каждую стратегию, пингует YouTube и Discord, включает лучшую.
        async void AutoPick()
        {
            if (z == null || busy) return;
            if (Zapret.ServiceStatus() == "Running") { Toast("Работает служба zapret — удалите её, чтобы подбирать стратегию", Warn); return; }
            if (!EnsureAdmin("--autopick", null)) return;
            Go("NavHome");
            busy = picking = true; pickCancel = false; expectRun = false;
            var btn = F<Button>("BtnPick");
            btn.IsEnabled = false; btn.Content = "Подбор…";
            var list = z.Strategies();
            SetState(State.Starting, null);
            Log("Автоподбор: " + list.Count + " стратегий");

            string best = null;
            int bestScore = int.MaxValue;
            for (int i = 0; i < list.Count && !pickCancel; i++)
            {
                string s = list[i];
                Select(s);
                F<TextBlock>("StateText").Text = "Подбор стратегии…";
                F<TextBlock>("StateSub").Text = (i + 1) + " из " + list.Count + " · " + s;
                int yt = -1, dc = -1;
                try
                {
                    await Task.Run(() => { if (Zapret.WinwsRunning()) Zapret.StopWinws(); z.Start(s); });
                    await Task.Delay(1200);
                    var r = await RunPings();
                    yt = r[0]; dc = r[1];
                }
                catch (Exception e) { Log(s + ": " + e.Message); }
                // обе службы обязательны; среди рабочих — с наименьшим суммарным откликом
                int score = (yt < 0 ? 100000 : yt) + (dc < 0 ? 100000 : dc);
                if (score < bestScore) { bestScore = score; best = s; }
            }
            try { await Task.Run(() => Zapret.StopWinws()); } catch { }

            busy = picking = false;
            btn.IsEnabled = true; btn.Content = "Подобрать";
            if (best != null && bestScore < 100000)
            {
                Log("Автоподбор: лучшая — " + best + " (" + bestScore + " мс суммарно)");
                Toast("Лучшая стратегия: " + best, Mint);
                Select(best);
                Start();
            }
            else
            {
                SetState(State.Off, null);
                if (best != null && bestScore < 200000) Select(best);
                Toast(pickCancel ? "Подбор остановлен" : "Ни одна стратегия не открыла и YouTube, и Discord", pickCancel ? Grey : Warn);
            }
        }

        // ---------- сторож ----------

        void WinwsDied()
        {
            expectRun = false;
            restarts.RemoveAll(t => (DateTime.Now - t).TotalMinutes > 5);
            bool watch = Settings.Get("watch") != "0";
            if (watch && Zapret.IsAdmin() && restarts.Count < 3 && runStrategy != null)
            {
                restarts.Add(DateTime.Now);
                Log("Сторож: winws.exe остановился, перезапуск (" + restarts.Count + "/3)");
                Toast("winws остановился — сторож перезапускает обход", Warn);
                SetState(State.Off, null);
                Select(runStrategy);
                Start();
            }
            else
            {
                Log("winws.exe остановился");
                SetState(State.Off, null);
                Toast("winws.exe остановился" + (watch && restarts.Count >= 3 ? " — сторож сдался после 3 попыток за 5 минут" : ""), Danger);
            }
        }

        async void PeriodicPing()
        {
            var r = await RunPings();
            if (Settings.Get("watch") == "0" || busy || !(state == State.On || state == State.Service)) return;
            ytFails = r[0] < 0 ? ytFails + 1 : 0;
            dcFails = r[1] < 0 ? dcFails + 1 : 0;
            if (ytFails == 2 || dcFails == 2)   // две проверки подряд (~минута) — предупреждаем один раз
            {
                string who = ytFails >= 2 && dcFails >= 2 ? "YouTube и Discord" : ytFails >= 2 ? "YouTube" : "Discord";
                Log("Сторож: " + who + " не отвечает");
                Toast(who + " не отвечает уже минуту — попробуйте «Подобрать» стратегию", Warn);
            }
        }

        // ---------- автозапуск ----------

        bool syncingAutostart;

        async void SyncAutostart()
        {
            if (syncingAutostart) return;
            syncingAutostart = true;
            bool on = await Task.Run(() => Autostart.Enabled());
            F<CheckBox>("TglAutostart").IsChecked = on;
            syncingAutostart = false;
        }

        async void ToggleAutostart(bool on)
        {
            var box = F<CheckBox>("TglAutostart");
            if (!Zapret.IsAdmin())
            {
                box.IsChecked = !on;   // применится после перезапуска с правами
                EnsureAdmin("--autostart", on ? "on" : "off");
                return;
            }
            box.IsEnabled = false;
            try
            {
                string exe = Assembly.GetExecutingAssembly().Location;
                await Task.Run(() => Autostart.Set(on, exe));
                box.IsChecked = on;
                Log(on ? "Автозапуск включён: " + exe : "Автозапуск выключен");
                Toast(on ? "Лаунчер будет запускаться с Windows в трей" : "Автозапуск выключен", on ? Mint : Grey);
            }
            catch (Exception e)
            {
                box.IsChecked = !on;
                Log(e.Message);
                Toast(e.Message, Danger);
            }
            box.IsEnabled = true;
        }

        // ---------- свои сайты ----------

        public class DomainItem
        {
            public string Name { get; set; }
            public string Kind { get; set; }
            public bool Exclude;
        }

        const string Placeholder = "domain.example.abc";

        string UserList(bool exclude) { return System.IO.Path.Combine(z.ListsDir, exclude ? "list-exclude-user.txt" : "list-general-user.txt"); }

        List<string> ReadDomains(bool exclude)
        {
            try
            {
                return File.ReadAllLines(UserList(exclude)).Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !l.StartsWith("#") && l != Placeholder).ToList();
            }
            catch { return new List<string>(); }
        }

        void WriteDomains(bool exclude, List<string> domains)
        {
            string file = UserList(exclude);
            var lines = File.Exists(file) ? File.ReadAllLines(file).Where(l => l.TrimStart().StartsWith("#")).ToList() : new List<string>();
            lines.AddRange(domains.Count > 0 ? domains : new List<string> { Placeholder });   // zapret не любит пустые списки
            File.WriteAllLines(file, lines);
        }

        void RefreshDomains()
        {
            if (z == null) return;
            var items = ReadDomains(false).Select(d => new DomainItem { Name = d, Kind = "в обходе" })
                .Concat(ReadDomains(true).Select(d => new DomainItem { Name = d, Kind = "исключение", Exclude = true })).ToList();
            F<ListBox>("DomList").ItemsSource = items;
            F<TextBlock>("DomEmpty").Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        static string NormalizeDomain(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            s = Regex.Replace(s, @"^[a-z]+://", "");
            int cut = s.IndexOfAny(new[] { '/', '?', '#', ':' });
            if (cut >= 0) s = s.Substring(0, cut);
            if (s.StartsWith("www.")) s = s.Substring(4);
            s = s.Trim('.');
            try { s = new System.Globalization.IdnMapping().GetAscii(s); } catch { return null; }   // сайт.рф → xn--80aswg.xn--p1ai
            return Regex.IsMatch(s, @"^([a-z0-9-]+\.)+([a-z]{2,}|xn--[a-z0-9-]+)$") ? s : null;
        }

        void AddDomain(bool exclude)
        {
            if (z == null) return;
            var input = F<TextBox>("DomInput");
            string d = NormalizeDomain(input.Text);
            if (d == null) { Toast("Не похоже на адрес сайта: " + input.Text.Trim(), Warn); return; }
            try
            {
                var mine = ReadDomains(exclude);
                var other = ReadDomains(!exclude);
                if (mine.Contains(d)) { Toast(d + " уже в списке", Grey); return; }
                mine.Add(d);
                WriteDomains(exclude, mine);
                if (other.Remove(d)) WriteDomains(!exclude, other);   // сайт не может быть и там, и там
                input.Clear();
                Log((exclude ? "В исключения: " : "В обход: ") + d);
                RefreshDomains();
                ApplyLists(d + (exclude ? " — в исключениях" : " — в обходе"));
            }
            catch (Exception e) { Toast("Не удалось сохранить список: " + e.Message, Danger); }
        }

        void RemoveDomain()
        {
            var item = F<ListBox>("DomList").SelectedItem as DomainItem;
            if (item == null) return;
            try
            {
                var list = ReadDomains(item.Exclude);
                list.Remove(item.Name);
                WriteDomains(item.Exclude, list);
                Log("Удалён из списка: " + item.Name);
                RefreshDomains();
                ApplyLists(item.Name + " удалён");
            }
            catch (Exception e) { Toast("Не удалось сохранить список: " + e.Message, Danger); }
        }

        // Готовые наборы: домены в обход (и из исключений долой) + при необходимости Game Filter
        void AddPreset(string title, string[] domains, bool gameFilter)
        {
            if (z == null) return;
            try
            {
                var mine = ReadDomains(false);
                var ex = ReadDomains(true);
                int added = 0;
                foreach (var d in domains)
                {
                    if (!mine.Contains(d)) { mine.Add(d); added++; }
                    ex.Remove(d);
                }
                WriteDomains(false, mine);
                WriteDomains(true, ex);
                bool gf = gameFilter && EnableGameFilter();
                Log(title + ": " + string.Join(", ", domains) + (gf ? " + Game Filter (TCP и UDP)" : ""));
                RefreshDomains();
                ApplyLists(title + (added > 0 ? " добавлен" : " уже в списке") + (gf ? " · Game Filter включён" : ""));
            }
            catch (Exception e) { Toast("Не удалось сохранить: " + e.Message, Danger); }
        }

        // Тот же файл, что пишет меню service.bat (пункт Game Filter): mode/tcp/udp
        bool EnableGameFilter()
        {
            string file = System.IO.Path.Combine(z.Dir, "utils", "game_filter.enabled");
            var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(file))
                foreach (var l in File.ReadAllLines(file))
                {
                    int i = l.IndexOf('=');
                    if (i > 0) kv[l.Substring(0, i).Trim()] = l.Substring(i + 1).Trim();
                    else if (l.Trim().Length > 0) kv["mode"] = l.Trim();   // старый формат: просто «all»
                }
            string mode;
            if (kv.TryGetValue("mode", out mode) && mode.Equals("all", StringComparison.OrdinalIgnoreCase)) return false;
            string tcp, udp;
            if (!kv.TryGetValue("tcp", out tcp) || tcp.Length == 0) tcp = "1024-65535";
            if (!kv.TryGetValue("udp", out udp) || udp.Length == 0) udp = "1024-65535";
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
            File.WriteAllLines(file, new[] { "mode=all", "tcp=" + tcp, "udp=" + udp });
            return true;
        }

        // winws читает списки при старте — перезапускаем, если он работает
        void ApplyLists(string what)
        {
            if (state == State.On && Zapret.IsAdmin() && !busy && runStrategy != null)
            {
                Toast(what + " · перезапускаю обход", Mint);
                Select(runStrategy);
                Start();
            }
            else if (state == State.On) Toast(what + " · перезапустите обход, чтобы применить", Warn);
            else if (state == State.Service) Toast(what + " · переустановите службу, чтобы применить", Warn);
            else Toast(what, Mint);
        }

        // ---------- служба ----------

        async void InstallService()
        {
            string s = Selected;
            if (z == null || busy) return;
            if (s == null) { Toast("Выберите стратегию на главной", Warn); return; }
            if (!EnsureAdmin("--install", s)) return;
            busy = true; SetButtons(false);
            expectRun = false;   // winws остановим сами — сторожу не вмешиваться
            SetState(State.Starting, s);
            try
            {
                await Task.Run(() =>
                {
                    if (Zapret.WinwsRunning()) Zapret.StopWinws();
                    z.InstallService(s);
                });
                Toast("Служба установлена · " + s, Mint);
            }
            catch (Exception e) { Log("Ошибка: " + e.Message); Toast(e.Message, Danger); }
            busy = false; SetButtons(true);
            state = State.Starting; Refresh();
        }

        async void RemoveService()
        {
            if (z == null || busy) return;
            if (!EnsureAdmin("--remove", null)) return;
            busy = true; SetButtons(false);
            expectRun = false;   // winws остановим сами — сторожу не вмешиваться
            SetState(State.Stopping, null);
            try
            {
                await Task.Run(() => z.RemoveService());
                Toast("Служба удалена", Grey);
            }
            catch (Exception e) { Log("Ошибка: " + e.Message); Toast(e.Message, Danger); }
            busy = false; SetButtons(true);
            Refresh();
        }

        void SetButtons(bool on)
        {
            foreach (var n in new[] { "BtnSvcInstall", "BtnSvcRemove", "BtnSvcMenu", "BtnUpdate", "BtnCheck" })
                F<Button>(n).IsEnabled = on;
        }

        // ---------- обновления ----------

        async void CheckUpdates(bool quiet, bool thenInstall = false)
        {
            var btn = F<Button>("BtnCheck");
            btn.IsEnabled = false;
            if (!quiet) F<TextBlock>("VerRemote").Text = "проверка…";
            try
            {
                release = await Task.Run(() => Zapret.Latest());
                string local = z != null ? z.LocalVersion() : null;
                bool newer = local == null || Zapret.CompareVersions(release.Tag, local) > 0;
                F<TextBlock>("VerRemote").Text = release.Tag;
                F<TextBlock>("VerRemoteLabel").Text = "Последний релиз" + (release.Date != null && release.Date.Length >= 10 ? " · " + release.Date.Substring(0, 10) : "");
                F<TextBox>("Changelog").Text = Pretty(release.Body);
                F<Button>("BtnUpdate").IsEnabled = newer && release.ZipUrl != null;
                F<Button>("BtnUpdate").Content = local == null ? "Скачать" : newer ? "Обновить до " + release.Tag : "Установлена последняя версия";
                F<Ellipse>("UpdDot").Visibility = newer && local != null ? Visibility.Visible : Visibility.Collapsed;
                if (newer && local != null) { Log("Доступна новая версия " + release.Tag); if (quiet) Toast("Доступна новая версия zapret " + release.Tag, Blue); }
                else if (!quiet) Toast("Установлена последняя версия", Mint);
                if (thenInstall && newer) InstallUpdate();
            }
            catch (Exception e)
            {
                F<TextBlock>("VerRemote").Text = "ошибка";
                if (!quiet) Toast("Не удалось проверить: " + Root(e).Message, Danger);
                Log("Проверка обновлений: " + Root(e).Message);
            }
            btn.IsEnabled = true;
        }

        static Exception Root(Exception e) { while (e.InnerException != null) e = e.InnerException; return e; }

        static string Pretty(string md)
        {
            if (string.IsNullOrEmpty(md)) return "Описание релиза пустое.";
            string s = md.Replace("\r", "").Replace("**", "").Replace("`", "");
            s = Regex.Replace(s, @"^(\s*)[\*\-] ", m => m.Groups[1].Value.Length > 0 ? m.Groups[1].Value + "◦ " : "• ", RegexOptions.Multiline);
            s = Regex.Replace(s, @"^#+\s*", "", RegexOptions.Multiline);
            s = Regex.Replace(s, @"\s+in https://github\.com/\S+/pull/(\d+)", " (#$1)");
            s = Regex.Replace(s, @"\[([^\]]+)\]\(([^)]+)\)", "$1");
            return s.Trim();
        }

        async void InstallUpdate()
        {
            if (busy) return;
            if (release == null) { CheckUpdates(false, true); return; }
            if (release.ZipUrl == null) { Toast("В релизе нет zip-архива", Danger); return; }

            bool firstInstall = z == null;
            string dir = firstInstall ? System.IO.Path.Combine(ExeDir, "zapret-discord-youtube") : z.Dir;
            string svc = Zapret.ServiceStatus(), svcStrategy = Zapret.ServiceStrategy();
            bool needAdmin = !firstInstall && (Zapret.WinwsRunning() || svc != null);
            if (needAdmin && !EnsureAdmin("--update", null)) return;

            busy = true; SetButtons(false);
            expectRun = false;   // winws остановим сами — сторожу не вмешиваться
            Go("NavUpdate");
            var bar = F<ProgressBar>("UpdProgress");
            bar.Value = 0; bar.Visibility = Visibility.Visible;
            string wasRunning = state == State.On ? Settings.Get("running") : null;
            try
            {
                Log("Загрузка " + release.ZipUrl);
                string zip = await Task.Run(() => Zapret.Download(release, p => W.Dispatcher.BeginInvoke((Action)(() => bar.Value = p))));
                Log("Установка в " + dir);
                await Task.Run(() =>
                {
                    if (svc == "Running") Zapret.StopService();
                    if (Zapret.WinwsRunning()) Zapret.StopWinws();
                    if (Zapret.IsAdmin()) Zapret.StopDriver();
                    Zapret.ApplyZip(zip, dir);
                    try { File.Delete(zip); } catch { }
                });
                LoadDir(dir);
                F<Ellipse>("UpdDot").Visibility = Visibility.Collapsed;
                F<Button>("BtnUpdate").IsEnabled = false;
                F<Button>("BtnUpdate").Content = "Установлена последняя версия";
                Log("Готово: zapret " + release.Tag);
                Toast("zapret обновлён до " + release.Tag, Mint);

                if (svc != null && svcStrategy != null && z.Strategies().Contains(svcStrategy, StringComparer.OrdinalIgnoreCase))
                {
                    Log("Переустановка службы со стратегией " + svcStrategy);
                    await Task.Run(() => z.InstallService(svcStrategy));
                }
                else if (svc != null) Toast("Стратегии службы больше нет — установите службу заново", Warn);
                else if (wasRunning != null) { busy = false; Select(wasRunning); Start(); }
            }
            catch (Exception e)
            {
                Log("Ошибка обновления: " + Root(e).Message);
                Toast("Ошибка обновления: " + Root(e).Message, Danger);
            }
            bar.Visibility = Visibility.Collapsed;
            busy = false; SetButtons(true);
            Refresh();
        }

        // ---------- Telegram ----------

        bool tgSearching;
        TgProxy SelectedProxy { get { return F<ListBox>("TgList").SelectedItem as TgProxy; } }

        void RefreshTg()
        {
            string exe = Telegram.FindExe();
            bool run = Telegram.Running();
            // путь к exe не показываем — в нём имя пользователя
            F<TextBlock>("TgState").Text = run ? "Telegram запущен" : exe != null ? "Telegram не запущен" : "Telegram Desktop не найден";
            F<TextBlock>("TgSub").Text = run ? "Можно подключать прокси" : exe != null ? "Запустится сам при подключении прокси" : "Ссылка на прокси откроется в браузере (t.me)";
        }

        async void FindProxies()
        {
            if (tgSearching) return;
            tgSearching = true;
            var btn = F<Button>("BtnTgFind");
            var bar = F<ProgressBar>("TgProgress");
            var list = F<ListBox>("TgList");
            var count = F<TextBlock>("TgCount");
            var empty = F<TextBlock>("TgEmpty");
            btn.IsEnabled = false;
            btn.Content = "Поиск…";
            bar.Value = 0; bar.Visibility = Visibility.Visible;
            count.Text = "загрузка списков…";
            empty.Text = "";
            try
            {
                var all = await Task.Run(() => Telegram.Fetch(Log));
                if (all.Count == 0) throw new Exception("списки прокси недоступны");
                bar.Maximum = all.Count;
                Log("Проверка " + all.Count + " прокси…");
                var alive = await Telegram.CheckAll(all, (done, ok) => W.Dispatcher.BeginInvoke((Action)(() =>
                {
                    bar.Value = done;
                    count.Text = "проверено " + done + " из " + all.Count + " · отвечают " + ok;
                })));
                var top = alive.Take(30).ToList();
                list.ItemsSource = top;
                FadeIn(list, 10);
                count.Text = alive.Count + " из " + all.Count + " отвечают";
                empty.Text = top.Count == 0 ? "Ни один прокси не ответил — попробуйте позже" : "";
                if (top.Count > 0)
                {
                    list.SelectedIndex = 0;
                    Log("Лучший прокси: " + top[0].Host + " (" + top[0].Ms + " мс)");
                    Toast("Найдено рабочих прокси: " + alive.Count + " · лучший " + top[0].Ms + " мс", Mint);
                }
                else Toast("Рабочих прокси не найдено", Warn);
            }
            catch (Exception e)
            {
                count.Text = "";
                empty.Text = "Не удалось: " + Root(e).Message;
                Toast("Поиск прокси: " + Root(e).Message, Danger);
            }
            bar.Visibility = Visibility.Collapsed;
            btn.Content = "Найти заново";
            btn.IsEnabled = true;
            tgSearching = false;
        }

        void ConnectProxy()
        {
            var p = SelectedProxy;
            if (p == null) return;
            Log("Подключение прокси " + p.Host + " в Telegram");
            try
            {
                // tg:// открывает Telegram Desktop с запросом «Подключить прокси?»
                Process.Start(new ProcessStartInfo(p.TgLink) { UseShellExecute = true });
                Toast("Подтвердите подключение в окне Telegram", Blue);
            }
            catch
            {
                Shell(p.WebLink);   // Telegram не установлен — открываем t.me в браузере
            }
        }

        void CopyProxy()
        {
            var p = SelectedProxy;
            if (p == null) return;
            try { Clipboard.SetText(p.WebLink); Toast("Ссылка скопирована", Mint); }
            catch (Exception e) { Toast(e.Message, Danger); }
        }

        // ---------- журнал ----------

        void Log(string msg)
        {
            if (!W.Dispatcher.CheckAccess()) { W.Dispatcher.BeginInvoke((Action)(() => Log(msg))); return; }
            var box = F<TextBox>("LogBox");
            if (box.Text.Length > 200000) box.Text = box.Text.Substring(100000);
            box.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + msg + "\n");
            box.ScrollToEnd();
        }

        void TailWinwsLog()
        {
            try
            {
                string p = Zapret.WinwsLogPath;
                if (!File.Exists(p)) return;
                using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length < logPos) logPos = 0;
                    if (fs.Length == logPos) return;
                    fs.Seek(logPos, SeekOrigin.Begin);
                    var bytes = new byte[Math.Min(fs.Length - logPos, 65536)];
                    int n = fs.Read(bytes, 0, bytes.Length);
                    logPos += n;
                    foreach (var l in Encoding.UTF8.GetString(bytes, 0, n).Split('\n'))
                        if (l.Trim().Length > 0) Log("winws: " + l.TrimEnd('\r'));
                }
            }
            catch { }
        }

        // ---------- анимации и мелочи ----------

        void Nav(string radio, string pageName)
        {
            F<RadioButton>(radio).Checked += (s, e) => ShowPage(F<FrameworkElement>(pageName));
        }

        void Go(string radio) { F<RadioButton>(radio).IsChecked = true; }

        void ShowPage(FrameworkElement p)
        {
            if (p == page) return;
            if (page != null) page.Visibility = Visibility.Collapsed;
            page = p;
            p.Visibility = Visibility.Visible;
            FadeIn(p, 14);
            if (p.Name == "PageLog") F<TextBox>("LogBox").ScrollToEnd();
        }

        static void FadeIn(FrameworkElement e, double dy)
        {
            var tt = new TranslateTransform(0, dy);
            e.RenderTransform = tt;
            e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = Ease });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = Ease });
        }

        static void Scale(ScaleTransform t, double to, int ms, IEasingFunction ease = null)
        {
            var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease ?? Ease };
            t.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            t.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        static void AnimStop(GradientStop g, Color to, int ms)
        {
            g.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Ease });
        }

        static void AnimBrush(SolidColorBrush b, Color to, int ms)
        {
            b.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Ease });
        }

        static void AnimColor(Shape s, Color to)
        {
            var b = s.Fill as SolidColorBrush;
            if (b == null || b.IsFrozen) s.Fill = b = new SolidColorBrush(b != null ? b.Color : to);
            if (b.Color != to) AnimBrush(b, to, 400);
        }

        void Toast(string msg, Color c)
        {
            F<TextBlock>("ToastText").Text = msg;
            F<Ellipse>("ToastDot").Fill = new SolidColorBrush(c);
            var t = F<Border>("Toast");
            t.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)) { EasingFunction = Ease });
            toastShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(380)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } });
            toastTimer.Stop(); toastTimer.Start();
            if (!W.IsVisible && tray != null && c != Grey) tray.Balloon("Zapret Launcher", msg);   // окно в трее — дублируем уведомлением
        }

        void HideToast()
        {
            F<Border>("Toast").BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)));
            toastShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, TimeSpan.FromMilliseconds(300)) { EasingFunction = Ease });
        }

        void Shell(string target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception e) { Toast(e.Message, Danger); }
        }
    }

    // Простые настройки в %APPDATA%\ZapretLauncher\settings.ini
    static class Settings
    {
        static readonly string file = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZapretLauncher", "settings.ini");
        static Dictionary<string, string> data;

        static Dictionary<string, string> Data
        {
            get
            {
                if (data != null) return data;
                data = new Dictionary<string, string>();
                try
                {
                    foreach (var l in File.ReadAllLines(file))
                    {
                        int i = l.IndexOf('=');
                        if (i > 0) data[l.Substring(0, i)] = l.Substring(i + 1);
                    }
                }
                catch { }
                return data;
            }
        }

        public static string Get(string k) { string v; return Data.TryGetValue(k, out v) && v.Length > 0 ? v : null; }
        public static void Set(string k, string v) { Data[k] = v ?? ""; Save(); }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
                File.WriteAllLines(file, Data.Select(p => p.Key + "=" + p.Value));
            }
            catch { }
        }
    }
}
