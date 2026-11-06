using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace VoltCore
{
    public class MainWindow : Window
    {
        private readonly Grid _root;
        private readonly Dictionary<Tweak, CheckBox> _checks = new Dictionary<Tweak, CheckBox>();
        private readonly Dictionary<Tweak, TextBlock> _badges = new Dictionary<Tweak, TextBlock>();
        private readonly CpuMeter _cpu = new CpuMeter();
        private readonly GameWatcher _watcher = new GameWatcher();
        private List<GameEntry> _games = new List<GameEntry>();
        private List<CleanTarget> _clean;
        private readonly Dictionary<CleanTarget, Tuple<CheckBox, TextBlock>> _cleanUi = new Dictionary<CleanTarget, Tuple<CheckBox, TextBlock>>();
        private List<CheckItem> _checkup;
        private HwInfo _hw;
        private double _freedSession;
        private DateTime _lastAutoClean = DateTime.MinValue;
        private int _autoCleans;
        private WinForms.NotifyIcon _tray;
        private bool _exiting;
        private int _tick;

        public MainWindow()
        {
            Title = "VoltCore Optimizer";
            Width = 1240; Height = 800; MinWidth = 1000; MinHeight = 640;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(7, 11, 20));
            WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 44, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false });
            try { Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location).Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions()); } catch { }

            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml"))
                _root = (Grid)XamlReader.Load(s);
            Content = _root;
            StateChanged += (a, b) => { _root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0); };

            WireChrome();
            WireNav();
            BuildTweakPage("debloat", F<StackPanel>("DebloatList"));
            BuildTweakPage("perf", F<StackPanel>("PerfList"));
            BuildBoostList();
            WireActions();
            DrawGauge(0);

            F<TextBlock>("Hello").Text = "Olá, " + Environment.UserName + "!";
            bool admin = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            if (!admin)
            {
                var pill = F<Border>("AdminPill");
                pill.Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x14, 0x14));
                ((TextBlock)pill.Child).Text = "● Sem administrador — ajustes não serão aplicados";
                ((TextBlock)pill.Child).Foreground = Res("Bad");
            }
            F<CheckBox>("MemAuto").IsChecked = Settings.GetBool("mem_auto", false);
            F<TextBox>("MemStandbyLimit").Text = Settings.GetInt("mem_standby", 1024).ToString();
            F<TextBox>("MemFreeLimit").Text = Settings.GetInt("mem_free", 2048).ToString();

            WireSettings();
            if (Settings.Get("link_since", "").Length == 0) Settings.Set("link_since", DateTime.Now.ToString("dd/MM HH:mm"));
            WireLink();

            _games = Games.Load();
            _watcher.Games = _games;
            _watcher.Message += m => Dispatcher.BeginInvoke(new Action(() => Status("🎮 " + m)));
            BuildGames();

            SetupTray();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (a, b) => OnTick();
            timer.Start();

            Loaded += async (a, b) => await InitialLoad();
            Closing += OnClosing;
        }

        private T F<T>(string name) where T : class { return _root.FindName(name) as T; }
        private Brush Res(string key) { return (Brush)_root.FindResource(key); }
        private Style St(string key) { return (Style)_root.FindResource(key); }

        private void Status(string s)
        {
            F<TextBlock>("StatusText").Text = s;
        }

        // ================== Janela / navegação ==================
        private void WireChrome()
        {
            F<Button>("BtnMin").Click += (a, b) => WindowState = WindowState.Minimized;
            F<Button>("BtnMax").Click += (a, b) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            F<Button>("BtnClose").Click += (a, b) => Close();
        }

        private void WireNav()
        {
            foreach (var rb in F<StackPanel>("NavPanel").Children.OfType<RadioButton>())
            {
                var r = rb;
                r.Checked += (a, b) => ShowPage((string)r.Tag);
            }
        }

        private static readonly string[] Pages = { "PageHome", "PageBoost", "PageDebloat", "PagePerf", "PageMem", "PageWin", "PageGames", "PageHw", "PageRestore", "PageSettings" };

        private void ShowPage(string page)
        {
            foreach (var p in Pages)
            {
                var el = F<FrameworkElement>(p);
                if (el != null) el.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
            }
            if (page == "PageWin" && F<StackPanel>("StartList").Children.Count == 0) LoadStartup();
            if (page == "PageWin" && _clean == null) BuildCleaner();
            if (page == "PageRestore") LoadRestorePoints();
            if (page == "PageMem") RefreshTopProcesses();
            if (page == "PageHw") RefreshLink();
        }

        /// <summary>Modo de desenvolvimento: salva um PNG de cada tela (usado para a documentação).</summary>
        public async Task CaptureScreens(string dir)
        {
            Directory.CreateDirectory(dir);
            F<Border>("AdminPill").Visibility = Visibility.Collapsed;
            await Task.Delay(9000);
            foreach (var p in Pages)
            {
                Go(p);
                await Task.Delay(p == "PageRestore" || p == "PageWin" || p == "PageHw" ? 5000 : 1200);
                UpdateLayout();
                var src = PresentationSource.FromVisual(this);
                double sx = src != null ? src.CompositionTarget.TransformToDevice.M11 : 1;
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(ActualWidth * sx), (int)(ActualHeight * sx), 96 * sx, 96 * sx, PixelFormats.Pbgra32);
                bmp.Render(_root);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using (var fs = File.Create(Path.Combine(dir, p.Substring(4).ToLowerInvariant() + ".png"))) enc.Save(fs);
            }
            _exiting = true;
            Close();
        }

        private void Go(string page)
        {
            foreach (var rb in F<StackPanel>("NavPanel").Children.OfType<RadioButton>())
                if ((string)rb.Tag == page) rb.IsChecked = true;
        }

        // ================== Carga inicial ==================
        private async Task InitialLoad()
        {
            Status("Analisando o sistema…");
            await Task.Run(() => TweakEngine.RefreshAppx());
            await RefreshTweakStates();
            await RefreshHardware();
            RefreshRestoreSummary();
            var auto = F<CheckBox>("SetAutostart");
            auto.IsChecked = await Task.Run(() => { AutoStart.Repair(); return AutoStart.IsEnabled(); });
            auto.Checked += async (a, b) => { var err = await Task.Run(() => AutoStart.Set(true)); F<TextBlock>("SetStatus").Text = err.Length == 0 ? "O VoltCore vai abrir na bandeja quando você entrar no Windows." : err; };
            auto.Unchecked += async (a, b) => { await Task.Run(() => AutoStart.Set(false)); F<TextBlock>("SetStatus").Text = "Início automático desligado."; };
            F<CheckBox>("WeeklyToggle").IsChecked = await Task.Run(() => Maintenance.IsScheduled());
            F<CheckBox>("WeeklyToggle").Checked += async (a, b) => { var ok = await Task.Run(() => Maintenance.Schedule(true)); Status(ok ? "Revisão semanal agendada para domingo, 12:00." : "Não foi possível agendar a revisão semanal."); };
            F<CheckBox>("WeeklyToggle").Unchecked += async (a, b) => { await Task.Run(() => Maintenance.Schedule(false)); Status("Revisão semanal removida."); };
            Status("Pronto.");
        }

        private async Task RefreshHardware()
        {
            var r = await Task.Run(() =>
            {
                var h = Hardware.Read();
                var c = Hardware.Checkup(h);
                int sc = Score.Compute(c);
                return Tuple.Create(h, c, sc);
            });
            _hw = r.Item1; _checkup = r.Item2;
            AnimateScore(r.Item3);
            F<TextBlock>("HomeCpuName").Text = _hw.Cpu;
            BuildHardware();
            BuildTips();
            await BuildLatencyGuide();
        }

        private async Task UpdateScore()
        {
            var c = _checkup;
            var sc = await Task.Run(() =>
            {
                if (_hw != null) c = Hardware.Checkup(_hw);
                return Score.Compute(c);
            });
            _checkup = c;
            AnimateScore(sc);
            BuildTips();
            if (_hw != null) BuildHardware();
        }

        // ================== Gauge ==================
        private int _shownScore;

        private void AnimateScore(int target)
        {
            int start = _shownScore;
            var t0 = DateTime.Now;
            var anim = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            anim.Tick += (a, b) =>
            {
                double p = Math.Min(1, (DateTime.Now - t0).TotalMilliseconds / 900.0);
                p = 1 - Math.Pow(1 - p, 3);
                int v = (int)Math.Round(start + (target - start) * p);
                DrawGauge(v);
                if (p >= 1) { anim.Stop(); _shownScore = target; }
            };
            anim.Start();
        }

        private void DrawGauge(int score)
        {
            F<System.Windows.Shapes.Path>("GaugeBg").Data = Arc(1.0);
            F<System.Windows.Shapes.Path>("GaugeArc").Data = Arc(Math.Max(0.001, score / 100.0));
            F<TextBlock>("ScoreText").Text = score > 0 ? score.ToString() : "—";
            F<TextBlock>("ScoreLabel").Text = score == 0 ? "calculando" : score >= 85 ? "excelente" : score >= 65 ? "bom" : score >= 45 ? "pode melhorar" : "precisa de boost";
        }

        private static Geometry Arc(double frac)
        {
            const double cx = 100, cy = 95, r = 78, start = 135, sweepTotal = 270;
            double sweep = sweepTotal * frac;
            Func<double, Point> pt = deg => new Point(cx + r * Math.Cos(deg * Math.PI / 180), cy + r * Math.Sin(deg * Math.PI / 180));
            var fig = new PathFigure { StartPoint = pt(start), IsClosed = false, IsFilled = false };
            fig.Segments.Add(new ArcSegment(pt(start + sweep), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true));
            var g = new PathGeometry();
            g.Figures.Add(fig);
            return g;
        }

        // ================== Listas de ajustes ==================
        private static readonly string[] LevelNames = { "Essencial", "Recomendado", "Avançado" };

        private Border Pill(string text, Brush fg, string bg)
        {
            return new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg)),
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = text, FontSize = 11, Foreground = fg, FontWeight = FontWeights.SemiBold }
            };
        }

        private void BuildTweakPage(string page, StackPanel host)
        {
            foreach (var grp in Catalog.Page(page).GroupBy(t => t.Group))
            {
                host.Children.Add(new TextBlock { Text = grp.Key.ToUpperInvariant(), Style = St("Label"), Margin = new Thickness(2, 14, 0, 8) });
                foreach (var t in grp) host.Children.Add(TweakRow(t));
            }
        }

        private FrameworkElement TweakRow(Tweak t)
        {
            var card = new Border { Style = St("CardBorder"), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 8) };
            var stack = new StackPanel();
            var head = new DockPanel();
            var badge = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Text = "…", Foreground = Res("Muted") };
            var badgeBorder = new Border { CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromRgb(0x14, 0x20, 0x3A)), Padding = new Thickness(8, 2, 8, 2), Child = badge, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(badgeBorder, Dock.Right);
            head.Children.Add(badgeBorder);
            var cb = new CheckBox { Style = St("Check"), Content = new TextBlock { Text = t.Title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.NoWrap } };
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(cb);
            string[] lvColors = { "#123A2A", "#132A4A", "#3A2A12" };
            Brush[] lvFg = { Res("Ok"), Res("Accent"), Res("Warn") };
            left.Children.Add(Pill(LevelNames[(int)t.Level], lvFg[(int)t.Level], lvColors[(int)t.Level]));
            if (t.NeedsReboot) left.Children.Add(Pill("reiniciar", Res("Muted"), "#1A2742"));
            head.Children.Add(left);
            stack.Children.Add(head);
            stack.Children.Add(new TextBlock { Text = t.Desc, Style = St("Sub"), Margin = new Thickness(30, 6, 0, 0), FontSize = 12.5 });
            if (!string.IsNullOrEmpty(t.UndoNote))
                stack.Children.Add(new TextBlock { Text = "↺ " + t.UndoNote, Foreground = Res("Muted"), FontSize = 11, Margin = new Thickness(30, 4, 0, 0), Opacity = 0.8 });
            card.Child = stack;
            card.MouseLeftButtonUp += (a, b) => { if (!(b.OriginalSource is FrameworkElement && IsInside((FrameworkElement)b.OriginalSource, cb))) cb.IsChecked = !(cb.IsChecked == true); };
            card.Cursor = System.Windows.Input.Cursors.Hand;
            _checks[t] = cb;
            _badges[t] = badge;
            return card;
        }

        private static bool IsInside(DependencyObject el, DependencyObject parent)
        {
            while (el != null) { if (el == parent) return true; el = VisualTreeHelper.GetParent(el); }
            return false;
        }

        private void SetBadge(Tweak t, bool applied)
        {
            TextBlock b;
            if (!_badges.TryGetValue(t, out b)) return;
            b.Text = applied ? "✓ Aplicado" : "Não aplicado";
            b.Foreground = applied ? Res("Ok") : Res("Muted");
        }

        private async Task RefreshTweakStates()
        {
            var states = await Task.Run(() => Catalog.All.ToDictionary(t => t, TweakEngine.IsApplied));
            foreach (var kv in states) SetBadge(kv.Key, kv.Value);
            foreach (var kv in _boostBadges)
            {
                bool ap;
                if (states.TryGetValue(kv.Key, out ap)) { kv.Value.Text = ap ? "✓" : "•"; kv.Value.Foreground = ap ? Res("Ok") : Res("Muted"); }
            }
        }

        private void SelectLevel(string page, int maxLevel)
        {
            foreach (var t in Catalog.Page(page)) _checks[t].IsChecked = maxLevel >= 0 && (int)t.Level <= maxLevel;
        }

        private async Task RunTweaks(string page, bool undo, Button btn, TextBlock status)
        {
            var sel = Catalog.Page(page).Where(t => _checks[t].IsChecked == true).ToList();
            if (sel.Count == 0) { status.Text = "Nenhum item selecionado."; return; }
            if (!undo && sel.Any(t => t.Level == Level.Avancado))
            {
                var names = string.Join("\n• ", sel.Where(t => t.Level == Level.Avancado).Select(t => t.Title));
                if (MessageBox.Show(this, "Você selecionou itens avançados:\n\n• " + names + "\n\nDeseja continuar?", "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            }
            btn.IsEnabled = false;
            bool reboot = false;
            int i = 0;
            var warnings = new List<string>();
            foreach (var t in sel)
            {
                i++;
                status.Text = (undo ? "Desfazendo " : "Aplicando ") + i + "/" + sel.Count + ": " + t.Title + "…";
                Status(status.Text);
                var tw = t;
                var err = await Task.Run(() => undo ? TweakEngine.Undo(tw) : TweakEngine.Apply(tw));
                if (!string.IsNullOrWhiteSpace(err)) warnings.Add(t.Title + ": " + err.Trim());
                if (t.NeedsReboot) reboot = true;
            }
            await RefreshTweakStates();
            btn.IsEnabled = true;
            status.Text = (undo ? "Desfeito: " : "Aplicado: ") + sel.Count + " itens." + (warnings.Count > 0 ? " Alguns avisos foram salvos no registro de atividades." : "");
            foreach (var w in warnings) Store.Log("AVISO " + w);
            Status(status.Text);
            if (reboot) F<TextBlock>("RebootHint").Visibility = Visibility.Visible;
            await UpdateScore();
        }

        // ================== Boost ==================
        private readonly Dictionary<Tweak, TextBlock> _boostBadges = new Dictionary<Tweak, TextBlock>();

        private void BuildBoostList()
        {
            var host = F<StackPanel>("BoostList");
            foreach (var t in Catalog.All.Where(x => x.InBoost))
            {
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                var mark = new TextBlock { Text = "•", Width = 22, Foreground = Res("Muted"), FontWeight = FontWeights.Bold };
                row.Children.Add(mark);
                row.Children.Add(new TextBlock { Text = t.Title, FontSize = 13 });
                host.Children.Add(row);
                _boostBadges[t] = mark;
            }
        }

        private async Task RunBoost()
        {
            var btn = F<Button>("BoostGo");
            var bar = F<ProgressBar>("BoostProgress");
            var st = F<TextBlock>("BoostStatus");
            btn.IsEnabled = false; F<Button>("HomeBoost").IsEnabled = false;
            bar.Visibility = Visibility.Visible; bar.Value = 0;
            int before = _shownScore;
            var steps = new List<Tuple<string, Func<string>>>();
            if (F<CheckBox>("BoostRestore").IsChecked == true)
                steps.Add(Tuple.Create<string, Func<string>>("Criando ponto de restauração…", () => RestorePoints.Create("VoltCore - antes do Boost")));
            if (F<CheckBox>("BoostTweaks").IsChecked == true)
                foreach (var t in Catalog.All.Where(x => x.InBoost))
                {
                    var tw = t;
                    steps.Add(Tuple.Create<string, Func<string>>("Aplicando: " + t.Title + "…", () => TweakEngine.IsApplied(tw) ? "" : TweakEngine.Apply(tw)));
                }
            if (F<CheckBox>("BoostClean").IsChecked == true)
            {
                steps.Add(Tuple.Create<string, Func<string>>("Limpando arquivos temporários…", () => { foreach (var c in Cleaner.Targets().Where(c => c.Default && !c.StopUpdate)) Cleaner.Clean(c); return ""; }));
                steps.Add(Tuple.Create<string, Func<string>>("Liberando memória…", () => { _freedSession += MemoryCleaner.DeepClean(null); return ""; }));
            }
            bool restoreFailed = false;
            for (int i = 0; i < steps.Count; i++)
            {
                st.Text = steps[i].Item1;
                Status(st.Text);
                var fn = steps[i].Item2;
                var res = await Task.Run(fn);
                if (i == 0 && steps[i].Item1.StartsWith("Criando") && res != null && res.StartsWith("ERRO"))
                {
                    restoreFailed = true;
                    if (MessageBox.Show(this, "Não foi possível criar o ponto de restauração:\n" + res + "\n\nOs ajustes do app continuam reversíveis individualmente. Continuar mesmo assim?", "Ponto de restauração", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                        break;
                }
                bar.Value = (i + 1) * 100.0 / steps.Count;
            }
            await RefreshTweakStates();
            RefreshRestoreSummary();
            if (F<CheckBox>("BoostScore").IsChecked == true) await UpdateScore();
            F<TextBlock>("HomeFreed").Text = Fmt.MB(_freedSession);
            st.Text = "Boost concluído! Nota: " + before + " → " + _shownScore + "." + (restoreFailed ? " (sem ponto de restauração)" : "") + " Reinicie o PC para aplicar tudo.";
            Status(st.Text);
            F<TextBlock>("RebootHint").Visibility = Visibility.Visible;
            btn.IsEnabled = true; F<Button>("HomeBoost").IsEnabled = true;
        }

        // ================== Ações ==================
        private void WireActions()
        {
            F<Button>("HomeBoost").Click += async (a, b) => { Go("PageBoost"); await RunBoost(); };
            F<Button>("BoostGo").Click += async (a, b) => await RunBoost();
            F<Button>("HomeClean").Click += async (a, b) => await CleanMemory(true);
            F<Button>("HomeRestoreBtn").Click += async (a, b) => await CreateRestore();

            F<Button>("DbLvl0").Click += (a, b) => SelectLevel("debloat", 0);
            F<Button>("DbLvl1").Click += (a, b) => SelectLevel("debloat", 1);
            F<Button>("DbLvl2").Click += (a, b) => SelectLevel("debloat", 2);
            F<Button>("DbNone").Click += (a, b) => SelectLevel("debloat", -1);
            F<Button>("DbApply").Click += async (a, b) => await RunTweaks("debloat", false, (Button)a, F<TextBlock>("DbStatus"));
            F<Button>("DbUndo").Click += async (a, b) => await RunTweaks("debloat", true, (Button)a, F<TextBlock>("DbStatus"));
            F<Button>("PfLvl0").Click += (a, b) => SelectLevel("perf", 0);
            F<Button>("PfLvl1").Click += (a, b) => SelectLevel("perf", 1);
            F<Button>("PfLvl2").Click += (a, b) => SelectLevel("perf", 2);
            F<Button>("PfNone").Click += (a, b) => SelectLevel("perf", -1);
            F<Button>("PfApply").Click += async (a, b) => await RunTweaks("perf", false, (Button)a, F<TextBlock>("PfStatus"));
            F<Button>("PfUndo").Click += async (a, b) => await RunTweaks("perf", true, (Button)a, F<TextBlock>("PfStatus"));

            F<Button>("MemQuick").Click += async (a, b) => await CleanMemory(false);
            F<Button>("MemDeep").Click += async (a, b) => await CleanMemory(true);
            F<CheckBox>("MemAuto").Checked += (a, b) => Settings.Set("mem_auto", true);
            F<CheckBox>("MemAuto").Unchecked += (a, b) => Settings.Set("mem_auto", false);
            F<TextBox>("MemStandbyLimit").LostFocus += (a, b) => { int v; if (int.TryParse(((TextBox)a).Text, out v) && v > 0) Settings.Set("mem_standby", v); };
            F<TextBox>("MemFreeLimit").LostFocus += (a, b) => { int v; if (int.TryParse(((TextBox)a).Text, out v) && v > 0) Settings.Set("mem_free", v); };

            F<Button>("StartRefresh").Click += (a, b) => LoadStartup();
            F<Button>("CleanScan").Click += async (a, b) => await ScanCleaner();
            F<Button>("CleanGo").Click += async (a, b) => await RunCleaner();
            F<Button>("RepSfc").Click += (a, b) => Shell.OpenConsole("sfc /scannow");
            F<Button>("RepDism").Click += (a, b) => Shell.OpenConsole("DISM /Online /Cleanup-Image /RestoreHealth");
            F<Button>("RepFull").Click += (a, b) => Shell.OpenConsole("DISM /Online /Cleanup-Image /RestoreHealth && sfc /scannow");
            F<Button>("RepComp").Click += (a, b) => Shell.OpenConsole("DISM /Online /Cleanup-Image /StartComponentCleanup");
            F<Button>("RepNet").Click += (a, b) => Shell.OpenConsole("ipconfig /flushdns && netsh winsock reset && netsh int ip reset && echo. && echo Reinicie o PC para concluir.");

            F<Button>("GameDetect").Click += async (a, b) => await DetectGames();
            F<Button>("GameAdd").Click += (a, b) => AddGameManual();
            F<Button>("OpenGpuPanel").Click += (a, b) => { try { if (_gpuPanel != null) _gpuPanel(); } catch (Exception ex) { Status("Não foi possível abrir o painel: " + ex.Message); } };
            var def = F<CheckBox>("DefExcl");
            def.IsChecked = Settings.GetBool("defender_excl", false);
            def.Checked += async (a, b) => { Settings.Set("defender_excl", true); await SyncDefender(); };
            def.Unchecked += async (a, b) => { Settings.Set("defender_excl", false); await SyncDefender(); };

            F<Button>("HwRefresh").Click += async (a, b) => { Status("Refazendo check-up…"); await RefreshHardware(); Status("Check-up atualizado."); };

            F<Button>("RpCreate").Click += async (a, b) => await CreateRestore();
            F<Button>("RpOpen").Click += (a, b) => Shell.Open(Shell.Sys("rstrui.exe"));
            F<Button>("UndoAll").Click += async (a, b) => await UndoAll();
            F<Button>("OpenLog").Click += (a, b) => { var p = Store.PathOf("log.txt"); if (!File.Exists(p)) File.WriteAllText(p, ""); Shell.Open(p); };
        }

        // ================== Configurações e sessão de jogo ==================
        private void WireSettings()
        {
            var tray = F<CheckBox>("SetTray");
            tray.IsChecked = Settings.GetBool("close_to_tray", true);
            tray.Checked += (a, b) => Settings.Set("close_to_tray", true);
            tray.Unchecked += (a, b) => Settings.Set("close_to_tray", false);

            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            F<TextBlock>("AboutText").Text = "VoltCore Optimizer " + ver.Major + "." + ver.Minor + "." + ver.Build + " · Dados e backups em " + Store.Dir;
            F<Button>("OpenData").Click += (a, b) => Shell.Open(Store.Dir);
            F<Button>("OpenRepo").Click += (a, b) => Shell.Open("https://github.com/Pedroedmachado/otimizacao");

            var timer = F<CheckBox>("SessTimer");
            timer.IsChecked = Settings.GetBool("session_timer", false);
            timer.Checked += async (a, b) =>
            {
                Settings.Set("session_timer", true);
                var tg = Catalog.ById("timer_global");
                if (Environment.OSVersion.Version.Build >= 22000 && tg != null && !TweakEngine.IsApplied(tg))
                {
                    await Task.Run(() => TweakEngine.Apply(tg));
                    SetBadge(tg, true);
                    F<TextBlock>("RebootHint").Visibility = Visibility.Visible;
                    Status("Timer global permitido no Windows 11 — reinicie o PC para valer.");
                }
            };
            timer.Unchecked += (a, b) => { Settings.Set("session_timer", false); if (!_watcher.InSession) TimerResolution.Disable(); };

            var close = F<CheckBox>("SessClose");
            close.IsChecked = Settings.GetBool("session_close", false);
            close.Checked += (a, b) => Settings.Set("session_close", true);
            close.Unchecked += (a, b) => Settings.Set("session_close", false);
            var list = F<TextBox>("SessCloseList");
            list.Text = Settings.Get("close_list", BackgroundCloser.DefaultList);
            list.LostFocus += (a, b) => Settings.Set("close_list", list.Text.Replace("\r", "").Replace("\n", " "));
        }

        // ================== Estabilidade do cabo ==================
        private void WireLink()
        {
            LinkWatcher.Changed += ev => Dispatcher.BeginInvoke(new Action(() =>
            {
                Status("🔌 Cabo de rede: " + ev.From + " → " + ev.To + " às " + ev.Time.ToString("HH:mm:ss"));
                RefreshLink();
            }));
            F<Button>("LinkClear").Click += (a, b) => { LinkWatcher.Clear(); RefreshLink(); };
            Task.Run(() => LinkWatcher.Tick());
        }

        private void RefreshLink()
        {
            var now = LinkWatcher.Current();
            F<TextBlock>("LinkNow").Text = now.Count == 0 ? "Nenhuma placa de rede cabeada encontrada." : string.Join("\n", now.Select(n => "Agora: " + n.Item2 + "  ·  " + n.Item1));
            var hist = LinkWatcher.History();
            int day = hist.Count(e => e.Time > DateTime.Now.AddHours(-24));
            var since = Settings.Get("link_since", "");
            F<TextBlock>("LinkSummary").Text = hist.Count == 0
                ? "Nenhuma troca registrada" + (since.Length > 0 ? " desde " + since : "") + ". Se continuar assim depois de trocar o cabo, o problema foi resolvido."
                : day + " trocas nas últimas 24 h · " + hist.Count + " no total.";
            F<TextBlock>("LinkSummary").Foreground = day > 0 ? Res("Warn") : Res("Muted");
            var host = F<StackPanel>("LinkList");
            host.Children.Clear();
            foreach (var e in Enumerable.Reverse(hist).Take(12))
            {
                bool down = e.To == "desconectado" || (e.To.EndsWith("Mbps") && !e.From.EndsWith("Mbps"));
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                row.Children.Add(new TextBlock { Text = e.Time.ToString("dd/MM HH:mm:ss"), Width = 120, Foreground = Res("Muted"), FontSize = 12.5 });
                row.Children.Add(new TextBlock { Text = (down ? "▼ " : "▲ ") + e.From + " → " + e.To, Foreground = down ? Res("Warn") : Res("Ok"), FontSize = 12.5, FontWeight = FontWeights.SemiBold });
                host.Children.Add(row);
            }
        }

        // ================== Guia de latência ==================
        private async Task BuildLatencyGuide()
        {
            var displays = await Task.Run(() => Displays.List());
            int hz = displays.Count > 0 ? displays.Max(d => d.MaxHz) : 60;
            int cap = Displays.FpsCap(hz);
            string gpu = _hw != null ? _hw.Gpu : "";
            bool nv = gpu.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0;
            bool amd = gpu.IndexOf("Radeon", StringComparison.OrdinalIgnoreCase) >= 0 || gpu.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0;
            var steps = new List<Tuple<string, string>>();
            if (nv)
            {
                steps.Add(Tuple.Create("NVIDIA Reflex: \"Ligado + Boost\"", "Nas configurações de vídeo do jogo (CS2, Valorant, Fortnite, Apex, CoD, Overwatch, Marvel Rivals…). É o ajuste que mais reduz input lag quando a GPU está no limite."));
                steps.Add(Tuple.Create("Modo de baixa latência: Ultra", "Painel de Controle NVIDIA > Gerenciar configurações 3D. Vale para jogos SEM Reflex (quando o jogo tem Reflex, ele manda)."));
                steps.Add(Tuple.Create("G-SYNC ligado + V-Sync ligado no painel", "Painel NVIDIA > Configurar G-SYNC (tela cheia e janela) e V-Sync 'Ativado' no painel, DESLIGADO dentro do jogo. Sem tearing e sem o atraso do V-Sync tradicional."));
                steps.Add(Tuple.Create("Limite de FPS em " + cap + " (monitor de " + hz + " Hz)", "Painel NVIDIA > Taxa de quadros máxima = " + cap + ". Mantém o G-SYNC sempre ativo. Com Reflex + G-SYNC + V-Sync o limite é aplicado automaticamente."));
                steps.Add(Tuple.Create("Gerenciamento de energia: desempenho máximo", "Painel NVIDIA > configuração do programa (por jogo) > 'Preferir desempenho máximo'. Evita quedas de clock em jogos leves."));
            }
            else if (amd)
            {
                steps.Add(Tuple.Create("AMD Anti-Lag 2 (no jogo) ou Radeon Anti-Lag", "Anti-Lag 2 aparece nas opções do jogo (CS2 e outros). Nos demais, ative Radeon Anti-Lag no AMD Software > Jogos."));
                steps.Add(Tuple.Create("FreeSync ligado + V-Sync 'Sempre ativado'", "AMD Software > Tela: FreeSync. V-Sync desligado dentro do jogo."));
                steps.Add(Tuple.Create("Limite de FPS em " + cap + " (monitor de " + hz + " Hz)", "Radeon Chill ou FRTC com mínimo e máximo em " + cap + ", ou o limitador do próprio jogo."));
                steps.Add(Tuple.Create("Radeon Boost / Super Resolution desligados", "Eles mudam a resolução dinamicamente e podem causar inconsistência na mira."));
            }
            else
            {
                steps.Add(Tuple.Create("Tecnologia de baixa latência do jogo", "Ative Reflex / Anti-Lag / XeLL nas opções de vídeo do jogo, se existir."));
                steps.Add(Tuple.Create("VRR ligado + limite de FPS em " + cap, "Com monitor de taxa variável, limite o FPS um pouco abaixo de " + hz + " Hz."));
            }
            steps.Add(Tuple.Create("Tela cheia exclusiva ou sem bordas otimizado", "No Windows 11 o modo sem bordas já é tão rápido quanto tela cheia com a opção 'Otimizações para jogos em janela' (aba Desempenho)."));
            steps.Add(Tuple.Create("Sem monitor VRR / jogo competitivo puro", "V-Sync desligado e FPS o mais alto possível (bem acima de " + hz + "). Aceita tearing em troca do menor atraso."));

            var host = F<StackPanel>("LatGuide");
            host.Children.Clear();
            int n = 0;
            foreach (var s in steps)
            {
                n++;
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
                row.Children.Add(new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Res("AccentGrad"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 12, 0), Child = new TextBlock { Text = n.ToString(), FontSize = 11, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
                var t = new StackPanel();
                t.Children.Add(new TextBlock { Text = s.Item1, FontSize = 14, FontWeight = FontWeights.SemiBold });
                t.Children.Add(new TextBlock { Text = s.Item2, Style = St("Sub"), FontSize = 12 });
                row.Children.Add(t);
                host.Children.Add(row);
            }

            var btn = F<Button>("OpenGpuPanel");
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (nv)
            {
                btn.Content = "Abrir Painel de Controle NVIDIA";
                _gpuPanel = () => Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "shell:AppsFolder\\NVIDIACorp.NVIDIAControlPanel_56jrs9ps46q5g!NVIDIACorp.NVIDIAControlPanel");
            }
            else if (amd)
            {
                btn.Content = "Abrir AMD Software";
                _gpuPanel = () => Shell.Open(Path.Combine(pf, @"AMD\CNext\CNext\RadeonSoftware.exe"));
            }
            else btn.Visibility = Visibility.Collapsed;
        }

        private Action _gpuPanel;

        // ================== Defender ==================
        private async Task SyncDefender()
        {
            bool on = Settings.GetBool("defender_excl", false);
            var desired = on ? _games.Select(g => DefenderExclusions.GameRoot(g.Exe)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();
            var current = Settings.Get("defender_paths", "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            var toAdd = desired.Where(d => !current.Contains(d, StringComparer.OrdinalIgnoreCase)).ToList();
            var toRemove = current.Where(c => !desired.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
            string err = await Task.Run(() => { DefenderExclusions.Remove(toRemove); return DefenderExclusions.Add(toAdd); });
            Settings.Set("defender_paths", string.Join("|", err.Length == 0 ? desired : current.Except(toRemove, StringComparer.OrdinalIgnoreCase)));
            var info = F<TextBlock>("DefExclInfo");
            if (err.Length > 0) info.Text = "Não foi possível alterar o Defender (" + err + "). Se você usa outro antivírus, configure as exclusões nele.";
            else if (on) info.Text = desired.Count + " pastas de jogos excluídas da verificação em tempo real.";
            else info.Text = "Menos travadas ao carregar mapas e texturas. Use só com jogos de lojas confiáveis (Steam, Epic). Vale para os jogos desta lista.";
        }

        // ================== Monitor ao vivo ==================
        private LiveMonitor _live;
        private int _liveBusy;

        private void UpdateLive()
        {
            if (System.Threading.Interlocked.Exchange(ref _liveBusy, 1) == 1) return;
            Task.Run(() =>
            {
                if (_live == null) _live = new LiveMonitor();
                return _live.Sample();
            }).ContinueWith(t =>
            {
                _liveBusy = 0;
                if (t.IsFaulted) return;
                var s = t.Result;
                F<TextBlock>("LvCpu").Text = s.CpuPct.ToString("0") + "%";
                F<ProgressBar>("LvCpuBar").Value = s.CpuPct;
                F<TextBlock>("LvCpuSub").Text = s.CpuMhz > 0 ? "Clock efetivo " + (s.CpuMhz / 1000).ToString("0.00") + " GHz" : "";
                if (s.GpuPct >= 0) { F<TextBlock>("LvGpu").Text = s.GpuPct.ToString("0") + "%"; F<ProgressBar>("LvGpuBar").Value = s.GpuPct; }
                var parts = new List<string>();
                if (s.GpuTemp >= 0) parts.Add(s.GpuTemp.ToString("0") + " °C");
                if (s.GpuClock >= 0) parts.Add(s.GpuClock.ToString("0") + " MHz");
                if (s.GpuPower >= 0) parts.Add(s.GpuPower.ToString("0") + " W");
                if (s.GpuFan >= 0) parts.Add("fan " + s.GpuFan.ToString("0") + "%");
                F<TextBlock>("LvGpuSub").Text = parts.Count > 0 ? string.Join(" · ", parts) : (_live != null && !_live.HasNvidia ? "Temperatura disponível só em placas NVIDIA" : "");
                if (s.VramUsedMB >= 0)
                {
                    double total = _hw != null && _hw.GpuVramGB > 0 ? _hw.GpuVramGB * 1024 : 0;
                    F<TextBlock>("LvVram").Text = Fmt.MB(s.VramUsedMB);
                    F<ProgressBar>("LvVramBar").Value = total > 0 ? Math.Min(100, s.VramUsedMB * 100 / total) : 0;
                    F<TextBlock>("LvVramSub").Text = total > 0 ? "de " + Fmt.MB(total) : "";
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // ================== Memória ==================
        private async Task CleanMemory(bool deep)
        {
            Status(deep ? "Limpeza completa de memória…" : "Limpando cache de memória…");
            var skip = _watcher.RunningGamePids;
            double freed = await Task.Run(() => deep ? MemoryCleaner.DeepClean(skip) : MemoryCleaner.QuickClean());
            _freedSession += freed;
            F<TextBlock>("HomeFreed").Text = Fmt.MB(_freedSession);
            F<TextBlock>("MemResult").Text = "Liberado: " + Fmt.MB(freed);
            Status("Memória liberada: " + Fmt.MB(freed) + ".");
            RefreshTopProcesses();
        }

        private void RefreshTopProcesses()
        {
            var host = F<StackPanel>("MemTop");
            Task.Run(() => MemoryCleaner.TopProcesses(10)).ContinueWith(t =>
            {
                host.Children.Clear();
                double max = t.Result.Count > 0 ? t.Result[0].Item2 : 1;
                foreach (var p in t.Result)
                {
                    var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
                    g.ColumnDefinitions.Add(new ColumnDefinition());
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
                    g.Children.Add(new TextBlock { Text = p.Item1, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
                    var bar = new ProgressBar { Maximum = max, Value = p.Item2, VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(bar, 1); g.Children.Add(bar);
                    var v = new TextBlock { Text = Fmt.MB(p.Item2), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Right, Foreground = Res("Muted") };
                    Grid.SetColumn(v, 2); g.Children.Add(v);
                    host.Children.Add(g);
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void OnTick()
        {
            _tick++;
            double cpu = _cpu.Sample();
            var m = MemoryCleaner.Snapshot();
            F<TextBlock>("SideCpu").Text = "CPU  " + cpu.ToString("0") + "%";
            F<ProgressBar>("SideCpuBar").Value = cpu;
            F<TextBlock>("SideRam").Text = "RAM  " + Fmt.MB(m.UsedMB) + " / " + Fmt.MB(m.TotalMB);
            F<ProgressBar>("SideRamBar").Value = m.LoadPct;
            F<TextBlock>("HomeCpu").Text = cpu.ToString("0") + "%";
            F<ProgressBar>("HomeCpuBar").Value = cpu;
            F<TextBlock>("HomeRam").Text = m.LoadPct + "%";
            F<ProgressBar>("HomeRamBar").Value = m.LoadPct;
            F<TextBlock>("HomeRamSub").Text = Fmt.MB(m.UsedMB) + " de " + Fmt.MB(m.TotalMB) + (m.ListsOk ? " · cache " + Fmt.MB(m.StandbyMB) : "");
            F<TextBlock>("MemUsed").Text = Fmt.MB(m.UsedMB);
            F<TextBlock>("MemFree").Text = m.ListsOk ? Fmt.MB(m.FreeMB) : Fmt.MB(m.AvailMB);
            F<TextBlock>("MemStandby").Text = m.ListsOk ? Fmt.MB(m.StandbyMB) : "—";
            F<TextBlock>("MemModified").Text = m.ListsOk ? Fmt.MB(m.ModifiedMB) : "—";
            F<ProgressBar>("MemBar").Value = m.LoadPct;

            if (_tick % 3 == 0 && _games.Count > 0) Task.Run(() => _watcher.Tick());
            if (_tick % 2 == 0) Task.Run(() => LinkWatcher.Tick());
            if (_tick % 2 == 0 && IsVisible && F<FrameworkElement>("PageHw").Visibility == Visibility.Visible) UpdateLive();
            if (_tick % 5 == 0 && IsVisible && F<FrameworkElement>("PageHw").Visibility == Visibility.Visible) RefreshLink();
            if (_tick % 2 == 0 && IsVisible && F<FrameworkElement>("PageGames").Visibility == Visibility.Visible)
            {
                double ms = TimerResolution.CurrentMs();
                F<TextBlock>("SessTimerInfo").Text = (ms > 0 ? "Timer atual do Windows: " + ms.ToString("0.0###") + " ms" : "") + (_watcher.InSession ? " · sessão de jogo ativa" : "");
            }

            // Limpeza automática (estilo ISLC)
            if (Settings.GetBool("mem_auto", false) && m.ListsOk && (DateTime.Now - _lastAutoClean).TotalSeconds > 30)
            {
                int standbyLimit = Settings.GetInt("mem_standby", 1024), freeLimit = Settings.GetInt("mem_free", 2048);
                if (m.StandbyMB > standbyLimit && m.FreeMB < freeLimit)
                {
                    _lastAutoClean = DateTime.Now;
                    Task.Run(() => { MemoryCleaner.PurgeStandby(); return MemoryCleaner.Snapshot(); }).ContinueWith(t =>
                    {
                        double freed = Math.Max(0, t.Result.FreeMB - m.FreeMB);
                        _freedSession += freed;
                        _autoCleans++;
                        F<TextBlock>("HomeFreed").Text = Fmt.MB(_freedSession);
                        F<TextBlock>("MemAutoInfo").Text = "Limpezas automáticas nesta sessão: " + _autoCleans + " · última às " + DateTime.Now.ToString("HH:mm:ss") + " (+" + Fmt.MB(freed) + ")";
                    }, TaskScheduler.FromCurrentSynchronizationContext());
                }
            }
        }

        // ================== Inicialização ==================
        private void LoadStartup()
        {
            var host = F<StackPanel>("StartList");
            host.Children.Clear();
            List<StartupItem> items;
            try { items = StartupManager.List(); } catch (Exception ex) { host.Children.Add(new TextBlock { Text = ex.Message }); return; }
            if (items.Count == 0) host.Children.Add(new TextBlock { Text = "Nenhum programa na inicialização. 👌", Style = St("Sub") });
            foreach (var it in items)
            {
                var item = it;
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
                var tg = new CheckBox { Style = St("Toggle"), IsChecked = it.Enabled, Margin = new Thickness(0, 0, 14, 0) };
                tg.Checked += (a, b) => { try { StartupManager.SetEnabled(item, true); Status(item.Name + " vai abrir com o Windows."); } catch (Exception ex) { Status(ex.Message); } };
                tg.Unchecked += (a, b) => { try { StartupManager.SetEnabled(item, false); Status(item.Name + " não abre mais com o Windows."); } catch (Exception ex) { Status(ex.Message); } };
                row.Children.Add(tg);
                var txt = new StackPanel();
                txt.Children.Add(new TextBlock { Text = it.Name, FontSize = 14, FontWeight = FontWeights.SemiBold });
                txt.Children.Add(new TextBlock { Text = it.Source + " · " + it.Command, Style = St("Sub"), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
                row.Children.Add(txt);
                host.Children.Add(row);
            }
        }

        // ================== Limpeza ==================
        private void BuildCleaner()
        {
            _clean = Cleaner.Targets();
            var host = F<StackPanel>("CleanList");
            foreach (var t in _clean)
            {
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
                var size = new TextBlock { Text = "—", Width = 90, TextAlignment = TextAlignment.Right, Foreground = Res("Accent2"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(size, Dock.Right);
                row.Children.Add(size);
                var cb = new CheckBox { Style = St("Check"), IsChecked = t.Default };
                var txt = new StackPanel();
                txt.Children.Add(new TextBlock { Text = t.Name, FontSize = 14 });
                txt.Children.Add(new TextBlock { Text = t.Desc, Style = St("Sub"), FontSize = 11.5 });
                cb.Content = txt;
                row.Children.Add(cb);
                host.Children.Add(row);
                _cleanUi[t] = Tuple.Create(cb, size);
            }
        }

        private async Task ScanCleaner()
        {
            F<TextBlock>("CleanStatus").Text = "Analisando…";
            long total = 0;
            foreach (var t in _clean)
            {
                var tt = t;
                await Task.Run(() => Cleaner.Measure(tt));
                _cleanUi[t].Item2.Text = Fmt.Bytes(t.Bytes);
                if (_cleanUi[t].Item1.IsChecked == true) total += t.Bytes;
            }
            F<TextBlock>("CleanStatus").Text = "Selecionado: " + Fmt.Bytes(total);
        }

        private async Task RunCleaner()
        {
            var sel = _clean.Where(t => _cleanUi[t].Item1.IsChecked == true).ToList();
            if (sel.Any(t => t.RecycleBin) && MessageBox.Show(this, "Esvaziar a Lixeira apaga os arquivos dela para sempre. Continuar?", "Lixeira", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var btn = F<Button>("CleanGo");
            btn.IsEnabled = false;
            long freed = 0;
            foreach (var t in sel)
            {
                F<TextBlock>("CleanStatus").Text = "Limpando " + t.Name + "…";
                var tt = t;
                freed += await Task.Run(() => Cleaner.Clean(tt));
                await Task.Run(() => Cleaner.Measure(tt));
                _cleanUi[t].Item2.Text = Fmt.Bytes(t.Bytes);
            }
            F<TextBlock>("CleanStatus").Text = "Liberado: " + Fmt.Bytes(freed) + " (arquivos em uso são ignorados).";
            Status("Limpeza concluída: " + Fmt.Bytes(freed) + " liberados.");
            btn.IsEnabled = true;
        }

        // ================== Jogos ==================
        private void BuildGames()
        {
            var host = F<StackPanel>("GameList");
            host.Children.Clear();
            if (_games.Count == 0)
            {
                host.Children.Add(new Border { Style = St("CardBorder"), Child = new TextBlock { Text = "Nenhum jogo ainda. Clique em \"Detectar jogos\" ou adicione o .exe manualmente.", Style = St("Sub") } });
                return;
            }
            foreach (var g in _games.OrderBy(x => x.Name))
            {
                var game = g;
                var card = new Border { Style = St("CardBorder"), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 10) };
                var st = new StackPanel();
                var head = new DockPanel();
                var rm = new Button { Style = St("Ghost"), Content = "Remover", Margin = new Thickness(0), Padding = new Thickness(10, 4, 10, 4), FontSize = 12 };
                DockPanel.SetDock(rm, Dock.Right);
                rm.Click += (a, b) =>
                {
                    try { Games.ClearRegistry(game); } catch { }
                    _games.Remove(game); Games.Save(_games); BuildGames();
                    var _ = SyncDefender();
                };
                head.Children.Add(rm);
                var chg = new Button { Style = St("Ghost"), Content = "Trocar .exe", Padding = new Thickness(10, 4, 10, 4), FontSize = 12 };
                DockPanel.SetDock(chg, Dock.Right);
                chg.Click += (a, b) =>
                {
                    var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Executável (*.exe)|*.exe", InitialDirectory = Path.GetDirectoryName(game.Exe) };
                    if (dlg.ShowDialog(this) == true) { try { Games.ClearRegistry(game); } catch { } game.Exe = dlg.FileName; SaveGame(game); BuildGames(); }
                };
                head.Children.Add(chg);
                var title = new StackPanel();
                var tl = new StackPanel { Orientation = Orientation.Horizontal };
                tl.Children.Add(new TextBlock { Text = g.Name, FontSize = 16, FontWeight = FontWeights.SemiBold });
                tl.Children.Add(Pill(g.Source, Res("Accent2"), "#0F2A3A"));
                if (!g.Exists) tl.Children.Add(Pill("exe não encontrado", Res("Bad"), "#3A1414"));
                title.Children.Add(tl);
                title.Children.Add(new TextBlock { Text = g.Exe, Style = St("Sub"), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
                head.Children.Add(title);
                st.Children.Add(head);

                var opts = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
                opts.Children.Add(GameToggle("Prioridade alta", g.HighPriority, v => { game.HighPriority = v; SaveGame(game); }));
                opts.Children.Add(GameToggle("GPU de alto desempenho", g.GpuHigh, v => { game.GpuHigh = v; SaveGame(game); }));
                opts.Children.Add(GameToggle("Sem otimizações de tela cheia", g.NoFso, v => { game.NoFso = v; SaveGame(game); }));
                opts.Children.Add(GameToggle("Núcleo 0 livre", g.FreeCore0, v => { game.FreeCore0 = v; SaveGame(game); }));
                opts.Children.Add(GameToggle("Limpar memória ao abrir", g.PurgeOnStart, v => { game.PurgeOnStart = v; SaveGame(game); }));
                st.Children.Add(opts);
                card.Child = st;
                host.Children.Add(card);
            }
        }

        private FrameworkElement GameToggle(string text, bool value, Action<bool> set)
        {
            var cb = new CheckBox { Style = St("Toggle"), IsChecked = value, Content = new TextBlock { Text = text, FontSize = 13 }, Margin = new Thickness(0, 4, 22, 4) };
            cb.Checked += (a, b) => set(true);
            cb.Unchecked += (a, b) => set(false);
            return cb;
        }

        private void SaveGame(GameEntry g)
        {
            try { Games.ApplyRegistry(g); } catch (Exception ex) { Status("Erro ao ajustar " + g.Name + ": " + ex.Message); }
            Games.Save(_games);
            _watcher.Games = _games;
            Status(g.Name + ": ajustes salvos. Núcleo 0 livre e limpeza ao abrir funcionam com o app aberto (ou na bandeja).");
        }

        private async Task DetectGames()
        {
            F<TextBlock>("GameStatus").Text = "Procurando jogos…";
            var found = await Task.Run(() => Games.DetectAll());
            int added = 0;
            foreach (var f in found)
            {
                if (_games.Any(g => string.Equals(g.Exe, f.Exe, StringComparison.OrdinalIgnoreCase))) continue;
                f.HighPriority = true; f.GpuHigh = true; f.PurgeOnStart = true;
                _games.Add(f);
                try { Games.ApplyRegistry(f); } catch { }
                added++;
            }
            Games.Save(_games);
            _watcher.Games = _games;
            BuildGames();
            F<TextBlock>("GameStatus").Text = found.Count + " jogos encontrados, " + added + " novos adicionados com os ajustes recomendados.";
            if (added > 0) await SyncDefender();
        }

        private void AddGameManual()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Executável do jogo (*.exe)|*.exe", Title = "Escolha o .exe do jogo" };
            if (dlg.ShowDialog(this) != true) return;
            if (_games.Any(g => string.Equals(g.Exe, dlg.FileName, StringComparison.OrdinalIgnoreCase))) return;
            string name = FileVersionInfo.GetVersionInfo(dlg.FileName).ProductName;
            if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(dlg.FileName);
            var g2 = new GameEntry { Name = name.Trim(), Exe = dlg.FileName, Source = "Manual", HighPriority = true, GpuHigh = true, PurgeOnStart = true };
            _games.Add(g2);
            SaveGame(g2);
            BuildGames();
            var _ = SyncDefender();
        }

        // ================== Hardware ==================
        private void BuildHardware()
        {
            var grid = F<Grid>("HwGrid");
            grid.Children.Clear();
            grid.RowDefinitions.Clear();
            var rows = new List<Tuple<string, string>>
            {
                Tuple.Create("Processador", _hw.Cpu + (_hw.Cores > 0 ? "  ·  " + _hw.Cores + " núcleos / " + _hw.Threads + " threads" : "")),
                Tuple.Create("Placa de vídeo", _hw.Gpu + (_hw.GpuVramGB > 0 ? "  ·  " + _hw.GpuVramGB.ToString("0") + " GB" : "")),
                Tuple.Create("Memória", _hw.RamText),
                Tuple.Create("Placa-mãe", _hw.Board),
                Tuple.Create("Sistema", _hw.Os),
                Tuple.Create("Discos", _hw.Disks.Count > 0 ? string.Join("\n", _hw.Disks) : "—"),
            };
            for (int i = 0; i < rows.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var k = new TextBlock { Text = rows[i].Item1.ToUpperInvariant(), Style = St("Label"), Margin = new Thickness(0, 8, 0, 8) };
                var v = new TextBlock { Text = rows[i].Item2, FontSize = 14, Margin = new Thickness(0, 6, 0, 6) };
                Grid.SetRow(k, i); Grid.SetRow(v, i); Grid.SetColumn(v, 1);
                grid.Children.Add(k); grid.Children.Add(v);
            }
            var host = F<StackPanel>("HwChecks");
            host.Children.Clear();
            foreach (var c in _checkup) host.Children.Add(CheckRow(c));
        }

        private FrameworkElement CheckRow(CheckItem c)
        {
            var row = new DockPanel { Margin = new Thickness(0, 5, 0, 5) };
            if (!string.IsNullOrEmpty(c.FixLabel))
            {
                var fix = new Button { Style = St("Ghost"), Content = c.FixLabel, Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(fix, Dock.Right);
                fix.Click += async (a, b) =>
                {
                    if (c.GoTo != null) { Go(c.GoTo); return; }
                    fix.IsEnabled = false;
                    var msg = await Task.Run(c.Fix);
                    Status(msg);
                    await UpdateScore();
                };
                row.Children.Add(fix);
            }
            var icon = new TextBlock { Text = c.Ok ? "✓" : "!", Width = 26, FontSize = 16, FontWeight = FontWeights.Bold, Foreground = c.Ok ? Res("Ok") : Res("Warn") };
            row.Children.Add(icon);
            var t = new StackPanel();
            t.Children.Add(new TextBlock { Text = c.Title, FontSize = 14, FontWeight = FontWeights.SemiBold });
            t.Children.Add(new TextBlock { Text = c.Detail, Style = St("Sub"), FontSize = 12 });
            row.Children.Add(t);
            return row;
        }

        private void BuildTips()
        {
            var host = F<StackPanel>("HomeTips");
            host.Children.Clear();
            var pending = Catalog.All.Where(t => t.InBoost).Count(t => _badges.ContainsKey(t) && _badges[t].Text != "✓ Aplicado");
            if (pending > 0)
            {
                var row = new DockPanel { Margin = new Thickness(0, 5, 0, 5) };
                var b = new Button { Style = St("Ghost"), Content = "Fazer Boost", Margin = new Thickness(0), Padding = new Thickness(10, 4, 10, 4) };
                b.Click += (a, e) => Go("PageBoost");
                DockPanel.SetDock(b, Dock.Right);
                row.Children.Add(b);
                row.Children.Add(CheckRow(new CheckItem { Ok = false, Title = pending + " ajustes de desempenho não aplicados", Detail = "O Boost aplica todos de uma vez." }));
                host.Children.Add(row);
            }
            if (_checkup != null)
                foreach (var c in _checkup.Where(x => !x.Ok)) host.Children.Add(CheckRow(c));
            if (host.Children.Count == 0)
                host.Children.Add(CheckRow(new CheckItem { Ok = true, Title = "Tudo certo!", Detail = "Seu PC está otimizado." }));
        }

        // ================== Restauração ==================
        private void RefreshRestoreSummary()
        {
            Task.Run(() => RestorePoints.List()).ContinueWith(t =>
            {
                var list = t.Result;
                F<TextBlock>("HomeRestore").Text = list.Count > 0 ? "Último: " + list[0].Split('|')[0] : "Nenhum ponto encontrado";
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void LoadRestorePoints()
        {
            var host = F<StackPanel>("RpList");
            host.Children.Clear();
            host.Children.Add(new TextBlock { Text = "Carregando…", Style = St("Sub") });
            Task.Run(() => RestorePoints.List()).ContinueWith(t =>
            {
                host.Children.Clear();
                if (t.Result.Count == 0) host.Children.Add(new TextBlock { Text = "Nenhum ponto de restauração. A Proteção do Sistema pode estar desligada — criar um ponto liga ela no disco C:.", Style = St("Sub") });
                foreach (var l in t.Result.Take(12))
                {
                    var p = l.Split(new[] { '|' }, 2);
                    var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                    row.Children.Add(new TextBlock { Text = p[0], Width = 140, Foreground = Res("Accent2") });
                    row.Children.Add(new TextBlock { Text = p[1] });
                    host.Children.Add(row);
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private async Task CreateRestore()
        {
            Status("Criando ponto de restauração (pode levar um minuto)…");
            F<TextBlock>("RpStatus").Text = "Criando…";
            var r = await Task.Run(() => RestorePoints.Create("VoltCore - manual"));
            bool ok = r.StartsWith("OK");
            F<TextBlock>("RpStatus").Text = ok ? "Ponto criado." : r;
            Status(ok ? "Ponto de restauração criado." : "Falha: " + r);
            RefreshRestoreSummary();
            LoadRestorePoints();
        }

        private async Task UndoAll()
        {
            var applied = Catalog.All.Where(t => t.Ops.Count > 0 || t.UndoPs.Count > 0 || t.CustomUndo != null).ToList();
            var ids = TweakEngine.Applied();
            var targets = applied.Where(t => ids.Contains(t.Id) && !t.Id.StartsWith("app_")).ToList();
            if (targets.Count == 0) { F<TextBlock>("UndoStatus").Text = "Nada para desfazer."; return; }
            if (MessageBox.Show(this, "Desfazer " + targets.Count + " ajustes aplicados pelo app?", "Desfazer tudo", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            int i = 0;
            foreach (var t in targets)
            {
                i++;
                F<TextBlock>("UndoStatus").Text = "Desfazendo " + i + "/" + targets.Count + "…";
                var tw = t;
                await Task.Run(() => TweakEngine.Undo(tw));
            }
            await RefreshTweakStates();
            await UpdateScore();
            F<TextBlock>("UndoStatus").Text = "Pronto: " + targets.Count + " ajustes desfeitos. Reinicie o PC.";
            F<TextBlock>("RebootHint").Visibility = Visibility.Visible;
        }

        // ================== Bandeja ==================
        private void SetupTray()
        {
            _tray = new WinForms.NotifyIcon { Text = "VoltCore Optimizer", Visible = true };
            try { _tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch { _tray.Icon = System.Drawing.SystemIcons.Application; }
            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("Abrir", null, (a, b) => ShowFromTray());
            menu.Items.Add("Limpar memória", null, (a, b) => Dispatcher.BeginInvoke(new Action(async () => await CleanMemory(false))));
            menu.Items.Add("Sair", null, (a, b) => { _exiting = true; Dispatcher.BeginInvoke(new Action(Close)); });
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (a, b) => ShowFromTray();
        }

        private void ShowFromTray()
        {
            Dispatcher.BeginInvoke(new Action(() => { Show(); WindowState = WindowState.Normal; Activate(); }));
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_exiting && Settings.GetBool("close_to_tray", true))
            {
                e.Cancel = true;
                Hide();
                if (!Settings.GetBool("tray_hint_shown", false))
                {
                    _tray.ShowBalloonTip(4000, "VoltCore continua ativo", "A limpeza automática de memória e os ajustes de jogos seguem rodando. Clique com o botão direito no ícone para sair.", WinForms.ToolTipIcon.Info);
                    Settings.Set("tray_hint_shown", true);
                }
                return;
            }
            TimerResolution.Disable();
            _tray.Visible = false;
            _tray.Dispose();
            Application.Current.Shutdown();
        }
    }
}
