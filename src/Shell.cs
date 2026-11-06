using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace VoltCore
{
    /// <summary>Executa processos e scripts PowerShell de forma oculta.</summary>
    internal static class Shell
    {
        public static int Run(string file, string args, out string output, int timeoutMs)
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            var sb = new StringBuilder();
            try
            {
                using (var p = new Process { StartInfo = psi })
                {
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        output = sb.ToString() + "\n[tempo esgotado]";
                        return -1;
                    }
                    p.WaitForExit();
                    output = sb.ToString();
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                output = ex.Message;
                return -1;
            }
        }

        public static int Run(string file, string args)
        {
            string o;
            return Run(file, args, out o, 120000);
        }

        public static string Ps(string script, int timeoutMs = 300000)
        {
            string full = "$ProgressPreference='SilentlyContinue'; $ErrorActionPreference='Continue'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; " + script;
            string enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
            string o;
            Run(Sys("WindowsPowerShell\\v1.0\\powershell.exe"), "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + enc, out o, timeoutMs);
            return o ?? "";
        }

        /// <summary>Abre um console visível (para SFC/DISM, onde o usuário quer ver o progresso).</summary>
        public static void OpenConsole(string command)
        {
            Process.Start(new ProcessStartInfo(Sys("cmd.exe"), "/k " + command) { UseShellExecute = true });
        }

        public static void Open(string target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
        }

        public static string Sys(string rel)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), rel);
        }

        public static string Q(string s)
        {
            return "'" + s.Replace("'", "''") + "'";
        }
    }

    /// <summary>Arquivos de estado em %ProgramData%\VoltCore.</summary>
    internal static class Store
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VoltCore");

        static Store()
        {
            try { Directory.CreateDirectory(Dir); } catch { }
        }

        public static string PathOf(string name) { return Path.Combine(Dir, name); }

        public static List<string> ReadLines(string name)
        {
            var p = PathOf(name);
            if (!File.Exists(p)) return new List<string>();
            try { return new List<string>(File.ReadAllLines(p, Encoding.UTF8)); }
            catch { return new List<string>(); }
        }

        public static void WriteLines(string name, IEnumerable<string> lines)
        {
            try { File.WriteAllLines(PathOf(name), lines, new UTF8Encoding(false)); } catch { }
        }

        public static void Log(string line)
        {
            try { File.AppendAllText(PathOf("log.txt"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine, Encoding.UTF8); } catch { }
        }
    }

    /// <summary>Configurações simples chave=valor.</summary>
    internal static class Settings
    {
        private static Dictionary<string, string> _d;

        private static Dictionary<string, string> D
        {
            get
            {
                if (_d == null)
                {
                    _d = new Dictionary<string, string>();
                    foreach (var l in Store.ReadLines("settings.txt"))
                    {
                        int i = l.IndexOf('=');
                        if (i > 0) _d[l.Substring(0, i)] = l.Substring(i + 1);
                    }
                }
                return _d;
            }
        }

        public static string Get(string k, string def)
        {
            string v;
            return D.TryGetValue(k, out v) ? v : def;
        }

        public static int GetInt(string k, int def)
        {
            int v;
            return int.TryParse(Get(k, def.ToString()), out v) ? v : def;
        }

        public static bool GetBool(string k, bool def) { return Get(k, def ? "1" : "0") == "1"; }

        public static void Set(string k, object v)
        {
            D[k] = v is bool ? ((bool)v ? "1" : "0") : Convert.ToString(v);
            var lines = new List<string>();
            foreach (var kv in D) lines.Add(kv.Key + "=" + kv.Value);
            Store.WriteLines("settings.txt", lines);
        }
    }
}
