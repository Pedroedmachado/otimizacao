using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoltCore
{
    public class GameEntry
    {
        public string Name, Exe, Source;
        public bool HighPriority, GpuHigh, NoFso, FreeCore0, PurgeOnStart;

        public string ExeName { get { return Path.GetFileName(Exe); } }
        public bool Exists { get { return File.Exists(Exe); } }
    }

    /// <summary>Configurações globais do DirectX (DirectXUserGlobalSettings), preservando as chaves que já existem.</summary>
    public static class DxGlobal
    {
        private const string Key = @"HKCU\Software\Microsoft\DirectX\UserGpuPreferences";
        private const string Name = "DirectXUserGlobalSettings";

        private static Dictionary<string, string> Read()
        {
            var d = new Dictionary<string, string>();
            var s = Convert.ToString(Reg.Get(Key, Name)) ?? "";
            foreach (var part in s.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int i = part.IndexOf('=');
                if (i > 0) d[part.Substring(0, i)] = part.Substring(i + 1);
            }
            return d;
        }

        public static string Get(string k)
        {
            string v;
            return Read().TryGetValue(k, out v) ? v : null;
        }

        public static void Set(string k, string v)
        {
            var d = Read();
            if (v == null) d.Remove(k); else d[k] = v;
            if (d.Count == 0) Reg.Delete(Key, Name);
            else Reg.Set(Key, Name, string.Join("", d.Select(kv => kv.Key + "=" + kv.Value + ";")), Microsoft.Win32.RegistryValueKind.String);
        }
    }

    public static class Games
    {
        private const string File_ = "games.tsv";
        private const string IFEO = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\";
        private const string GPU = @"HKCU\Software\Microsoft\DirectX\UserGpuPreferences";
        private const string LAYERS = @"HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";

        public static List<GameEntry> Load()
        {
            var r = new List<GameEntry>();
            foreach (var l in Store.ReadLines(File_))
            {
                var p = l.Split('\t');
                if (p.Length < 8) continue;
                r.Add(new GameEntry { Name = p[0], Exe = p[1], Source = p[2], HighPriority = p[3] == "1", GpuHigh = p[4] == "1", NoFso = p[5] == "1", FreeCore0 = p[6] == "1", PurgeOnStart = p[7] == "1" });
            }
            return r;
        }

        public static void Save(List<GameEntry> games)
        {
            Store.WriteLines(File_, games.Select(g => string.Join("\t", g.Name, g.Exe, g.Source, B(g.HighPriority), B(g.GpuHigh), B(g.NoFso), B(g.FreeCore0), B(g.PurgeOnStart))));
        }

        private static string B(bool b) { return b ? "1" : "0"; }

        /// <summary>Grava no registro os ajustes por jogo (prioridade, GPU, otimizações de tela cheia).</summary>
        public static void ApplyRegistry(GameEntry g)
        {
            string ifeo = IFEO + g.ExeName + @"\PerfOptions";
            if (g.HighPriority) Reg.Set(ifeo, "CpuPriorityClass", 3, Microsoft.Win32.RegistryValueKind.DWord);
            else Reg.Delete(ifeo, "CpuPriorityClass");

            if (g.GpuHigh) Reg.Set(GPU, g.Exe, "GpuPreference=2;", Microsoft.Win32.RegistryValueKind.String);
            else if (Convert.ToString(Reg.Get(GPU, g.Exe)) == "GpuPreference=2;") Reg.Delete(GPU, g.Exe);

            if (g.NoFso) Reg.Set(LAYERS, g.Exe, "~ DISABLEDXMAXIMIZEDWINDOWEDMODE", Microsoft.Win32.RegistryValueKind.String);
            else if (Convert.ToString(Reg.Get(LAYERS, g.Exe)) == "~ DISABLEDXMAXIMIZEDWINDOWEDMODE") Reg.Delete(LAYERS, g.Exe);
            Store.Log("JOGO ajustado: " + g.Name);
        }

        public static void ClearRegistry(GameEntry g)
        {
            var copy = new GameEntry { Name = g.Name, Exe = g.Exe };
            ApplyRegistry(copy);
        }

        // ---------------- Detecção ----------------
        public static List<GameEntry> DetectAll()
        {
            var r = new List<GameEntry>();
            try { r.AddRange(DetectSteam()); } catch { }
            try { r.AddRange(DetectEpic()); } catch { }
            return r;
        }

        private static IEnumerable<GameEntry> DetectSteam()
        {
            var steam = Convert.ToString(Reg.Get(@"HKCU\Software\Valve\Steam", "SteamPath"));
            if (string.IsNullOrEmpty(steam)) steam = Convert.ToString(Reg.Get(@"HKLM\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"));
            if (string.IsNullOrEmpty(steam)) yield break;
            steam = steam.Replace('/', '\\');
            var libs = new List<string> { steam };
            var vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    libs.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
            foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var apps = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(apps)) continue;
                foreach (var acf in Directory.GetFiles(apps, "appmanifest_*.acf"))
                {
                    string txt;
                    try { txt = File.ReadAllText(acf); } catch { continue; }
                    var name = Regex.Match(txt, "\"name\"\\s+\"([^\"]+)\"").Groups[1].Value;
                    var dir = Regex.Match(txt, "\"installdir\"\\s+\"([^\"]+)\"").Groups[1].Value;
                    if (name.Length == 0 || dir.Length == 0) continue;
                    if (Regex.IsMatch(name, "Steamworks|Redistributable|Proton|Steam Linux Runtime|SDK|Dedicated Server", RegexOptions.IgnoreCase)) continue;
                    var exe = GuessExe(Path.Combine(apps, "common", dir));
                    if (exe != null) yield return new GameEntry { Name = name, Exe = exe, Source = "Steam" };
                }
            }
        }

        private static IEnumerable<GameEntry> DetectEpic()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
            if (!Directory.Exists(dir)) yield break;
            foreach (var f in Directory.GetFiles(dir, "*.item"))
            {
                string txt;
                try { txt = File.ReadAllText(f); } catch { continue; }
                var name = JsonStr(txt, "DisplayName");
                var loc = JsonStr(txt, "InstallLocation");
                var exe = JsonStr(txt, "LaunchExecutable");
                if (name == null || loc == null || exe == null) continue;
                var full = Path.Combine(loc, exe.Replace('/', '\\'));
                if (File.Exists(full)) yield return new GameEntry { Name = name, Exe = full, Source = "Epic" };
            }
        }

        private static string JsonStr(string json, string key)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? Regex.Unescape(m.Groups[1].Value) : null;
        }

        private static readonly Regex Junk = new Regex("unins|setup|redist|vcredist|vc_redist|dxsetup|crash|report|helper|easyanticheat|eac_|beservice|battleye|launcherpatcher|prereq|install|update|unitycrash|dotnet|directx|ue4prereq|cefprocess|webhelper|overlay|hlds|hltv|srcds|proxy|dedicated|server|trial|benchmark|editor|sdk|tool", RegexOptions.IgnoreCase);

        /// <summary>Escolhe o executável principal: o maior .exe que não seja instalador/anticheat.</summary>
        public static string GuessExe(string dir)
        {
            if (!Directory.Exists(dir)) return null;
            var candidates = new List<FileInfo>();
            Collect(new DirectoryInfo(dir), 0, candidates);
            var best = candidates.Where(f => !Junk.IsMatch(f.Name)).OrderByDescending(f => f.Name.EndsWith("-Shipping.exe", StringComparison.OrdinalIgnoreCase) ? 1 : 0).ThenByDescending(f => f.Length).FirstOrDefault();
            return best == null ? null : best.FullName;
        }

        private static void Collect(DirectoryInfo d, int depth, List<FileInfo> acc)
        {
            if (depth > 4) return;
            try { acc.AddRange(d.GetFiles("*.exe")); } catch { }
            try { foreach (var s in d.GetDirectories()) if (!Junk.IsMatch(s.Name) && !s.Name.StartsWith("_")) Collect(s, depth + 1, acc); } catch { }
        }
    }

    /// <summary>
    /// Vigia os jogos cadastrados. Quando um jogo abre: libera o núcleo 0 (afinidade), aplica prioridade alta
    /// em tempo real e limpa a memória standby uma vez.
    /// </summary>
    public class GameWatcher
    {
        private readonly HashSet<int> _handled = new HashSet<int>();
        public event Action<string> Message;
        public List<GameEntry> Games = new List<GameEntry>();
        public HashSet<int> RunningGamePids = new HashSet<int>();

        public void Tick()
        {
            var alive = new HashSet<int>();
            foreach (var g in Games.ToList())
            {
                Process[] procs;
                try { procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(g.Exe)); } catch { continue; }
                foreach (var p in procs)
                {
                    try
                    {
                        alive.Add(p.Id);
                        if (_handled.Contains(p.Id)) continue;
                        _handled.Add(p.Id);
                        var done = new List<string>();
                        if (g.FreeCore0 && Environment.ProcessorCount > 2)
                        {
                            long all = Environment.ProcessorCount >= 64 ? -1L : (1L << Environment.ProcessorCount) - 1;
                            p.ProcessorAffinity = new IntPtr(all & ~1L);
                            done.Add("núcleo 0 livre");
                        }
                        if (g.HighPriority)
                        {
                            p.PriorityClass = ProcessPriorityClass.High;
                            done.Add("prioridade alta");
                        }
                        if (g.PurgeOnStart)
                        {
                            MemoryCleaner.PurgeStandby();
                            done.Add("memória limpa");
                        }
                        if (Message != null) Message(g.Name + " detectado" + (done.Count > 0 ? ": " + string.Join(", ", done) : ""));
                    }
                    catch (Exception ex)
                    {
                        if (Message != null) Message(g.Name + ": não foi possível ajustar (" + ex.Message + "). Alguns anti-cheats bloqueiam isso.");
                    }
                    finally { p.Dispose(); }
                }
            }
            _handled.IntersectWith(alive);
            RunningGamePids = alive;
        }
    }
}
