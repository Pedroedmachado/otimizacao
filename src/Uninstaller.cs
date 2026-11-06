using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VoltCore
{
    public class InstalledApp
    {
        public string Name, Publisher, Version, InstallLocation, UninstallString, QuietUninstall, DisplayIcon, KeyPath, InstallDate;
        public long SizeKB;
        public bool Msi;
        public string ProductCode;

        public string SizeText { get { return SizeKB > 0 ? Fmt.Bytes(SizeKB * 1024) : ""; } }
    }

    public enum Confidence { Seguro = 0, Moderado = 1, Avancado = 2 }
    public enum LeftoverKind { Pasta, Arquivo, ChaveRegistro, ValorRegistro, Servico, Tarefa }

    public class Leftover
    {
        public LeftoverKind Kind;
        public Confidence Level;
        public string Path;        // pasta/arquivo, chave (HKLM\...), nome do serviço ou da tarefa
        public string ValueName;   // para ValorRegistro
        public string Reason;
        public long Bytes;
    }

    /// <summary>
    /// Desinstalador completo: roda o desinstalador oficial e depois procura sobras (pastas, registro,
    /// atalhos, serviços e tarefas). Pastas vão para a Lixeira; registro é exportado em .reg antes de apagar.
    /// </summary>
    public static class Uninstaller
    {
        private static readonly string[] UninstallRoots =
        {
            @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        // Nunca oferecer estes nomes como pasta/chave (compartilhados pelo sistema ou por vários programas).
        private static readonly HashSet<string> Protected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft", "Windows", "Common Files", "WindowsApps", "Microsoft Shared", "Microsoft.NET", "dotnet", "Windows Defender",
            "Windows NT", "Windows Mail", "Windows Media Player", "Windows Photo Viewer", "Windows Sidebar", "Internet Explorer",
            "Packages", "Temp", "Programs", "Classes", "Policies", "Wow6432Node", "Clients", "RegisteredApplications", "ODBC",
            "Intel", "NVIDIA", "NVIDIA Corporation", "AMD", "ATI", "Realtek", "Google", "Mozilla", "Apple", "Adobe", "Steam",
            "Epic Games", "Program Files", "ProgramData", "Users", "Public", "Default", "System32", "SysWOW64", "Drivers",
            "Microsoft Office", "Microsoft Visual Studio", "Reference Assemblies", "MSBuild", "Package Cache", "Installer",
            "Start Menu", "Startup", "Desktop", "Documents", "Downloads", "Accessories", "Administrative Tools", "Maintenance",
            "System Tools", "Accessibility", "VoltCore", "Unknown", "Software", "Games", "Tools", "Apps", "App", "Data", "Logs", "Cache", "Config", "Settings",
            "Application", "Applications", "bin", "bin32", "bin64", "x64", "x86", "current", "latest", "lib", "resources", "Launcher", "Client", "Uninstall", "Uninstaller", "Setup", "Update", "Updater"
        };

        // ===================== Lista de programas =====================
        public static List<InstalledApp> List()
        {
            var r = new List<InstalledApp>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in UninstallRoots)
            {
                foreach (var sub in Reg.SubKeys(root))
                {
                    string k = root + "\\" + sub;
                    try
                    {
                        string name = Str(k, "DisplayName");
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        if (Int(k, "SystemComponent") == 1 || Str(k, "ParentKeyName").Length > 0) continue;
                        string rel = Str(k, "ReleaseType");
                        if (Regex.IsMatch(rel, "Update|Hotfix", RegexOptions.IgnoreCase) || Regex.IsMatch(name, @"^(Security )?Update for|\(KB\d+\)", RegexOptions.IgnoreCase)) continue;
                        string unins = Str(k, "UninstallString");
                        bool msi = Int(k, "WindowsInstaller") == 1;
                        if (unins.Length == 0 && !msi) continue;
                        string ver = Str(k, "DisplayVersion");
                        if (!seen.Add(name + "|" + ver)) continue;
                        var app = new InstalledApp
                        {
                            Name = name.Trim(), Publisher = Str(k, "Publisher").Trim(), Version = ver, KeyPath = k,
                            InstallLocation = Str(k, "InstallLocation").Trim().Trim('"').TrimEnd('\\'),
                            UninstallString = unins, QuietUninstall = Str(k, "QuietUninstallString"), DisplayIcon = Str(k, "DisplayIcon"),
                            SizeKB = Int(k, "EstimatedSize"), Msi = msi, InstallDate = FormatDate(Str(k, "InstallDate"))
                        };
                        if (Regex.IsMatch(sub, @"^\{[0-9A-Fa-f\-]{36}\}$")) app.ProductCode = sub;
                        r.Add(app);
                    }
                    catch { }
                }
            }
            return r.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string Str(string k, string n) { try { return Convert.ToString(Reg.Get(k, n)) ?? ""; } catch { return ""; } }
        private static long Int(string k, string n) { try { var v = Reg.Get(k, n); return v == null ? 0 : Convert.ToInt64(v); } catch { return 0; } }

        private static string FormatDate(string d)
        {
            DateTime t;
            return DateTime.TryParseExact(d, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out t) ? t.ToString("dd/MM/yyyy") : "";
        }

        public static bool StillInstalled(InstalledApp a)
        {
            string sub;
            using (var root = Reg.Root(a.KeyPath, out sub))
            using (var k = root.OpenSubKey(sub))
                return k != null;
        }

        // ===================== Desinstalação =====================
        /// <summary>Separa o executável dos argumentos de uma UninstallString.</summary>
        public static Tuple<string, string> SplitCommand(string cmd)
        {
            cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
            if (cmd.StartsWith("\""))
            {
                int end = cmd.IndexOf('"', 1);
                if (end > 0) return Tuple.Create(cmd.Substring(1, end - 1), cmd.Substring(end + 1).Trim());
            }
            var m = Regex.Match(cmd, @"^(.+?\.exe)(\s.*)?$", RegexOptions.IgnoreCase);
            if (m.Success) return Tuple.Create(m.Groups[1].Value, m.Groups[2].Value.Trim());
            int sp = cmd.IndexOf(' ');
            return sp < 0 ? Tuple.Create(cmd, "") : Tuple.Create(cmd.Substring(0, sp), cmd.Substring(sp + 1));
        }

        /// <summary>Executa o desinstalador oficial e espera ele (e as cópias temporárias que ele abre) terminar.</summary>
        public static string RunUninstaller(InstalledApp a, Func<bool> cancelWait)
        {
            string file, args;
            if (a.Msi && a.ProductCode != null) { file = Path.Combine(Environment.SystemDirectory, "msiexec.exe"); args = "/x " + a.ProductCode; }
            else
            {
                var sc = SplitCommand(a.UninstallString);
                file = sc.Item1; args = sc.Item2;
                if (Path.GetFileName(file).Equals("MsiExec.exe", StringComparison.OrdinalIgnoreCase))
                    args = Regex.Replace(args, @"/I\s*\{", "/X{", RegexOptions.IgnoreCase);
            }
            if (!Path.IsPathRooted(file) && !file.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) && !File.Exists(file))
                return "Desinstalador não encontrado: " + file;
            if (Path.IsPathRooted(file) && !File.Exists(file))
                return "O desinstalador não existe mais (" + file + "). Use a Remoção forçada.";

            DateTime start = DateTime.Now.AddSeconds(-1);
            Store.Log("DESINSTALAR " + a.Name + ": " + file + " " + args);
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(file) ?? "" }))
                {
                    if (p != null)
                        while (!p.WaitForExit(500)) if (cancelWait()) return "";
                }
            }
            catch (Exception ex) { return "Não foi possível iniciar o desinstalador: " + ex.Message; }

            // Muitos desinstaladores (NSIS, Inno Setup) se copiam para a pasta TEMP e saem na hora.
            string temp = Path.GetTempPath();
            var rx = new Regex(@"^(au_|un_a|un_|unins|uninst|uninstall|_iu|setup|msiexec|.*uninstall.*)", RegexOptions.IgnoreCase);
            for (int idle = 0; idle < 6; )
            {
                if (cancelWait()) return "";
                bool any = false;
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        if (p.StartTime < start || p.Id == Process.GetCurrentProcess().Id) continue;
                        string path = "";
                        try { path = p.MainModule.FileName; } catch { }
                        if (rx.IsMatch(p.ProcessName) || (path.Length > 0 && path.StartsWith(temp, StringComparison.OrdinalIgnoreCase))) { any = true; break; }
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
                idle = any ? 0 : idle + 1;
                System.Threading.Thread.Sleep(500);
            }
            return "";
        }

        // ===================== Varredura de sobras =====================
        private static string Norm(string s) { return Regex.Replace(s ?? "", @"[^\p{L}\p{N}]", "").ToLowerInvariant(); }

        /// <summary>Nomes possíveis do programa: "7-Zip 23.01 (x64)" → "7-Zip"; "Mozilla Firefox" → também "Firefox".</summary>
        public static List<string> NameKeys(InstalledApp a)
        {
            var keys = new List<string>();
            string n = Regex.Replace(a.Name, @"\(.*?\)|\[.*?\]", " ");
            n = Regex.Replace(n, @"\b(v(ersion)?\s*)?\d+([\.\-_]\d+)+\w*\b", " ", RegexOptions.IgnoreCase);
            n = Regex.Replace(n, @"\b(x64|x86|64-bit|32-bit|amd64|win64|win32)\b", " ", RegexOptions.IgnoreCase);
            n = Regex.Replace(n, @"\s+", " ").Trim(' ', '-', '_', '.');
            if (n.Length >= 3) keys.Add(n);
            if (!string.IsNullOrEmpty(a.Publisher))
            {
                string pubFirst = a.Publisher.Split(' ', ',')[0];
                if (pubFirst.Length >= 3 && n.StartsWith(pubFirst + " ", StringComparison.OrdinalIgnoreCase))
                {
                    string rest = n.Substring(pubFirst.Length).Trim();
                    if (rest.Length >= 4) keys.Add(rest);
                }
            }
            string dir = InstallDir(a);
            if (dir != null) { string dn = Path.GetFileName(dir); if (dn.Length >= 3) keys.Add(dn); }
            return keys.Where(k => !Protected.Contains(k) && Norm(k).Length >= 3).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Pasta de instalação: InstallLocation, ou a pasta do desinstalador/ícone dentro de Program Files.</summary>
        public static string InstallDir(InstalledApp a)
        {
            var cands = new List<string>();
            if (!string.IsNullOrEmpty(a.InstallLocation)) cands.Add(a.InstallLocation);
            try { if (!string.IsNullOrEmpty(a.UninstallString) && !a.Msi) cands.Add(Path.GetDirectoryName(SplitCommand(a.UninstallString).Item1)); } catch { }
            try { if (!string.IsNullOrEmpty(a.DisplayIcon)) cands.Add(Path.GetDirectoryName(a.DisplayIcon.Split(',')[0].Trim('"'))); } catch { }
            foreach (var c in cands)
                if (!string.IsNullOrEmpty(c) && SafeFolder(c)) return c.TrimEnd('\\');
            return null;
        }

        /// <summary>Uma pasta só pode ser oferecida se não for raiz, pasta do sistema ou pasta base de usuário.</summary>
        public static bool SafeFolder(string path)
        {
            try
            {
                string p = Path.GetFullPath(path).TrimEnd('\\');
                if (p.Length <= 3) return false;
                if (Protected.Contains(Path.GetFileName(p))) return false;
                var forbidden = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
                }.Where(f => !string.IsNullOrEmpty(f)).Select(f => f.TrimEnd('\\'));
                foreach (var f in forbidden)
                {
                    if (p.Equals(f, StringComparison.OrdinalIgnoreCase)) return false;
                    // nunca dentro do Windows
                }
                string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (p.StartsWith(win + "\\", StringComparison.OrdinalIgnoreCase)) return false;
                if (p.IndexOf(@"\Microsoft\Windows", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                return true;
            }
            catch { return false; }
        }

        private static readonly string[] RegProtected = { @"\Software\Microsoft", @"\Software\Classes", @"\Software\Policies", @"\Software\WOW6432Node\Microsoft", @"\Software\WOW6432Node\Classes", @"\Software\WOW6432Node\Policies" };

        private static bool SafeRegKey(string key)
        {
            var parts = key.Split('\\');
            if (parts.Length < 3) return false;           // ex.: HKCU\Software
            if (Protected.Contains(parts[parts.Length - 1])) return false;
            foreach (var p in RegProtected)
                if (key.IndexOf(p + "\\", StringComparison.OrdinalIgnoreCase) >= 0 || key.EndsWith(p, StringComparison.OrdinalIgnoreCase)) return false;
            return Regex.IsMatch(key, @"^HK(LM|CU)\\Software\\", RegexOptions.IgnoreCase);
        }

        public static List<Leftover> Scan(InstalledApp a)
        {
            var found = new List<Leftover>();
            var keys = NameKeys(a);
            var normKeys = keys.Select(Norm).Where(k => k.Length >= 3).Distinct().ToList();
            string dir = InstallDir(a);
            string pub = (a.Publisher ?? "").Split(',')[0].Trim();
            pub = Regex.Replace(pub, @"\b(Inc|LLC|Ltd|GmbH|Corp|Corporation|Co|S\.A\.|Limited|Software|Technologies)\b\.?", "", RegexOptions.IgnoreCase).Trim(' ', ',', '.');
            string pubNorm = Norm(pub);

            Action<LeftoverKind, Confidence, string, string, string> add = (kind, lvl, path, val, reason) =>
            {
                if (found.Any(f => f.Kind == kind && string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase) && f.ValueName == val)) return;
                // uma pasta/chave dentro de outra já listada é redundante
                if ((kind == LeftoverKind.Pasta || kind == LeftoverKind.Arquivo) && found.Any(f => f.Kind == LeftoverKind.Pasta && path.StartsWith(f.Path + "\\", StringComparison.OrdinalIgnoreCase))) return;
                if (kind == LeftoverKind.ChaveRegistro && found.Any(f => f.Kind == LeftoverKind.ChaveRegistro && path.StartsWith(f.Path + "\\", StringComparison.OrdinalIgnoreCase))) return;
                found.Add(new Leftover { Kind = kind, Level = lvl, Path = path, ValueName = val, Reason = reason });
            };

            // ---- Pastas ----
            if (dir != null && Directory.Exists(dir)) add(LeftoverKind.Pasta, Confidence.Seguro, dir, null, "Pasta de instalação");
            var bases = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"AppData\LocalLow"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                Environment.GetFolderPath(Environment.SpecialFolder.Programs), Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
            }.Where(b => !string.IsNullOrEmpty(b) && Directory.Exists(b)).Distinct(StringComparer.OrdinalIgnoreCase);

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var b in bases)
            {
                // Pastas em AppData guardam dados do usuário (perfis, saves, senhas): nunca vêm marcadas por padrão.
                bool userData = b.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase) && !b.EndsWith("Programs", StringComparison.OrdinalIgnoreCase);
                Confidence exact = userData ? Confidence.Moderado : Confidence.Seguro;
                string why = userData ? " — pode ter dados pessoais (perfis, saves, configurações)" : "";
                foreach (var d in SafeDirs(b))
                {
                    string dn = Norm(Path.GetFileName(d));
                    if (normKeys.Contains(dn)) add(LeftoverKind.Pasta, exact, d, null, "Pasta com o nome do programa" + why);
                    else if (pubNorm.Length >= 3 && (dn == pubNorm || dn == pubNorm + "corporation" || dn == pubNorm + "inc"))
                    {
                        // pasta do fabricante: examina as subpastas do produto; a do fabricante só se tiver apenas este produto
                        var subs = SafeDirs(d).ToList();
                        var mine = subs.Where(s => normKeys.Contains(Norm(Path.GetFileName(s)))).ToList();
                        foreach (var s in mine) add(LeftoverKind.Pasta, exact, s, null, "Pasta do produto dentro da pasta do fabricante" + why);
                        bool onlyMine = mine.Count > 0 && subs.Count == mine.Count && SafeFiles(d).Count == 0;
                        if (onlyMine && SafeFolder(d)) add(LeftoverKind.Pasta, Confidence.Moderado, d, null, "Pasta do fabricante (só tinha este programa)" + why);
                    }
                    else if (normKeys.Any(k => k.Length >= 5 && dn.Length >= 5 && (dn.StartsWith(k) || k.StartsWith(dn)))) add(LeftoverKind.Pasta, Confidence.Avancado, d, null, "Nome parecido com o do programa" + why);
                }
            }
            found.RemoveAll(f => f.Kind == LeftoverKind.Pasta && !SafeFolder(f.Path));

            // ---- Atalhos (.lnk) que apontam para a pasta de instalação ou têm o nome do programa ----
            var lnkDirs = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.Programs), Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar")
            }.Where(x => !string.IsNullOrEmpty(x) && Directory.Exists(x));
            foreach (var ld in lnkDirs)
            {
                string[] links;
                try { links = Directory.GetFiles(ld, "*.lnk", ld.EndsWith("Programs", StringComparison.OrdinalIgnoreCase) ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly); } catch { continue; }
                foreach (var l in links)
                {
                    string target = LinkTarget(l);
                    bool pointsInside = dir != null && target != null && target.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase);
                    bool sameName = normKeys.Contains(Norm(Path.GetFileNameWithoutExtension(l)));
                    bool broken = target != null && target.Length > 0 && !File.Exists(target) && !Directory.Exists(target);
                    if (pointsInside) add(LeftoverKind.Arquivo, Confidence.Seguro, l, null, "Atalho para a pasta do programa");
                    else if (sameName && broken) add(LeftoverKind.Arquivo, Confidence.Seguro, l, null, "Atalho quebrado com o nome do programa");
                    else if (sameName) add(LeftoverKind.Arquivo, Confidence.Moderado, l, null, "Atalho com o nome do programa");
                }
            }

            // ---- Registro ----
            if (StillInstalledKey(a.KeyPath)) add(LeftoverKind.ChaveRegistro, Confidence.Seguro, a.KeyPath, null, "Entrada da lista de programas instalados");
            foreach (var hive in new[] { @"HKCU\Software", @"HKLM\SOFTWARE", @"HKLM\SOFTWARE\WOW6432Node" })
            {
                foreach (var sub in Reg.SubKeys(hive))
                {
                    string kp = hive + "\\" + sub, sn = Norm(sub);
                    bool safe = SafeRegKey(kp);
                    if (safe && normKeys.Contains(sn)) add(LeftoverKind.ChaveRegistro, Confidence.Seguro, kp, null, "Configurações do programa no registro");
                    else if (pubNorm.Length >= 3 && sn == pubNorm && !sub.Equals("Microsoft", StringComparison.OrdinalIgnoreCase))
                    {
                        var subs = Reg.SubKeys(kp);
                        var mine = subs.Where(s => normKeys.Contains(Norm(s)) && SafeRegKey(kp + "\\" + s)).ToList();
                        foreach (var s in mine) add(LeftoverKind.ChaveRegistro, Confidence.Seguro, kp + "\\" + s, null, "Configurações do programa (dentro da chave do fabricante)");
                        if (safe && mine.Count > 0 && mine.Count == subs.Length && Reg.ValueNames(kp).Length == 0)
                            add(LeftoverKind.ChaveRegistro, Confidence.Moderado, kp, null, "Chave do fabricante (só tinha este programa)");
                    }
                    else if (safe && normKeys.Any(k => k.Length >= 5 && sn.Length >= 5 && (sn.StartsWith(k) || k.StartsWith(sn))))
                        add(LeftoverKind.ChaveRegistro, Confidence.Avancado, kp, null, "Nome parecido com o do programa");
                }
            }

            if (dir != null)
            {
                // Itens de inicialização apontando para a pasta
                foreach (var run in new[] { @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run" })
                    foreach (var v in Reg.ValueNames(run))
                        if (Str(run, v).IndexOf(dir, StringComparison.OrdinalIgnoreCase) >= 0) add(LeftoverKind.ValorRegistro, Confidence.Seguro, run, v, "Programa de inicialização");
                // App Paths
                foreach (var ap in new[] { @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths", @"HKCU\Software\Microsoft\Windows\CurrentVersion\App Paths" })
                    foreach (var s in Reg.SubKeys(ap))
                        if (Str(ap + "\\" + s, "").IndexOf(dir, StringComparison.OrdinalIgnoreCase) >= 0) add(LeftoverKind.ChaveRegistro, Confidence.Seguro, ap + "\\" + s, null, "Registro do executável (App Paths)");
                // Serviços
                const string svc = @"HKLM\SYSTEM\CurrentControlSet\Services";
                foreach (var s in Reg.SubKeys(svc))
                    if (Str(svc + "\\" + s, "ImagePath").IndexOf(dir, StringComparison.OrdinalIgnoreCase) >= 0) add(LeftoverKind.Servico, Confidence.Moderado, s, null, "Serviço do Windows instalado pelo programa");
                // Tarefas agendadas
                string tasks = Path.Combine(Environment.SystemDirectory, "Tasks");
                foreach (var f in SafeFilesRecursive(tasks))
                {
                    string xml;
                    try { xml = File.ReadAllText(f); } catch { continue; }
                    if (xml.IndexOf(dir, StringComparison.OrdinalIgnoreCase) >= 0)
                        add(LeftoverKind.Tarefa, Confidence.Moderado, f.Substring(tasks.Length), null, "Tarefa agendada que executa o programa");
                }
            }

            // Remove itens que já estão dentro de outra pasta/chave listada com confiança igual ou maior.
            found.RemoveAll(f => found.Any(p => p != f && p.Level <= f.Level &&
                ((p.Kind == LeftoverKind.Pasta && (f.Kind == LeftoverKind.Pasta || f.Kind == LeftoverKind.Arquivo)) || (p.Kind == LeftoverKind.ChaveRegistro && f.Kind == LeftoverKind.ChaveRegistro)) &&
                f.Path.StartsWith(p.Path + "\\", StringComparison.OrdinalIgnoreCase)));
            foreach (var f in found.Where(f => f.Kind == LeftoverKind.Pasta)) f.Bytes = DirSize(f.Path);
            return found.OrderBy(f => f.Level).ThenBy(f => f.Kind).ThenBy(f => f.Path).ToList();
        }

        private static bool StillInstalledKey(string kp)
        {
            string sub;
            using (var root = Reg.Root(kp, out sub))
            using (var k = root.OpenSubKey(sub)) return k != null;
        }

        private static IEnumerable<string> SafeDirs(string d)
        {
            try { return Directory.GetDirectories(d).Where(s => (new DirectoryInfo(s).Attributes & FileAttributes.ReparsePoint) == 0).ToList(); }
            catch { return new List<string>(); }
        }

        private static List<string> SafeFiles(string d) { try { return Directory.GetFiles(d).ToList(); } catch { return new List<string>(); } }

        private static IEnumerable<string> SafeFilesRecursive(string d)
        {
            var r = new List<string>();
            var st = new Stack<string>();
            if (Directory.Exists(d)) st.Push(d);
            while (st.Count > 0)
            {
                var x = st.Pop();
                r.AddRange(SafeFiles(x));
                foreach (var s in SafeDirs(x)) st.Push(s);
            }
            return r;
        }

        private static long DirSize(string d)
        {
            long s = 0;
            foreach (var f in SafeFilesRecursive(d)) { try { s += new FileInfo(f).Length; } catch { } }
            return s;
        }

        private static string LinkTarget(string lnk)
        {
            try
            {
                var t = Type.GetTypeFromProgID("WScript.Shell");
                object sh = Activator.CreateInstance(t);
                object sc = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
                string target = (string)sc.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, sc, null);
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(sc);
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(sh);
                return target;
            }
            catch { return null; }
        }

        // ===================== Remoção =====================
        public static string BackupDir(InstalledApp a)
        {
            string safe = Regex.Replace(a.Name, @"[^\w\-]+", "_");
            return Path.Combine(Store.Dir, "uninstall_backups", DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + "_" + safe);
        }

        /// <summary>Remove as sobras escolhidas. Pastas/arquivos → Lixeira; registro → exporta .reg antes; tarefas → exporta XML.</summary>
        public static Tuple<int, int, long> Remove(InstalledApp a, List<Leftover> items, string backup, List<string> errors)
        {
            Directory.CreateDirectory(backup);
            int ok = 0, fail = 0;
            long bytes = 0;
            int n = 0;
            foreach (var it in items.OrderByDescending(i => i.Path.Length))
            {
                n++;
                try
                {
                    switch (it.Kind)
                    {
                        case LeftoverKind.Pasta:
                            if (!SafeFolder(it.Path)) throw new Exception("pasta protegida");
                            if (Directory.Exists(it.Path))
                            {
                                long sz = it.Bytes;
                                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(it.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                                bytes += sz;
                            }
                            break;
                        case LeftoverKind.Arquivo:
                            if (File.Exists(it.Path)) Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(it.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                            break;
                        case LeftoverKind.ChaveRegistro:
                            if (!SafeRegKey(it.Path) && !it.Path.StartsWith(a.KeyPath, StringComparison.OrdinalIgnoreCase) && it.Path.IndexOf(@"\App Paths\", StringComparison.OrdinalIgnoreCase) < 0) throw new Exception("chave protegida");
                            ExportKey(it.Path, Path.Combine(backup, "reg_" + n + ".reg"));
                            DeleteKey(it.Path);
                            break;
                        case LeftoverKind.ValorRegistro:
                            ExportKey(it.Path, Path.Combine(backup, "reg_" + n + "_" + Regex.Replace(it.ValueName, @"[^\w]+", "_") + ".reg"));
                            Reg.Delete(it.Path, it.ValueName);
                            break;
                        case LeftoverKind.Servico:
                            ExportKey(@"HKLM\SYSTEM\CurrentControlSet\Services\" + it.Path, Path.Combine(backup, "servico_" + it.Path + ".reg"));
                            Shell.Run("sc", "stop \"" + it.Path + "\"");
                            Shell.Run("sc", "delete \"" + it.Path + "\"");
                            break;
                        case LeftoverKind.Tarefa:
                            string xml;
                            Shell.Run("schtasks", "/Query /TN \"" + it.Path + "\" /XML", out xml, 15000);
                            File.WriteAllText(Path.Combine(backup, "tarefa_" + n + ".xml"), xml ?? "");
                            Shell.Run("schtasks", "/Delete /TN \"" + it.Path + "\" /F");
                            break;
                    }
                    ok++;
                    Store.Log("SOBRA removida (" + it.Kind + "): " + it.Path + (it.ValueName != null ? " :: " + it.ValueName : ""));
                }
                catch (Exception ex)
                {
                    fail++;
                    errors.Add(it.Path + ": " + ex.Message);
                }
            }
            File.WriteAllText(Path.Combine(backup, "LEIA-ME.txt"),
                "Backup do VoltCore antes de remover as sobras de: " + a.Name + Environment.NewLine +
                "Para restaurar o registro, dê dois cliques nos arquivos .reg desta pasta." + Environment.NewLine +
                "Pastas e atalhos foram enviados para a Lixeira." + Environment.NewLine +
                "Tarefas: schtasks /Create /TN <nome> /XML tarefa_N.xml");
            return Tuple.Create(ok, fail, bytes);
        }

        private static void ExportKey(string key, string file)
        {
            string o;
            int code = Shell.Run(Path.Combine(Environment.SystemDirectory, "reg.exe"), "export \"" + key + "\" \"" + file + "\" /y /reg:64", out o, 30000);
            if (code != 0) throw new Exception("backup do registro falhou, nada foi apagado (" + (o ?? "").Trim() + ")");
        }

        private static void DeleteKey(string key)
        {
            string sub;
            using (var root = Reg.Root(key, out sub))
                root.DeleteSubKeyTree(sub, false);
        }
    }
}
