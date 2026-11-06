using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace VoltCore
{
    public class MemSnapshot
    {
        public double TotalMB, AvailMB, UsedMB, StandbyMB, ModifiedMB, FreeMB;
        public int LoadPct;
        public bool ListsOk;
    }

    /// <summary>
    /// Redução de memória: limpa a lista standby (cache), a lista modificada e os working sets,
    /// usando as mesmas APIs internas do Windows que o RAMMap/ISLC usam.
    /// </summary>
    public static class MemoryCleaner
    {
        private const int SystemMemoryListInformation = 80;
        private const int MemoryEmptyWorkingSets = 2;
        private const int MemoryFlushModifiedList = 3;
        private const int MemoryPurgeStandbyList = 4;
        private const int MemoryPurgeLowPriorityStandbyList = 5;
        private static bool _privs;

        private static void EnsurePrivileges()
        {
            if (_privs) return;
            Native.EnablePrivilege("SeProfileSingleProcessPrivilege");
            Native.EnablePrivilege("SeIncreaseQuotaPrivilege");
            Native.EnablePrivilege("SeDebugPrivilege");
            _privs = true;
        }

        public static MemSnapshot Snapshot()
        {
            var s = new MemSnapshot();
            var m = new Native.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX)) };
            Native.GlobalMemoryStatusEx(ref m);
            s.TotalMB = m.ullTotalPhys / 1048576.0;
            s.AvailMB = m.ullAvailPhys / 1048576.0;
            s.UsedMB = s.TotalMB - s.AvailMB;
            s.LoadPct = (int)m.dwMemoryLoad;

            EnsurePrivileges();
            IntPtr buf = Marshal.AllocHGlobal(512);
            try
            {
                int ret;
                if (Native.NtQuerySystemInformation(SystemMemoryListInformation, buf, 512, out ret) == 0)
                {
                    const double page = 4096.0 / 1048576.0;
                    long zero = Marshal.ReadInt64(buf, 0);
                    long free = Marshal.ReadInt64(buf, 8);
                    long modified = Marshal.ReadInt64(buf, 16);
                    long standby = 0;
                    for (int i = 0; i < 8; i++) standby += Marshal.ReadInt64(buf, 40 + i * 8);
                    s.FreeMB = (zero + free) * page;
                    s.ModifiedMB = modified * page;
                    s.StandbyMB = standby * page;
                    s.ListsOk = true;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return s;
        }

        private static bool Command(int cmd)
        {
            EnsurePrivileges();
            int c = cmd;
            return Native.NtSetSystemInformation(SystemMemoryListInformation, ref c, sizeof(int)) == 0;
        }

        public static bool PurgeStandby() { return Command(MemoryPurgeStandbyList); }
        public static bool PurgeLowPriorityStandby() { return Command(MemoryPurgeLowPriorityStandbyList); }
        public static bool FlushModified() { return Command(MemoryFlushModifiedList); }

        /// <summary>Esvazia o working set de todos os processos (exceto o jogo em primeiro plano, se informado).</summary>
        public static int TrimWorkingSets(ICollection<int> skipPids)
        {
            EnsurePrivileges();
            if (skipPids == null || skipPids.Count == 0)
            {
                if (Command(MemoryEmptyWorkingSets)) return -1;
            }
            int n = 0;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id <= 4 || (skipPids != null && skipPids.Contains(p.Id))) continue;
                    if (Native.EmptyWorkingSet(p.Handle)) n++;
                }
                catch { }
                finally { p.Dispose(); }
            }
            return n;
        }

        /// <summary>Limpeza rápida: lista modificada + standby. Seguro durante jogos.</summary>
        public static double QuickClean()
        {
            var before = Snapshot();
            FlushModified();
            PurgeStandby();
            var after = Snapshot();
            double freed = after.AvailMB - before.AvailMB;
            Store.Log("MEMÓRIA rápida: +" + freed.ToString("0") + " MB livres");
            return Math.Max(0, after.FreeMB - before.FreeMB);
        }

        /// <summary>Limpeza completa: working sets + modificada + standby.</summary>
        public static double DeepClean(ICollection<int> skipPids)
        {
            var before = Snapshot();
            TrimWorkingSets(skipPids);
            FlushModified();
            PurgeStandby();
            var after = Snapshot();
            Store.Log("MEMÓRIA completa: livre " + before.FreeMB.ToString("0") + " -> " + after.FreeMB.ToString("0") + " MB");
            return Math.Max(0, after.FreeMB - before.FreeMB);
        }

        public static List<Tuple<string, double>> TopProcesses(int n)
        {
            var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    double mb = p.WorkingSet64 / 1048576.0;
                    string name = p.ProcessName;
                    double cur;
                    map[name] = (map.TryGetValue(name, out cur) ? cur : 0) + mb;
                }
                catch { }
                finally { p.Dispose(); }
            }
            return map.OrderByDescending(kv => kv.Value).Take(n).Select(kv => Tuple.Create(kv.Key, kv.Value)).ToList();
        }
    }

    /// <summary>Uso de CPU total via GetSystemTimes (não depende de contadores localizados).</summary>
    public class CpuMeter
    {
        private long _idle, _kernel, _user;

        public CpuMeter() { Native.GetSystemTimes(out _idle, out _kernel, out _user); }

        public double Sample()
        {
            long i, k, u;
            Native.GetSystemTimes(out i, out k, out u);
            long di = i - _idle, dk = k - _kernel, du = u - _user;
            _idle = i; _kernel = k; _user = u;
            long total = dk + du;
            if (total <= 0) return 0;
            return Math.Max(0, Math.Min(100, (total - di) * 100.0 / total));
        }
    }
}
