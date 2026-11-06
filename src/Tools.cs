using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace VoltCore
{
    // ======================= PING / JITTER / PERDA =======================
    public class PingTarget
    {
        public string Name, Host;
        public readonly List<double?> Samples = new List<double?>(); // null = pacote perdido
        public int Sent { get { return Samples.Count; } }
        public int Lost { get { return Samples.Count(s => !s.HasValue); } }
        public double LossPct { get { return Sent == 0 ? 0 : Lost * 100.0 / Sent; } }
        public double? Last { get { return Samples.Count == 0 ? null : Samples[Samples.Count - 1]; } }

        public double Avg
        {
            get { var ok = Samples.Where(s => s.HasValue).Select(s => s.Value).ToList(); return ok.Count == 0 ? 0 : ok.Average(); }
        }

        public double Max
        {
            get { var ok = Samples.Where(s => s.HasValue).Select(s => s.Value).ToList(); return ok.Count == 0 ? 0 : ok.Max(); }
        }

        /// <summary>Jitter = média da diferença entre pings consecutivos (RFC 3550 simplificado).</summary>
        public double Jitter
        {
            get
            {
                var ok = Samples.Where(s => s.HasValue).Select(s => s.Value).ToList();
                if (ok.Count < 2) return 0;
                double sum = 0;
                for (int i = 1; i < ok.Count; i++) sum += Math.Abs(ok[i] - ok[i - 1]);
                return sum / (ok.Count - 1);
            }
        }
    }

    public static class PingMonitor
    {
        public static string Gateway()
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var g in n.GetIPProperties().GatewayAddresses)
                    if (g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))
                        return g.Address.ToString();
            }
            return null;
        }

        public static async Task<double?> PingOnce(string host)
        {
            try
            {
                using (var p = new Ping())
                {
                    var r = await p.SendPingAsync(host, 1000);
                    if (r.Status != IPStatus.Success) return null;
                    // RoundtripTime tem resolução de 1 ms; abaixo de 1 ms reporta 0.
                    return Math.Max(0.5, r.RoundtripTime);
                }
            }
            catch { return null; }
        }

        /// <summary>Diagnóstico em linguagem simples a partir das medições.</summary>
        public static string Diagnose(PingTarget router, PingTarget internet, bool wired)
        {
            if (internet == null || internet.Sent < 20) return "Medindo… deixe rodar pelo menos 30 segundos (de preferência jogando).";
            var sb = new List<string>();
            bool localBad = router != null && router.Sent >= 20 && (router.LossPct > 0 || router.Jitter > (wired ? 2 : 8) || router.Avg > (wired ? 3 : 15));
            bool netBad = internet.LossPct > 1 || internet.Jitter > 10;
            if (localBad)
                sb.Add("⚠ Problema na sua rede local (entre o PC e o roteador): " +
                       (router.LossPct > 0 ? router.LossPct.ToString("0.#") + "% de perda" : "ping instável (" + router.Jitter.ToString("0.0") + " ms de oscilação)") +
                       (wired ? ". Com cabo isso não deveria acontecer: troque o cabo de rede ou a porta do roteador." : ". No Wi-Fi é comum: aproxime do roteador, use 5 GHz ou, melhor, use cabo."));
            if (netBad && !localBad)
                sb.Add("⚠ A rede local está boa, mas a internet oscila (" + internet.LossPct.ToString("0.#") + "% de perda, " + internet.Jitter.ToString("0.0") + " ms de oscilação). O problema está no provedor ou em alguém usando muita banda na casa (downloads, streaming).");
            if (!localBad && !netBad)
                sb.Add("✓ Conexão saudável: sem perda de pacotes e com ping estável.");
            return string.Join("\n", sb);
        }
    }

    // ======================= TESTE DE FPS (PresentMon) =======================
    public class BenchResult
    {
        public DateTime Time;
        public string Game, Label;
        public int Seconds, Frames;
        public double AvgFps, Low1, Low01, AvgFrametime;
    }

    public static class Benchmark
    {
        public const string Version = "2.6.0";
        public const string Url = "https://github.com/GameTechDev/PresentMon/releases/download/v2.6.0/PresentMon-2.6.0-x64.exe";
        public const string Sha256 = "b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af";
        private const string File_ = "benchmarks.tsv";

        public static string ExePath { get { return Path.Combine(Store.Dir, @"tools\PresentMon-" + Version + ".exe"); } }

        public static bool Installed { get { return File.Exists(ExePath) && Hash(ExePath) == Sha256; } }

        private static string Hash(string path)
        {
            using (var s = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>Baixa o PresentMon oficial do GitHub e confere o SHA-256 publicado na release.</summary>
        public static string Download()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ExePath));
            string tmp = ExePath + ".download";
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using (var wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "VoltCore-Optimizer");
                    wc.DownloadFile(Url, tmp);
                }
                string h = Hash(tmp);
                if (h != Sha256)
                {
                    File.Delete(tmp);
                    return "O arquivo baixado não confere com o hash oficial (" + h.Substring(0, 12) + "…). Download descartado por segurança.";
                }
                if (File.Exists(ExePath)) File.Delete(ExePath);
                File.Move(tmp, ExePath);
                Store.Log("PRESENTMON " + Version + " baixado e verificado");
                return "";
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                return "Falha no download: " + ex.Message;
            }
        }

        /// <summary>Grava os quadros do processo por N segundos e calcula FPS médio, 1% e 0,1% low.</summary>
        public static BenchResult Run(string exeName, string game, string label, int seconds, out string error)
        {
            error = null;
            string csv = Path.Combine(Path.GetTempPath(), "voltcore_bench_" + DateTime.Now.Ticks + ".csv");
            string args = "--process_name " + Quote(exeName) + " --output_file " + Quote(csv) + " --timed " + seconds +
                          " --terminate_after_timed --terminate_on_proc_exit --stop_existing_session --session_name VoltCoreBench --no_console_stats --no_track_input";
            string o;
            int code = Shell.Run(ExePath, args, out o, (seconds + 40) * 1000);
            try
            {
                if (!File.Exists(csv)) { error = "Nenhum quadro gravado. O jogo estava aberto e renderizando? (" + (o ?? "").Trim() + ")"; return null; }
                var frames = Parse(csv, exeName);
                if (frames.Count < 30) { error = "Poucos quadros gravados (" + frames.Count + "). Fique dentro do jogo (não no menu minimizado) durante o teste."; return null; }
                var r = Stats(frames);
                r.Time = DateTime.Now; r.Game = game; r.Label = label; r.Seconds = seconds;
                Save(r);
                return r;
            }
            finally { try { File.Delete(csv); } catch { } }
        }

        private static string Quote(string s) { return "\"" + s + "\""; }

        /// <summary>Lê os frametimes (MsBetweenPresents) da swap chain com mais quadros do processo.</summary>
        public static List<double> Parse(string csv, string exeName)
        {
            var lines = File.ReadAllLines(csv);
            if (lines.Length < 2) return new List<double>();
            var head = lines[0].Split(',').Select(h => h.Trim()).ToList();
            int iApp = head.FindIndex(h => h.Equals("Application", StringComparison.OrdinalIgnoreCase));
            int iSc = head.FindIndex(h => h.Equals("SwapChainAddress", StringComparison.OrdinalIgnoreCase));
            int iFt = head.FindIndex(h => h.Equals("MsBetweenPresents", StringComparison.OrdinalIgnoreCase));
            if (iFt < 0) iFt = head.FindIndex(h => h.Equals("FrameTime", StringComparison.OrdinalIgnoreCase));
            if (iFt < 0) return new List<double>();
            var bySc = new Dictionary<string, List<double>>();
            foreach (var l in lines.Skip(1))
            {
                var c = l.Split(',');
                if (c.Length <= iFt) continue;
                if (iApp >= 0 && !c[iApp].Trim().Equals(exeName, StringComparison.OrdinalIgnoreCase)) continue;
                double ft;
                if (!double.TryParse(c[iFt], NumberStyles.Float, CultureInfo.InvariantCulture, out ft) || ft <= 0 || ft > 5000) continue;
                string sc = iSc >= 0 ? c[iSc] : "";
                List<double> list;
                if (!bySc.TryGetValue(sc, out list)) bySc[sc] = list = new List<double>();
                list.Add(ft);
            }
            return bySc.Count == 0 ? new List<double>() : bySc.Values.OrderByDescending(v => v.Count).First();
        }

        public static BenchResult Stats(List<double> ft)
        {
            var sorted = ft.OrderByDescending(x => x).ToList();
            Func<double, double> low = frac =>
            {
                int n = Math.Max(1, (int)Math.Round(sorted.Count * frac));
                return 1000.0 / sorted.Take(n).Average();
            };
            double mean = ft.Average();
            return new BenchResult { Frames = ft.Count, AvgFrametime = mean, AvgFps = 1000.0 / mean, Low1 = low(0.01), Low01 = low(0.001) };
        }

        private static void Save(BenchResult r)
        {
            var inv = CultureInfo.InvariantCulture;
            try
            {
                File.AppendAllText(Store.PathOf(File_), string.Join("\t", r.Time.ToString("yyyy-MM-dd HH:mm:ss"), r.Game.Replace("\t", " "), r.Label.Replace("\t", " "), r.Seconds, r.Frames,
                    r.AvgFps.ToString("0.0", inv), r.Low1.ToString("0.0", inv), r.Low01.ToString("0.0", inv), r.AvgFrametime.ToString("0.00", inv)) + Environment.NewLine, System.Text.Encoding.UTF8);
            }
            catch { }
        }

        public static List<BenchResult> History()
        {
            var inv = CultureInfo.InvariantCulture;
            var r = new List<BenchResult>();
            foreach (var l in Store.ReadLines(File_))
            {
                var p = l.Split('\t');
                DateTime t;
                if (p.Length < 9 || !DateTime.TryParseExact(p[0], "yyyy-MM-dd HH:mm:ss", null, DateTimeStyles.None, out t)) continue;
                try
                {
                    r.Add(new BenchResult { Time = t, Game = p[1], Label = p[2], Seconds = int.Parse(p[3]), Frames = int.Parse(p[4]), AvgFps = double.Parse(p[5], inv), Low1 = double.Parse(p[6], inv), Low01 = double.Parse(p[7], inv), AvgFrametime = double.Parse(p[8], inv) });
                }
                catch { }
            }
            return r;
        }

        public static void Clear() { Store.WriteLines(File_, new string[0]); }
    }

    // ======================= TESTE DE POLLING DO MOUSE =======================
    public class MouseRateTest
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICE
        {
            public ushort usUsagePage, usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

        private const int WM_INPUT = 0x00FF;
        private readonly Stopwatch _sw = new Stopwatch();
        private readonly List<int> _windows = new List<int>(); // eventos por janela de 100 ms
        private int _count;
        private long _windowStart;
        private IntPtr _hwnd;
        public bool Running { get; private set; }
        public int CurrentHz { get; private set; }

        public bool Start(IntPtr hwnd)
        {
            _hwnd = hwnd;
            var dev = new[] { new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = 0x100 /* INPUTSINK */, hwndTarget = hwnd } };
            if (!RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)))) return false;
            _windows.Clear(); _count = 0; CurrentHz = 0;
            _sw.Restart(); _windowStart = 0;
            Running = true;
            return true;
        }

        public void Stop()
        {
            if (!Running) return;
            var dev = new[] { new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = 0x1 /* REMOVE */, hwndTarget = IntPtr.Zero } };
            RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
            Running = false;
        }

        /// <summary>Chamado pelo hook da janela para cada mensagem.</summary>
        public void OnMessage(int msg)
        {
            if (!Running || msg != WM_INPUT) return;
            _count++;
            Roll();
        }

        public void Roll()
        {
            long now = _sw.ElapsedMilliseconds;
            while (now - _windowStart >= 100)
            {
                _windows.Add(_count);
                CurrentHz = _count * 10;
                _count = 0;
                _windowStart += 100;
            }
        }

        /// <summary>Taxa típica: mediana das janelas com o mouse em movimento contínuo.</summary>
        public int TypicalHz
        {
            get
            {
                var moving = _windows.Where(c => c >= 5).OrderBy(c => c).ToList();
                if (moving.Count < 3) return 0;
                // descarta as janelas mais lentas (início/fim do movimento)
                var top = moving.Skip(moving.Count / 2).ToList();
                return top[top.Count / 2] * 10;
            }
        }

        public int PeakHz { get { return _windows.Count == 0 ? 0 : _windows.Max() * 10; } }

        public static int Nearest(int hz)
        {
            int[] std = { 125, 250, 500, 1000, 2000, 4000, 8000 };
            return std.OrderBy(s => Math.Abs(s - hz)).First();
        }
    }
}
