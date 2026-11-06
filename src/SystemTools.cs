using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoltCore
{
    // ======================= INICIALIZAÇÃO =======================
    public class StartupItem
    {
        public string Name, Command, Source, ApprovedKey, ApprovedName;
        public bool Enabled;
    }

    public static class StartupManager
    {
        private const string APPROVED = @"\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

        public static List<StartupItem> List()
        {
            var r = new List<StartupItem>();
            AddRun(r, @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", "Usuário", "HKCU" + APPROVED + "Run");
            AddRun(r, @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Todos os usuários", "HKLM" + APPROVED + "Run");
            AddRun(r, @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Todos (32 bits)", "HKLM" + APPROVED + "Run32");
            AddFolder(r, Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Pasta Inicializar", "HKCU" + APPROVED + "StartupFolder");
            AddFolder(r, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "Pasta Inicializar (todos)", "HKLM" + APPROVED + "StartupFolder");
            return r.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void AddRun(List<StartupItem> r, string key, string src, string approved)
        {
            foreach (var n in Reg.ValueNames(key))
            {
                if (string.IsNullOrEmpty(n)) continue;
                r.Add(new StartupItem { Name = n, Command = Convert.ToString(Reg.Get(key, n)), Source = src, ApprovedKey = approved, ApprovedName = n, Enabled = IsEnabled(approved, n) });
            }
        }

        private static void AddFolder(List<StartupItem> r, string dir, string src, string approved)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir))
            {
                var fn = Path.GetFileName(f);
                if (fn.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                r.Add(new StartupItem { Name = Path.GetFileNameWithoutExtension(f), Command = f, Source = src, ApprovedKey = approved, ApprovedName = fn, Enabled = IsEnabled(approved, fn) });
            }
        }

        private static bool IsEnabled(string approved, string name)
        {
            var v = Reg.Get(approved, name) as byte[];
            return v == null || v.Length == 0 || (v[0] & 1) == 0;
        }

        public static void SetEnabled(StartupItem it, bool on)
        {
            var data = new byte[12];
            data[0] = (byte)(on ? 2 : 3);
            if (!on) BitConverter.GetBytes(DateTime.Now.ToFileTimeUtc()).CopyTo(data, 4);
            Reg.Set(it.ApprovedKey, it.ApprovedName, data, Microsoft.Win32.RegistryValueKind.Binary);
            it.Enabled = on;
            Store.Log("INICIALIZAÇÃO " + (on ? "ligado " : "desligado ") + it.Name);
        }
    }

    // ======================= LIMPEZA =======================
    public class CleanTarget
    {
        public string Name, Desc;
        public string[] Paths;
        public bool Default, StopUpdate, RecycleBin;
        public long Bytes;
    }

    public static class Cleaner
    {
        public static List<CleanTarget> Targets()
        {
            string la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return new List<CleanTarget>
            {
                new CleanTarget { Name = "Arquivos temporários", Desc = "Pasta TEMP do usuário e do Windows.", Paths = new[] { Path.GetTempPath(), Path.Combine(win, "Temp") }, Default = true },
                new CleanTarget { Name = "Cache do Windows Update", Desc = "Instaladores de atualizações já aplicadas.", Paths = new[] { Path.Combine(win, @"SoftwareDistribution\Download") }, Default = true, StopUpdate = true },
                new CleanTarget { Name = "Otimização de Entrega", Desc = "Cache de atualizações compartilhadas.", Paths = new[] { Path.Combine(win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache") }, Default = true },
                new CleanTarget { Name = "Relatórios de erro e despejos", Desc = "Relatórios de falha (WER) e crash dumps.", Paths = new[] { Path.Combine(pd, @"Microsoft\Windows\WER"), Path.Combine(la, "CrashDumps"), Path.Combine(win, "Minidump"), Path.Combine(la, @"Microsoft\Windows\WER") }, Default = true },
                new CleanTarget { Name = "Cache de miniaturas", Desc = "Miniaturas do Explorador (são recriadas).", Paths = new[] { Path.Combine(la, @"Microsoft\Windows\Explorer") }, Default = false },
                new CleanTarget { Name = "Cache de shaders (DirectX/NVIDIA/AMD)", Desc = "Recriado pelos jogos. Limpe após atualizar o driver de vídeo; os primeiros minutos de jogo podem ter travadinhas.", Paths = new[] { Path.Combine(la, "D3DSCache"), Path.Combine(la, @"NVIDIA\DXCache"), Path.Combine(la, @"NVIDIA\GLCache"), Path.Combine(la, @"AMD\DxCache"), Path.Combine(la, @"AMD\DxcCache"), Path.Combine(la, @"AMD\VkCache") }, Default = false },
                new CleanTarget { Name = "Lixeira", Desc = "Esvazia a Lixeira de todos os discos (não dá para recuperar depois).", Paths = new string[0], Default = false, RecycleBin = true },
            };
        }

        public static void Measure(CleanTarget t)
        {
            if (t.RecycleBin)
            {
                var info = new Native.SHQUERYRBINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.SHQUERYRBINFO)) };
                Native.SHQueryRecycleBin(null, ref info);
                t.Bytes = info.i64Size;
                return;
            }
            long sum = 0;
            foreach (var p in t.Paths)
            {
                if (t.Name.StartsWith("Cache de miniaturas")) sum += SafeFiles(p, "thumbcache_*.db").Sum(f => Len(f));
                else sum += SafeFiles(p, "*").Sum(f => Len(f));
            }
            t.Bytes = sum;
        }

        public static long Clean(CleanTarget t)
        {
            long freed = 0;
            if (t.RecycleBin)
            {
                Measure(t);
                freed = t.Bytes;
                Native.SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4);
                return freed;
            }
            if (t.StopUpdate) { Shell.Run("net", "stop wuauserv /y"); Shell.Run("net", "stop bits /y"); }
            try
            {
                foreach (var p in t.Paths)
                {
                    var pattern = t.Name.StartsWith("Cache de miniaturas") ? "thumbcache_*.db" : "*";
                    foreach (var f in SafeFiles(p, pattern))
                    {
                        try
                        {
                            long l = Len(f);
                            File.SetAttributes(f, FileAttributes.Normal);
                            File.Delete(f);
                            freed += l;
                        }
                        catch { }
                    }
                    if (pattern == "*" && Directory.Exists(p))
                    {
                        foreach (var d in SafeDirs(p).OrderByDescending(d => d.Length))
                        {
                            try { Directory.Delete(d, false); } catch { }
                        }
                    }
                }
            }
            finally
            {
                if (t.StopUpdate) { Shell.Run("net", "start bits"); Shell.Run("net", "start wuauserv"); }
            }
            Store.Log("LIMPEZA " + t.Name + ": " + Fmt.Bytes(freed));
            return freed;
        }

        private static long Len(string f) { try { return new FileInfo(f).Length; } catch { return 0; } }

        private static IEnumerable<string> SafeFiles(string dir, string pattern)
        {
            var stack = new Stack<string>();
            if (Directory.Exists(dir)) stack.Push(dir);
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                string[] files = new string[0], subs = new string[0];
                try { files = Directory.GetFiles(d, pattern); } catch { }
                try
                {
                    if (pattern == "*")
                        subs = Directory.GetDirectories(d).Where(s => (new DirectoryInfo(s).Attributes & FileAttributes.ReparsePoint) == 0).ToArray();
                }
                catch { }
                foreach (var f in files) yield return f;
                foreach (var s in subs) stack.Push(s);
            }
        }

        private static IEnumerable<string> SafeDirs(string dir)
        {
            var r = new List<string>();
            var stack = new Stack<string>();
            stack.Push(dir);
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                try
                {
                    foreach (var s in Directory.GetDirectories(d))
                    {
                        if ((new DirectoryInfo(s).Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        r.Add(s);
                        stack.Push(s);
                    }
                }
                catch { }
            }
            return r;
        }
    }

    // ======================= RESTAURAÇÃO =======================
    public static class RestorePoints
    {
        public static string Create(string description)
        {
            var o = Shell.Ps(
                "New-Item 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\SystemRestore' -Force | Out-Null; " +
                "Set-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\SystemRestore' -Name SystemRestorePointCreationFrequency -Value 0 -Type DWord; " +
                "Enable-ComputerRestore -Drive \"$env:SystemDrive\\\" -ErrorAction SilentlyContinue; " +
                "try { Checkpoint-Computer -Description " + Shell.Q(description) + " -RestorePointType MODIFY_SETTINGS -ErrorAction Stop; 'OK' } catch { 'ERRO: ' + $_.Exception.Message }", 600000);
            Store.Log("PONTO DE RESTAURAÇÃO: " + o.Trim());
            return o.Trim();
        }

        public static List<string> List()
        {
            var o = Shell.Ps("Get-ComputerRestorePoint -ErrorAction SilentlyContinue | Sort-Object SequenceNumber -Descending | ForEach-Object { $_.ConvertToDateTime($_.CreationTime).ToString('dd/MM/yyyy HH:mm') + '|' + $_.Description }", 60000);
            return o.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Where(l => l.Contains("|")).ToList();
        }
    }

    // ======================= MANUTENÇÃO AGENDADA =======================
    public static class Maintenance
    {
        public const string TaskName = "VoltCore\\Revisao semanal";

        public static bool IsScheduled()
        {
            string o;
            return Shell.Run("schtasks", "/Query /TN \"" + TaskName + "\"", out o, 15000) == 0;
        }

        public static bool Schedule(bool on)
        {
            string o;
            if (!on) return Shell.Run("schtasks", "/Delete /TN \"" + TaskName + "\" /F", out o, 15000) == 0;
            string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
            string args = "/Create /TN \"" + TaskName + "\" /SC WEEKLY /D SUN /ST 12:00 /RL HIGHEST /F /TR \"\\\"" + exe + "\\\" --maintenance\"";
            return Shell.Run("schtasks", args, out o, 15000) == 0;
        }

        /// <summary>Executado pela tarefa agendada: limpeza de arquivos seguros + memória.</summary>
        public static void Run()
        {
            Store.Log("REVISÃO SEMANAL iniciada");
            foreach (var t in Cleaner.Targets().Where(t => t.Default)) Cleaner.Clean(t);
            MemoryCleaner.QuickClean();
            Store.Log("REVISÃO SEMANAL concluída");
        }
    }

    public static class Fmt
    {
        public static string Bytes(long b)
        {
            if (b >= 1L << 30) return (b / (double)(1L << 30)).ToString("0.0") + " GB";
            if (b >= 1L << 20) return (b / (double)(1L << 20)).ToString("0") + " MB";
            if (b >= 1L << 10) return (b / 1024.0).ToString("0") + " KB";
            return b + " B";
        }

        public static string MB(double mb)
        {
            return mb >= 1024 ? (mb / 1024).ToString("0.0") + " GB" : mb.ToString("0") + " MB";
        }
    }
}
