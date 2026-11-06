using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace VoltCore
{
    public enum Level { Essencial = 0, Recomendado = 1, Avancado = 2 }

    public class RegOp
    {
        public string Path;          // ex.: HKLM\SOFTWARE\...
        public string Name;
        public RegistryValueKind Kind;
        public object Value;         // null = apagar o valor
    }

    /// <summary>Um ajuste reversível. Valores originais do registro são guardados antes de aplicar.</summary>
    public class Tweak
    {
        public string Id, Page, Group, Title, Desc;
        public Level Level;
        public bool NeedsReboot;
        public bool InBoost;                         // entra no Boost de 1 clique
        public List<RegOp> Ops = new List<RegOp>();
        public List<string> ApplyPs = new List<string>();
        public List<string> UndoPs = new List<string>();
        public string UndoNote;                      // explicação quando o desfazer é diferente
        public Func<bool> Check;                     // verificação customizada de "aplicado"
        public Action CustomApply, CustomUndo;

        public Tweak Reg(string path, string name, int v) { Ops.Add(new RegOp { Path = path, Name = name, Kind = RegistryValueKind.DWord, Value = v }); return this; }
        public Tweak RegS(string path, string name, string v) { Ops.Add(new RegOp { Path = path, Name = name, Kind = RegistryValueKind.String, Value = v }); return this; }
        public Tweak RegDel(string path, string name) { Ops.Add(new RegOp { Path = path, Name = name, Kind = RegistryValueKind.DWord, Value = null }); return this; }
        public Tweak Ps(string apply, string undo) { if (apply != null) ApplyPs.Add(apply); if (undo != null) UndoPs.Add(undo); return this; }
        public Tweak Reboot() { NeedsReboot = true; return this; }
        public Tweak Boost() { InBoost = true; return this; }
        public Tweak Checked(Func<bool> f) { Check = f; return this; }
        public Tweak Note(string n) { UndoNote = n; return this; }
    }

    public static class Reg
    {
        public static RegistryKey Root(string full, out string sub)
        {
            int i = full.IndexOf('\\');
            string hive = full.Substring(0, i).ToUpperInvariant();
            sub = full.Substring(i + 1);
            RegistryHive h;
            switch (hive)
            {
                case "HKLM": h = RegistryHive.LocalMachine; break;
                case "HKCU": h = RegistryHive.CurrentUser; break;
                case "HKCR": h = RegistryHive.ClassesRoot; break;
                case "HKU": h = RegistryHive.Users; break;
                default: throw new ArgumentException("Hive inválida: " + hive);
            }
            return RegistryKey.OpenBaseKey(h, RegistryView.Registry64);
        }

        public static object Get(string path, string name)
        {
            string sub;
            using (var root = Root(path, out sub))
            using (var k = root.OpenSubKey(sub))
            {
                return k == null ? null : k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            }
        }

        public static RegistryValueKind Kind(string path, string name)
        {
            string sub;
            using (var root = Root(path, out sub))
            using (var k = root.OpenSubKey(sub))
            {
                try { return k == null ? RegistryValueKind.Unknown : k.GetValueKind(name); }
                catch { return RegistryValueKind.Unknown; }
            }
        }

        public static void Set(string path, string name, object value, RegistryValueKind kind)
        {
            string sub;
            using (var root = Root(path, out sub))
            using (var k = root.CreateSubKey(sub))
            {
                k.SetValue(name, value, kind);
            }
        }

        public static void Delete(string path, string name)
        {
            string sub;
            using (var root = Root(path, out sub))
            using (var k = root.OpenSubKey(sub, true))
            {
                if (k != null) k.DeleteValue(name, false);
            }
        }

        public static string[] SubKeys(string path)
        {
            string sub;
            using (var root = Root(path, out sub))
            using (var k = root.OpenSubKey(sub))
            {
                return k == null ? new string[0] : k.GetSubKeyNames();
            }
        }

        public static string[] ValueNames(string path)
        {
            string sub;
            using (var root = Root(path, out sub))
            using (var k = root.OpenSubKey(sub))
            {
                return k == null ? new string[0] : k.GetValueNames();
            }
        }

        public static bool Matches(RegOp op)
        {
            object cur;
            try { cur = Get(op.Path, op.Name); } catch { return false; }
            if (op.Value == null) return cur == null;
            if (cur == null) return false;
            if (op.Kind == RegistryValueKind.DWord)
            {
                try { return unchecked((int)Convert.ToInt64(cur)) == (int)op.Value; } catch { return false; }
            }
            return string.Equals(Convert.ToString(cur), Convert.ToString(op.Value), StringComparison.OrdinalIgnoreCase);
        }

        public static string Encode(object v, RegistryValueKind k)
        {
            switch (k)
            {
                case RegistryValueKind.DWord: return Convert.ToInt32(v).ToString();
                case RegistryValueKind.QWord: return Convert.ToInt64(v).ToString();
                case RegistryValueKind.MultiString: return Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join("\0", (string[])v)));
                case RegistryValueKind.Binary: return Convert.ToBase64String((byte[])v);
                default: return Convert.ToBase64String(Encoding.UTF8.GetBytes(Convert.ToString(v)));
            }
        }

        public static object Decode(string s, RegistryValueKind k)
        {
            switch (k)
            {
                case RegistryValueKind.DWord: return int.Parse(s);
                case RegistryValueKind.QWord: return long.Parse(s);
                case RegistryValueKind.MultiString: return Encoding.UTF8.GetString(Convert.FromBase64String(s)).Split('\0');
                case RegistryValueKind.Binary: return Convert.FromBase64String(s);
                default: return Encoding.UTF8.GetString(Convert.FromBase64String(s));
            }
        }
    }

    /// <summary>Aplica e desfaz ajustes, com backup dos valores originais.</summary>
    public static class TweakEngine
    {
        private const string BackupFile = "backup.tsv";
        private const string StateFile = "applied.txt";
        private static readonly object Lock = new object();
        private static HashSet<string> _installedAppx;

        public static HashSet<string> Applied()
        {
            return new HashSet<string>(Store.ReadLines(StateFile).Where(l => l.Length > 0));
        }

        private static void MarkApplied(string id, bool on)
        {
            lock (Lock)
            {
                var s = Applied();
                if (on) s.Add(id); else s.Remove(id);
                Store.WriteLines(StateFile, s);
            }
        }

        /// <summary>Lista de pacotes AppX instalados (cache) para saber o que já foi removido.</summary>
        public static HashSet<string> InstalledAppx
        {
            get
            {
                if (_installedAppx == null) RefreshAppx();
                return _installedAppx;
            }
        }

        public static void RefreshAppx()
        {
            var o = Shell.Ps("Get-AppxPackage -AllUsers | ForEach-Object { $_.Name }", 120000);
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in o.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) set.Add(l.Trim());
            _installedAppx = set;
        }

        public static bool AnyAppxLike(string pattern)
        {
            var rx = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return InstalledAppx.Any(n => rx.IsMatch(n));
        }

        public static bool IsApplied(Tweak t)
        {
            try
            {
                if (t.Check != null) return t.Check();
                if (t.Ops.Count > 0) return t.Ops.All(Reg.Matches);
                return Applied().Contains(t.Id);
            }
            catch { return false; }
        }

        public static string Apply(Tweak t)
        {
            var errors = new StringBuilder();
            lock (Lock)
            {
                var backup = Store.ReadLines(BackupFile);
                var have = new HashSet<string>(backup.Select(l => string.Join("\t", l.Split('\t').Take(3))));
                foreach (var op in t.Ops)
                {
                    string key = t.Id + "\t" + op.Path + "\t" + op.Name;
                    if (!have.Contains(key))
                    {
                        object cur = null;
                        RegistryValueKind kind = RegistryValueKind.Unknown;
                        try { cur = Reg.Get(op.Path, op.Name); kind = Reg.Kind(op.Path, op.Name); } catch { }
                        if (cur == null) backup.Add(key + "\t0\t\t");
                        else backup.Add(key + "\t1\t" + kind + "\t" + Reg.Encode(cur, kind));
                        have.Add(key);
                    }
                }
                Store.WriteLines(BackupFile, backup);
            }

            foreach (var op in t.Ops)
            {
                try
                {
                    if (op.Value == null) Reg.Delete(op.Path, op.Name);
                    else Reg.Set(op.Path, op.Name, op.Value, op.Kind);
                }
                catch (Exception ex) { errors.AppendLine(op.Path + "\\" + op.Name + ": " + ex.Message); }
            }
            foreach (var ps in t.ApplyPs) Shell.Ps(ps);
            if (t.CustomApply != null)
            {
                try { t.CustomApply(); } catch (Exception ex) { errors.AppendLine(ex.Message); }
            }
            MarkApplied(t.Id, true);
            Store.Log("APLICADO  " + t.Id + (errors.Length > 0 ? "  (com avisos)" : ""));
            return errors.ToString();
        }

        public static string Undo(Tweak t)
        {
            var errors = new StringBuilder();
            lock (Lock)
            {
                var backup = Store.ReadLines(BackupFile);
                var keep = new List<string>();
                foreach (var line in backup)
                {
                    var p = line.Split('\t');
                    if (p.Length < 6 || p[0] != t.Id) { keep.Add(line); continue; }
                    try
                    {
                        if (p[3] == "0") Reg.Delete(p[1], p[2]);
                        else
                        {
                            var kind = (RegistryValueKind)Enum.Parse(typeof(RegistryValueKind), p[4]);
                            Reg.Set(p[1], p[2], Reg.Decode(p[5], kind), kind);
                        }
                    }
                    catch (Exception ex) { errors.AppendLine(p[1] + "\\" + p[2] + ": " + ex.Message); keep.Add(line); }
                }
                Store.WriteLines(BackupFile, keep);
            }
            foreach (var ps in t.UndoPs) Shell.Ps(ps);
            if (t.CustomUndo != null)
            {
                try { t.CustomUndo(); } catch (Exception ex) { errors.AppendLine(ex.Message); }
            }
            MarkApplied(t.Id, false);
            Store.Log("DESFEITO  " + t.Id);
            return errors.ToString();
        }
    }
}
