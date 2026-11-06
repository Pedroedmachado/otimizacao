using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;

namespace VoltCore
{
    public class HwInfo
    {
        public string Cpu = "?", Gpu = "?", Board = "?", Os = "?", RamText = "?", GpuDriver = "";
        public int Cores, Threads, CpuMhz, RamSticks, RamSpeed, RamConfigured, RamType;
        public double RamGB, GpuVramGB;
        public List<string> Disks = new List<string>();
        public bool SystemOnSsd = true, SystemDiskKnown;
    }

    public class CheckItem
    {
        public bool Ok;
        public string Title, Detail;
        public string FixLabel;      // texto do botão de correção
        public Func<string> Fix;     // executa a correção e devolve a mensagem
        public string GoTo;          // ou leva a uma aba
    }

    public static class Hardware
    {
        private static IEnumerable<ManagementObject> Q(string query, string scope = @"root\cimv2")
        {
            var list = new List<ManagementObject>();
            try
            {
                using (var s = new ManagementObjectSearcher(scope, query))
                    foreach (ManagementObject o in s.Get()) list.Add(o);
            }
            catch { }
            return list;
        }

        private static string S(ManagementObject o, string p) { try { var v = o[p]; return v == null ? "" : v.ToString().Trim(); } catch { return ""; } }
        private static long L(ManagementObject o, string p) { long v; return long.TryParse(S(o, p), out v) ? v : 0; }

        public static HwInfo Read()
        {
            var h = new HwInfo();
            var cpu = Q("SELECT Name,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed FROM Win32_Processor").FirstOrDefault();
            if (cpu != null)
            {
                h.Cpu = S(cpu, "Name");
                h.Cores = (int)L(cpu, "NumberOfCores");
                h.Threads = (int)L(cpu, "NumberOfLogicalProcessors");
                h.CpuMhz = (int)L(cpu, "MaxClockSpeed");
            }

            var gpus = Q("SELECT Name,DriverVersion,AdapterRAM FROM Win32_VideoController").ToList();
            var real = gpus.Where(g => !S(g, "Name").Contains("Basic") && !S(g, "Name").Contains("Virtual") && !S(g, "Name").Contains("Parsec")).ToList();
            var gpu = real.FirstOrDefault(g => S(g, "Name").IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0 || S(g, "Name").IndexOf("Radeon RX", StringComparison.OrdinalIgnoreCase) >= 0 || S(g, "Name").IndexOf("Arc", StringComparison.OrdinalIgnoreCase) >= 0) ?? real.FirstOrDefault();
            if (gpu != null)
            {
                h.Gpu = S(gpu, "Name");
                h.GpuDriver = S(gpu, "DriverVersion");
                h.GpuVramGB = VramFromRegistry(h.Gpu);
                if (h.GpuVramGB <= 0) h.GpuVramGB = L(gpu, "AdapterRAM") / 1073741824.0;
            }

            var sticks = Q("SELECT Capacity,Speed,ConfiguredClockSpeed,SMBIOSMemoryType FROM Win32_PhysicalMemory").ToList();
            h.RamSticks = sticks.Count;
            h.RamGB = sticks.Sum(s => L(s, "Capacity")) / 1073741824.0;
            if (sticks.Count > 0)
            {
                h.RamSpeed = (int)sticks.Max(s => L(s, "Speed"));
                h.RamConfigured = (int)sticks.Max(s => L(s, "ConfiguredClockSpeed"));
                h.RamType = (int)L(sticks[0], "SMBIOSMemoryType");
            }
            if (h.RamGB <= 0) h.RamGB = MemoryCleaner.Snapshot().TotalMB / 1024.0;
            string type = h.RamType == 34 ? "DDR5" : h.RamType == 26 ? "DDR4" : h.RamType == 24 ? "DDR3" : "RAM";
            h.RamText = h.RamGB.ToString("0") + " GB " + type + (h.RamConfigured > 0 ? " @ " + h.RamConfigured + " MT/s" : "") + " (" + h.RamSticks + (h.RamSticks == 1 ? " pente)" : " pentes)");

            var board = Q("SELECT Manufacturer,Product FROM Win32_BaseBoard").FirstOrDefault();
            if (board != null) h.Board = (S(board, "Manufacturer") + " " + S(board, "Product")).Trim();

            var os = Q("SELECT Caption,BuildNumber FROM Win32_OperatingSystem").FirstOrDefault();
            if (os != null) h.Os = S(os, "Caption").Replace("Microsoft ", "") + " (build " + S(os, "BuildNumber") + ")";

            // Discos físicos com tipo de mídia (SSD/HDD/NVMe)
            char sysLetter = Path.GetPathRoot(Environment.SystemDirectory)[0];
            string sysDiskNumber = null;
            foreach (var p in Q("SELECT DiskNumber,DriveLetter FROM MSFT_Partition", @"root\Microsoft\Windows\Storage"))
                if (S(p, "DriveLetter").Length > 0 && char.ToUpperInvariant(S(p, "DriveLetter")[0]) == char.ToUpperInvariant(sysLetter)) sysDiskNumber = S(p, "DiskNumber");
            foreach (var d in Q("SELECT DeviceId,FriendlyName,MediaType,BusType,Size FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage"))
            {
                long media = L(d, "MediaType"), bus = L(d, "BusType");
                string kind = bus == 17 ? "NVMe" : media == 4 ? "SSD" : media == 3 ? "HDD" : bus == 7 ? "USB" : "Disco";
                h.Disks.Add(S(d, "FriendlyName") + " — " + kind + " " + (L(d, "Size") / 1e9).ToString("0") + " GB");
                if (S(d, "DeviceId") == sysDiskNumber)
                {
                    h.SystemDiskKnown = true;
                    h.SystemOnSsd = media != 3;
                }
            }
            return h;
        }

        private static double VramFromRegistry(string gpuName)
        {
            const string cls = @"HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
            try
            {
                foreach (var sub in Reg.SubKeys(cls))
                {
                    string k = cls + "\\" + sub;
                    var desc = Convert.ToString(Reg.Get(k, "DriverDesc"));
                    if (desc != gpuName) continue;
                    var v = Reg.Get(k, "HardwareInformation.qwMemorySize");
                    if (v is long) return (long)v / 1073741824.0;
                    if (v is byte[] && ((byte[])v).Length >= 8) return BitConverter.ToInt64((byte[])v, 0) / 1073741824.0;
                }
            }
            catch { }
            return 0;
        }

        /// <summary>Check-up: pontos que mais afetam o desempenho em jogos.</summary>
        public static List<CheckItem> Checkup(HwInfo h)
        {
            var r = new List<CheckItem>();
            if (h.RamSticks > 0)
                r.Add(new CheckItem { Ok = h.RamSticks >= 2, Title = "Memória em dual channel", Detail = h.RamSticks >= 2 ? "Você tem " + h.RamSticks + " pentes de memória." : "Só 1 pente de memória: o PC roda em single channel e pode perder 10–30% de FPS em jogos que dependem de CPU. Use 2 pentes iguais." });

            if (h.RamConfigured > 0 && (h.RamType == 26 || h.RamType == 34))
            {
                bool slow = (h.RamType == 26 && h.RamConfigured <= 2666) || (h.RamType == 34 && h.RamConfigured <= 4800);
                bool belowRated = h.RamSpeed > 0 && h.RamConfigured < h.RamSpeed;
                r.Add(new CheckItem { Ok = !slow, Title = "Velocidade da memória (XMP/EXPO)", Detail = slow ? "A memória está a " + h.RamConfigured + " MT/s, velocidade padrão. Ative o perfil XMP (Intel) ou EXPO (AMD) na BIOS para ganhar desempenho." + (belowRated ? " O pente suporta " + h.RamSpeed + " MT/s." : "") : "Rodando a " + h.RamConfigured + " MT/s." });
            }

            if (h.RamGB > 0)
                r.Add(new CheckItem { Ok = h.RamGB >= 15, Title = "Quantidade de memória", Detail = h.RamGB >= 15 ? h.RamGB.ToString("0") + " GB é suficiente para jogos atuais." : "Com " + h.RamGB.ToString("0") + " GB, jogos atuais podem travar. 16 GB é o recomendado; use a Limpeza de Memória automática enquanto isso." });

            if (h.SystemDiskKnown)
                r.Add(new CheckItem { Ok = h.SystemOnSsd, Title = "Windows instalado em SSD", Detail = h.SystemOnSsd ? "O sistema está num SSD." : "O Windows está num HD mecânico. Trocar por um SSD é a melhoria mais perceptível que existe." });

            var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory));
            double freePct = drive.TotalFreeSpace * 100.0 / drive.TotalSize;
            r.Add(new CheckItem { Ok = freePct >= 15, Title = "Espaço livre no disco do sistema", Detail = freePct.ToString("0") + "% livre (" + Fmt.Bytes(drive.TotalFreeSpace) + "). " + (freePct >= 15 ? "" : "Abaixo de 15% o Windows fica lento; use a Limpeza.") });

            foreach (var d in Displays.List())
            {
                var disp = d;
                bool ok = d.Hz >= d.MaxHz;
                r.Add(new CheckItem
                {
                    Ok = ok,
                    Title = "Taxa de atualização — " + d.Name,
                    Detail = ok ? d.Width + "×" + d.Height + " a " + d.Hz + " Hz (máximo do monitor)." : "Rodando a " + d.Hz + " Hz, mas o monitor suporta " + d.MaxHz + " Hz! É o maior ganho de fluidez e input lag possível. Confira também se o cabo é DisplayPort/HDMI 2.x.",
                    FixLabel = ok ? null : "Usar " + d.MaxHz + " Hz",
                    Fix = ok ? (Func<string>)null : () => Displays.SetMaxRefresh(disp) ? "Monitor agora a " + disp.MaxHz + " Hz." : "Não foi possível mudar; ajuste em Configurações > Vídeo > Vídeo avançado."
                });
            }

            var rebar = Platform.ResizableBar();
            if (rebar.HasValue)
                r.Add(new CheckItem { Ok = rebar.Value, Title = "Resizable BAR", Detail = rebar.Value ? "Ligado: a CPU acessa toda a memória da placa de vídeo." : "Desligado. Ative 'Above 4G Decoding' e 'Re-Size BAR Support' na BIOS (exige CSM desligado). Ganho de até 10% em vários jogos." });

            foreach (var n in Nic.Connected().Where(x => x.Wired))
            {
                bool fast = n.SpeedBps > 100000000;
                bool capable = Regex.IsMatch(n.Description ?? "", @"2\.5G|5G|10G|Gigabit|GbE|I2[0-9]{2}|Killer|AQtion|Ethernet Controller", RegexOptions.IgnoreCase);
                if (!capable) continue;
                r.Add(new CheckItem
                {
                    Ok = fast,
                    Title = "Velocidade do cabo de rede",
                    Detail = n.Description + " conectada a " + (n.SpeedBps >= 1000000000 ? (n.SpeedBps / 1e9).ToString("0.#") + " Gbps" : (n.SpeedBps / 1e6).ToString("0") + " Mbps") + "." +
                             (fast ? "" : " A placa suporta mais: quase sempre é cabo ruim (Cat5 velho/danificado), mau contato ou porta do roteador. Troque por um cabo Cat6.")
                });
            }

            var drops = LinkWatcher.History().Where(e => e.Time > DateTime.Now.AddHours(-24)).ToList();
            if (drops.Count > 0)
                r.Add(new CheckItem
                {
                    Ok = false,
                    Title = "Cabo de rede instável",
                    Detail = drops.Count + " trocas de velocidade/quedas nas últimas 24 h (última às " + drops.Last().Time.ToString("HH:mm") + ": " + drops.Last().From + " → " + drops.Last().To + "). Cada troca derruba a internet por alguns segundos. Troque o cabo ou a porta do roteador e acompanhe o histórico abaixo."
                });

            bool hpet = Platform.HpetForced();
            r.Add(new CheckItem
            {
                Ok = !hpet,
                Title = "Timer do sistema (HPET)",
                Detail = hpet ? "O HPET está forçado (useplatformclock), o que aumenta a latência. Remova para voltar ao padrão." : "Padrão do Windows (sem HPET forçado).",
                FixLabel = hpet ? "Remover" : null,
                Fix = hpet ? (Func<string>)(() => Platform.RemoveHpet() ? "HPET removido. Reinicie o PC." : "Não foi possível alterar o bcdedit.") : null
            });

            bool hvci = Platform.HvciRunning();
            r.Add(new CheckItem
            {
                Ok = !hvci,
                Title = "Integridade de Memória (HVCI)",
                Detail = hvci ? "Ligada: mais segurança, mas custa de 5 a 10% de FPS em alguns jogos. Você pode desligar em Desempenho > Segurança x desempenho." : "Desligada.",
                FixLabel = hvci ? "Ver opção" : null,
                GoTo = hvci ? "PagePerf" : null
            });

            bool ultimate = PowerPlan.IsUltimateActive();
            string plan = PowerPlan.ActiveName();
            bool highPerf = ultimate || plan.IndexOf("alto", StringComparison.OrdinalIgnoreCase) >= 0 || plan.IndexOf("high", StringComparison.OrdinalIgnoreCase) >= 0 || plan.IndexOf("máximo", StringComparison.OrdinalIgnoreCase) >= 0 || plan.IndexOf("ultimate", StringComparison.OrdinalIgnoreCase) >= 0;
            r.Add(new CheckItem { Ok = highPerf, Title = "Plano de energia", Detail = "Ativo: " + plan + (highPerf ? "." : ". Use o plano Desempenho Máximo (aba Desempenho).") });

            var hags = Reg.Get(@"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode");
            r.Add(new CheckItem { Ok = hags != null && Convert.ToInt32(hags) == 2, Title = "Agendamento de GPU por hardware", Detail = hags != null && Convert.ToInt32(hags) == 2 ? "Ligado." : "Desligado. Ligue na aba Desempenho (exige reiniciar)." });

            var dvr = Reg.Get(@"HKCU\System\GameConfigStore", "GameDVR_Enabled");
            r.Add(new CheckItem { Ok = dvr != null && Convert.ToInt32(dvr) == 0, Title = "Gravação em segundo plano", Detail = dvr != null && Convert.ToInt32(dvr) == 0 ? "Desligada." : "A Game Bar pode estar gravando o jogo o tempo todo." });

            int startup = StartupManager.List().Count(i => i.Enabled);
            r.Add(new CheckItem { Ok = startup <= 8, Title = "Programas na inicialização", Detail = startup + " programas abrem junto com o Windows." + (startup > 8 ? " Desative os que não precisa na aba Windows." : "") });

            if (!string.IsNullOrEmpty(h.GpuDriver))
                r.Add(new CheckItem { Ok = true, Title = "Driver de vídeo", Detail = h.Gpu + " — driver " + h.GpuDriver + ". Mantenha-o atualizado pelo app da NVIDIA/AMD/Intel." });
            return r;
        }
    }

    /// <summary>Nota do PC de 0 a 100.</summary>
    public static class Score
    {
        public static int Compute(List<CheckItem> checks)
        {
            var perf = Catalog.All.Where(t => t.InBoost).ToList();
            double tweakPart = perf.Count == 0 ? 1 : perf.Count(TweakEngine.IsApplied) / (double)perf.Count;
            var debloat = Catalog.Page("debloat").Where(t => t.Level == Level.Essencial).ToList();
            double debloatPart = debloat.Count == 0 ? 1 : debloat.Count(TweakEngine.IsApplied) / (double)debloat.Count;
            double checkPart = checks == null || checks.Count == 0 ? 0.7 : checks.Count(c => c.Ok) / (double)checks.Count;
            var mem = MemoryCleaner.Snapshot();
            double memPart = mem.LoadPct < 60 ? 1 : mem.LoadPct < 80 ? 0.6 : 0.2;
            double s = 45 * tweakPart + 20 * debloatPart + 25 * checkPart + 10 * memPart;
            return (int)Math.Round(Math.Max(1, Math.Min(100, s)));
        }
    }
}
