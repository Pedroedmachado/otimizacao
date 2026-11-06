using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace VoltCore
{
    public class LiveStats
    {
        public double CpuPct, CpuMhz, GpuPct = -1, VramUsedMB = -1, GpuTemp = -1, GpuClock = -1, GpuPower = -1, GpuFan = -1;
    }

    /// <summary>
    /// Monitor ao vivo. Usa os contadores do Windows (mesmos do Gerenciador de Tarefas) para CPU/GPU/VRAM
    /// em qualquer placa, e o nvidia-smi para temperatura/clock/consumo em placas NVIDIA.
    /// </summary>
    public class LiveMonitor : IDisposable
    {
        private IntPtr _q, _gpu, _vram, _perf, _freq;
        private bool _ok;
        private readonly string _nvsmi;
        private readonly CpuMeter _cpu = new CpuMeter();

        public LiveMonitor()
        {
            if (Native.PdhOpenQuery(null, IntPtr.Zero, out _q) == 0)
            {
                Native.PdhAddEnglishCounter(_q, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out _gpu);
                Native.PdhAddEnglishCounter(_q, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out _vram);
                Native.PdhAddEnglishCounter(_q, @"\Processor Information(_Total)\% Processor Performance", IntPtr.Zero, out _perf);
                Native.PdhAddEnglishCounter(_q, @"\Processor Information(_Total)\Processor Frequency", IntPtr.Zero, out _freq);
                Native.PdhCollectQueryData(_q);
                _ok = true;
            }
            var candidates = new[]
            {
                Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"NVIDIA Corporation\NVSMI\nvidia-smi.exe")
            };
            _nvsmi = candidates.FirstOrDefault(File.Exists);
        }

        public bool HasNvidia { get { return _nvsmi != null; } }

        private static Dictionary<string, double> Read(IntPtr counter)
        {
            var r = new Dictionary<string, double>();
            if (counter == IntPtr.Zero) return r;
            uint size = 0, count;
            const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000;
            int st = Native.PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, IntPtr.Zero);
            if (size == 0) return r;
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (Native.PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, buf) != 0) return r;
                int stride = IntPtr.Size == 8 ? 24 : 16;
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = buf + i * stride;
                    string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item));
                    int status = Marshal.ReadInt32(item, IntPtr.Size);
                    if (status != 0 && status != 0x00000001) continue;
                    double v = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, IntPtr.Size + 8));
                    if (name != null) r[name] = v;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return r;
        }

        public LiveStats Sample()
        {
            var s = new LiveStats { CpuPct = _cpu.Sample() };
            if (_ok)
            {
                Native.PdhCollectQueryData(_q);
                // Uso de GPU: soma dos motores 3D por adaptador, pega o adaptador mais usado (ignora a iGPU ociosa).
                var gpu = Read(_gpu);
                if (gpu.Count > 0)
                {
                    s.GpuPct = gpu.GroupBy(kv => { var m = Regex.Match(kv.Key, "luid_0x[0-9a-fA-F]+_0x[0-9a-fA-F]+"); return m.Success ? m.Value : ""; })
                                  .Select(g => g.Sum(kv => kv.Value)).DefaultIfEmpty(0).Max();
                    s.GpuPct = Math.Min(100, s.GpuPct);
                }
                var vram = Read(_vram);
                if (vram.Count > 0) s.VramUsedMB = vram.Values.Max() / 1048576.0;
                var perf = Read(_perf);
                var freq = Read(_freq);
                if (perf.Count > 0 && freq.Count > 0) s.CpuMhz = freq.Values.First() * perf.Values.First() / 100.0;
            }
            if (_nvsmi != null)
            {
                string o;
                if (Shell.Run(_nvsmi, "--query-gpu=temperature.gpu,clocks.gr,power.draw,fan.speed --format=csv,noheader,nounits", out o, 4000) == 0)
                {
                    var p = (o ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    if (p != null)
                    {
                        var f = p.Split(',').Select(x => x.Trim()).ToArray();
                        double v;
                        var inv = System.Globalization.CultureInfo.InvariantCulture;
                        if (f.Length > 0 && double.TryParse(f[0], System.Globalization.NumberStyles.Float, inv, out v)) s.GpuTemp = v;
                        if (f.Length > 1 && double.TryParse(f[1], System.Globalization.NumberStyles.Float, inv, out v)) s.GpuClock = v;
                        if (f.Length > 2 && double.TryParse(f[2], System.Globalization.NumberStyles.Float, inv, out v)) s.GpuPower = v;
                        if (f.Length > 3 && double.TryParse(f[3], System.Globalization.NumberStyles.Float, inv, out v)) s.GpuFan = v;
                    }
                }
            }
            return s;
        }

        public void Dispose() { }
    }

    /// <summary>Resolução do timer do Windows (0,5 ms deixa o frametime mais estável em muitos jogos).</summary>
    public static class TimerResolution
    {
        private static bool _active;

        public static double CurrentMs()
        {
            uint min, max, cur;
            return Native.NtQueryTimerResolution(out min, out max, out cur) == 0 ? cur / 10000.0 : -1;
        }

        public static void Enable()
        {
            if (_active) return;
            uint cur;
            if (Native.NtSetTimerResolution(5000, true, out cur) == 0) _active = true;
        }

        public static void Disable()
        {
            if (!_active) return;
            uint cur;
            Native.NtSetTimerResolution(5000, false, out cur);
            _active = false;
        }

        public static bool Active { get { return _active; } }
    }

    /// <summary>Inicia o app com o Windows, minimizado na bandeja, com privilégio de admin e sem pedir UAC.</summary>
    public static class AutoStart
    {
        private const string Name = "Iniciar com o Windows";

        public static bool IsEnabled()
        {
            string o;
            return Shell.Run("schtasks", "/Query /TN \"\\VoltCore\\" + Name + "\"", out o, 15000) == 0;
        }

        public static string Set(bool on)
        {
            if (!on)
            {
                Shell.Ps("Unregister-ScheduledTask -TaskName " + Shell.Q(Name) + " -TaskPath '\\VoltCore\\' -Confirm:$false -ErrorAction SilentlyContinue");
                Settings.Set("autostart_path", "");
                return "";
            }
            string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var o = Shell.Ps(
                "$u = \"$env:USERDOMAIN\\$env:USERNAME\"; " +
                "$a = New-ScheduledTaskAction -Execute " + Shell.Q(exe) + " -Argument '--tray'; " +
                "$t = New-ScheduledTaskTrigger -AtLogOn -User $u; $t.Delay = 'PT15S'; " +
                "$p = New-ScheduledTaskPrincipal -UserId $u -LogonType Interactive -RunLevel Highest; " +
                "$s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew; " +
                "try { Register-ScheduledTask -TaskName " + Shell.Q(Name) + " -TaskPath '\\VoltCore\\' -Action $a -Trigger $t -Principal $p -Settings $s -Force -ErrorAction Stop | Out-Null; 'OK' } catch { 'ERRO: ' + $_.Exception.Message }", 60000).Trim();
            if (o.StartsWith("OK")) Settings.Set("autostart_path", exe);
            Store.Log("INICIAR COM WINDOWS: " + o);
            return o.StartsWith("OK") ? "" : o;
        }

        /// <summary>Se o .exe mudou de lugar, atualiza a tarefa.</summary>
        public static void Repair()
        {
            string saved = Settings.Get("autostart_path", "");
            string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (saved.Length > 0 && !string.Equals(saved, exe, StringComparison.OrdinalIgnoreCase)) Set(true);
        }
    }

    /// <summary>Fecha programas em segundo plano escolhidos pelo usuário quando um jogo abre.</summary>
    public static class BackgroundCloser
    {
        public const string DefaultList = "OneDrive, ms-teams, PhoneExperienceHost, WidgetService, Widgets";

        public static List<string> Names()
        {
            return Settings.Get("close_list", DefaultList).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0).Select(s => s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s.Substring(0, s.Length - 4) : s).ToList();
        }

        public static List<string> CloseAll()
        {
            var closed = new List<string>();
            foreach (var n in Names())
            {
                Process[] ps;
                try { ps = Process.GetProcessesByName(n); } catch { continue; }
                foreach (var p in ps)
                {
                    try
                    {
                        if (p.MainWindowHandle != IntPtr.Zero && p.CloseMainWindow()) { if (!p.WaitForExit(3000)) p.Kill(); }
                        else p.Kill();
                        if (!closed.Contains(n)) closed.Add(n);
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            if (closed.Count > 0) Store.Log("SESSÃO DE JOGO fechou: " + string.Join(", ", closed));
            return closed;
        }
    }
}
