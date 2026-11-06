using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace VoltCore
{
    // ======================= MONITORES =======================
    public class DisplayInfo
    {
        public string Device, Name;
        public int Width, Height, Hz, MaxHz;
    }

    public static class Displays
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE dd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE dm, IntPtr hwnd, uint flags, IntPtr lParam);

        private static DEVMODE NewMode()
        {
            var m = new DEVMODE();
            m.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            return m;
        }

        public static List<DisplayInfo> List()
        {
            var r = new List<DisplayInfo>();
            for (uint i = 0; i < 16; i++)
            {
                var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
                if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
                if ((dd.StateFlags & 1) == 0) continue; // não está no desktop
                var cur = NewMode();
                if (!EnumDisplaySettings(dd.DeviceName, -1, ref cur)) continue;
                int max = cur.dmDisplayFrequency;
                var m = NewMode();
                for (int k = 0; EnumDisplaySettings(dd.DeviceName, k, ref m); k++)
                {
                    if (m.dmPelsWidth == cur.dmPelsWidth && m.dmPelsHeight == cur.dmPelsHeight && m.dmBitsPerPel == cur.dmBitsPerPel && m.dmDisplayFrequency > max)
                        max = m.dmDisplayFrequency;
                    m = NewMode();
                }
                var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
                string name = EnumDisplayDevices(dd.DeviceName, 0, ref mon, 0) ? mon.DeviceString : dd.DeviceString;
                if (string.IsNullOrEmpty(name) || name.IndexOf("Generic", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Genérico", StringComparison.OrdinalIgnoreCase) >= 0)
                    name = "monitor " + (r.Count + 1) + " (" + cur.dmPelsWidth + "×" + cur.dmPelsHeight + ")";
                r.Add(new DisplayInfo { Device = dd.DeviceName, Name = name, Width = cur.dmPelsWidth, Height = cur.dmPelsHeight, Hz = cur.dmDisplayFrequency, MaxHz = max });
            }
            return r;
        }

        /// <summary>Coloca o monitor na maior taxa de atualização disponível na resolução atual.</summary>
        public static bool SetMaxRefresh(DisplayInfo d)
        {
            var cur = NewMode();
            if (!EnumDisplaySettings(d.Device, -1, ref cur)) return false;
            cur.dmDisplayFrequency = d.MaxHz;
            cur.dmFields = 0x80000 | 0x100000 | 0x400000; // largura, altura, frequência
            bool ok = ChangeDisplaySettingsEx(d.Device, ref cur, IntPtr.Zero, 0x1, IntPtr.Zero) == 0;
            Store.Log("MONITOR " + d.Name + " -> " + d.MaxHz + " Hz: " + (ok ? "ok" : "falhou"));
            return ok;
        }

        /// <summary>Limite de FPS recomendado com G-Sync/FreeSync (mesma fórmula usada pelo Reflex).</summary>
        public static int FpsCap(int hz)
        {
            return (int)Math.Floor(hz - hz * hz / 3600.0);
        }
    }

    // ======================= ENERGIA USB / PCIe =======================
    public static class PowerSettings
    {
        public const string SubUsb = "2a737441-1930-4402-8d77-b2bebba308a3";
        public const string UsbSuspend = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";
        public const string SubPcie = "501a4d13-42af-4429-9fd1-a8218c268e20";
        public const string Aspm = "ee12f906-d277-404b-b6da-e5fa1a576df5";

        /// <summary>Retorna (AC, DC) do plano atual, ou null.</summary>
        public static int[] Get(string sub, string setting)
        {
            string o;
            Shell.Run("powercfg", "/q scheme_current " + sub + " " + setting, out o, 15000);
            var ms = Regex.Matches(o ?? "", "0x([0-9a-fA-F]{8})");
            if (ms.Count < 2) return null;
            return new[] { Convert.ToInt32(ms[ms.Count - 2].Groups[1].Value, 16), Convert.ToInt32(ms[ms.Count - 1].Groups[1].Value, 16) };
        }

        public static void Set(string sub, string setting, int ac, int dc)
        {
            Shell.Run("powercfg", "/setacvalueindex scheme_current " + sub + " " + setting + " " + ac);
            Shell.Run("powercfg", "/setdcvalueindex scheme_current " + sub + " " + setting + " " + dc);
            Shell.Run("powercfg", "/setactive scheme_current");
        }

        public static bool IsOff()
        {
            var u = Get(SubUsb, UsbSuspend);
            var p = Get(SubPcie, Aspm);
            return u != null && p != null && u[0] == 0 && p[0] == 0;
        }

        public static void Apply()
        {
            var u = Get(SubUsb, UsbSuspend);
            var p = Get(SubPcie, Aspm);
            if (u != null && Settings.Get("prev_usb", "").Length == 0) Settings.Set("prev_usb", u[0] + "," + u[1]);
            if (p != null && Settings.Get("prev_aspm", "").Length == 0) Settings.Set("prev_aspm", p[0] + "," + p[1]);
            Set(SubUsb, UsbSuspend, 0, u != null ? u[1] : 1);
            Set(SubPcie, Aspm, 0, p != null ? p[1] : 2);
        }

        public static void Undo()
        {
            var u = Settings.Get("prev_usb", "1,1").Split(',');
            var p = Settings.Get("prev_aspm", "0,2").Split(',');
            Set(SubUsb, UsbSuspend, int.Parse(u[0]), int.Parse(u[1]));
            Set(SubPcie, Aspm, int.Parse(p[0]), int.Parse(p[1]));
            Settings.Set("prev_usb", "");
            Settings.Set("prev_aspm", "");
        }
    }

    // ======================= REDE =======================
    public class NicInfo
    {
        public string Name, Description;
        public long SpeedBps;
        public bool Wired;
    }

    public static class Nic
    {
        // Palavras-chave padrão do Windows/drivers para economia de energia e moderação de interrupções.
        private const string Keywords = "'*EEE','AdvancedEEE','EEELinkAdvertisement','EnableGreenEthernet','GreenEthernet','PowerSavingMode','ULPMode','*InterruptModeration'";

        public static string ApplyScript()
        {
            return @"$f = Join-Path $env:ProgramData 'VoltCore\nic_backup.csv'
$kw = @(" + Keywords + @")
$rows = @()
foreach ($a in Get-NetAdapter -Physical | Where-Object Status -eq 'Up') {
  foreach ($p in Get-NetAdapterAdvancedProperty -Name $a.Name -ErrorAction SilentlyContinue | Where-Object { $kw -contains $_.RegistryKeyword }) {
    $rows += [pscustomobject]@{ Adapter = $a.Name; Keyword = $p.RegistryKeyword; Value = ($p.RegistryValue -join ' ') }
  }
  $pm = Get-NetAdapterPowerManagement -Name $a.Name -ErrorAction SilentlyContinue
  if ($pm) { $rows += [pscustomobject]@{ Adapter = $a.Name; Keyword = '__PM__'; Value = [string]$pm.AllowComputerToTurnOffDevice } }
}
if (-not (Test-Path $f) -and $rows.Count -gt 0) { $rows | Export-Csv $f -NoTypeInformation -Encoding UTF8 }
foreach ($r in $rows) {
  if ($r.Keyword -eq '__PM__') { Set-NetAdapterPowerManagement -Name $r.Adapter -AllowComputerToTurnOffDevice Disabled -NoRestart -ErrorAction SilentlyContinue }
  else { Set-NetAdapterAdvancedProperty -Name $r.Adapter -RegistryKeyword $r.Keyword -RegistryValue 0 -NoRestart -ErrorAction SilentlyContinue }
}
Get-NetAdapter -Physical | Where-Object Status -eq 'Up' | Restart-NetAdapter -Confirm:$false -ErrorAction SilentlyContinue";
        }

        public static string UndoScript()
        {
            return @"$f = Join-Path $env:ProgramData 'VoltCore\nic_backup.csv'
if (Test-Path $f) {
  foreach ($r in Import-Csv $f) {
    if ($r.Keyword -eq '__PM__') { Set-NetAdapterPowerManagement -Name $r.Adapter -AllowComputerToTurnOffDevice $r.Value -NoRestart -ErrorAction SilentlyContinue }
    else { Set-NetAdapterAdvancedProperty -Name $r.Adapter -RegistryKeyword $r.Keyword -RegistryValue $r.Value -NoRestart -ErrorAction SilentlyContinue }
  }
  Remove-Item $f -Force
  Get-NetAdapter -Physical | Where-Object Status -eq 'Up' | Restart-NetAdapter -Confirm:$false -ErrorAction SilentlyContinue
}";
        }

        public static List<NicInfo> Connected()
        {
            var r = new List<NicInfo>();
            try
            {
                using (var s = new ManagementObjectSearcher(@"root\StandardCimv2", "SELECT Name,InterfaceDescription,Speed,MediaConnectState,ConnectorPresent,NdisPhysicalMedium FROM MSFT_NetAdapter"))
                    foreach (ManagementObject o in s.Get())
                    {
                        if (Convert.ToInt32(o["MediaConnectState"] ?? 0) != 1 || !(o["ConnectorPresent"] is bool && (bool)o["ConnectorPresent"])) continue;
                        r.Add(new NicInfo
                        {
                            Name = Convert.ToString(o["Name"]),
                            Description = Convert.ToString(o["InterfaceDescription"]),
                            SpeedBps = Convert.ToInt64(o["Speed"] ?? 0L),
                            Wired = Convert.ToInt32(o["NdisPhysicalMedium"] ?? 0) == 14
                        });
                    }
            }
            catch { }
            return r;
        }
    }

    // ======================= DEFENDER =======================
    public static class DefenderExclusions
    {
        private static readonly Regex BinDir = new Regex(@"^(bin|bin32|bin64|binaries|win32|win64|x64|x86|retail|game|client)$", RegexOptions.IgnoreCase);

        /// <summary>Pasta raiz do jogo (sobe de bin/Binaries/Win64 até a pasta de instalação).</summary>
        public static string GameRoot(string exe)
        {
            string p = exe.Replace('/', '\\');
            var m = Regex.Match(p, @"^(.*\\steamapps\\common\\[^\\]+)", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value;
            m = Regex.Match(p, @"^(.*\\Epic Games\\[^\\]+)", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value;
            var dir = new DirectoryInfo(Path.GetDirectoryName(p));
            for (int i = 0; i < 3 && dir.Parent != null && BinDir.IsMatch(dir.Name); i++) dir = dir.Parent;
            return dir.FullName;
        }

        public static string Add(IEnumerable<string> folders)
        {
            var list = folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (list.Count == 0) return "";
            var o = Shell.Ps("try { Add-MpPreference -ExclusionPath @(" + string.Join(",", list.Select(Shell.Q)) + ") -ErrorAction Stop; 'OK' } catch { 'ERRO: ' + $_.Exception.Message }", 60000).Trim();
            Store.Log("DEFENDER exclusões +" + list.Count + ": " + o);
            return o.StartsWith("OK") ? "" : o;
        }

        public static void Remove(IEnumerable<string> folders)
        {
            var list = folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (list.Count == 0) return;
            Shell.Ps("Remove-MpPreference -ExclusionPath @(" + string.Join(",", list.Select(Shell.Q)) + ") -ErrorAction SilentlyContinue", 60000);
            Store.Log("DEFENDER exclusões removidas: " + list.Count);
        }
    }

    // ======================= PLATAFORMA =======================
    public static class Platform
    {
        /// <summary>HPET forçado via bcdedit (useplatformclock) piora a latência na maioria dos PCs.</summary>
        public static bool HpetForced()
        {
            string o;
            Shell.Run("bcdedit", "/enum {current}", out o, 15000);
            var m = Regex.Match(o ?? "", @"useplatformclock\s+(\S+)", RegexOptions.IgnoreCase);
            return m.Success && !Regex.IsMatch(m.Groups[1].Value, "^(No|Não|Nao)$", RegexOptions.IgnoreCase);
        }

        public static bool RemoveHpet()
        {
            return Shell.Run("bcdedit", "/deletevalue useplatformclock") == 0;
        }

        /// <summary>Integridade de Memória (HVCI) em execução.</summary>
        public static bool HvciRunning()
        {
            try
            {
                using (var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\DeviceGuard", "SELECT SecurityServicesRunning FROM Win32_DeviceGuard"))
                    foreach (ManagementObject o in s.Get())
                    {
                        var arr = o["SecurityServicesRunning"] as int[];
                        if (arr == null)
                        {
                            var u = o["SecurityServicesRunning"] as uint[];
                            if (u != null) arr = u.Select(x => (int)x).ToArray();
                        }
                        if (arr != null && arr.Contains(2)) return true;
                    }
            }
            catch { }
            return false;
        }

        /// <summary>Resizable BAR (só NVIDIA, via nvidia-smi): null = desconhecido.</summary>
        public static bool? ResizableBar()
        {
            string smi = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
            if (!File.Exists(smi)) return null;
            string o;
            if (Shell.Run(smi, "-q -d MEMORY", out o, 8000) != 0) return null;
            var fb = Regex.Match(o, @"FB Memory Usage\s+Total\s*:\s*(\d+)\s*MiB", RegexOptions.IgnoreCase);
            var bar = Regex.Match(o, @"BAR1 Memory Usage\s+Total\s*:\s*(\d+)\s*MiB", RegexOptions.IgnoreCase);
            if (!fb.Success || !bar.Success) return null;
            return long.Parse(bar.Groups[1].Value) >= long.Parse(fb.Groups[1].Value) * 0.9;
        }
    }
}
