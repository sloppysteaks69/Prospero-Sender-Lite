// SPDX-License-Identifier: LGPL-2.1-or-later
// Prospero Sender Lite: Configure dongles (ported from pw_bonded.py, Turbo 5.93).
// Sets up the PC's ports on the Realtek 2.5 / 5 / 10 GbE lanes for a fast send:
//   - Windows' PC-wide task offload (LSO) off: with it on the Realtek USB driver cuts
//     jumbo frames into ~2 KB ones whatever the Jumbo setting says
//   - Jumbo Packet on each port to match its lane's frame size on the PS5
//   - Energy-Efficient Ethernet and green / power saving off, LSO off on the port
//   - a new MAC address where a port shares one with the PS5's dongle or another port
//   - the ports restarted
// One Windows permission prompt; a report afterwards.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PSLite
{
    class DonglePort
    {
        public string Ip, Name, Lane;
        public string Mac = "", Why = "";
        public int Frame, Jumbo, WasMtu;
    }

    class DonglePlan
    {
        public List<DonglePort> Ports = new List<DonglePort>();
        public List<string> Problems = new List<string>();
    }

    static class Dongles
    {
        public const int SplitFrames = 3000;     // frames averaging less on a jumbo lane: the PC is sending small ones

        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        static extern int SendARP(uint destIp, uint srcIp, byte[] mac, ref uint length);

        static uint Ip(string address)
        {
            byte[] b = IPAddress.Parse(address).GetAddressBytes();
            return (uint)(b[0] | b[1] << 8 | b[2] << 16 | b[3] << 24);
        }

        /// <summary>The MAC the PS5's adapter answers ARP with ("" when it doesn't).</summary>
        static string Ps5Mac(string remote, string local)
        {
            try
            {
                byte[] mac = new byte[8];
                uint length = 8;
                if (SendARP(Ip(remote), Ip(local), mac, ref length) != 0) return "";
                List<string> hex = new List<string>();
                for (int i = 0; i < length && i < 8; i++) hex.Add(mac[i].ToString("x2"));
                return string.Join(":", hex.ToArray());
            }
            catch (Exception)
            {
                return "";
            }
        }

        static string NewMac()
        {
            byte[] b = new byte[5];
            new Random().NextBytes(b);
            StringBuilder s = new StringBuilder("02");      // locally administered
            foreach (byte x in b) s.Append(x.ToString("X2"));
            return s.ToString();
        }

        /// <summary>Windows' PC-wide task offload switch: 1 on, 0 off, -1 unknown.</summary>
        static int TaskOffload()
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo("powershell.exe", "-NoProfile -Command \"(Get-NetOffloadGlobalSetting).TaskOffload\"");
                info.UseShellExecute = false;
                info.RedirectStandardOutput = true;
                info.CreateNoWindow = true;
                using (Process p = Process.Start(info))
                {
                    string text = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit(15000);
                    return text == "Enabled" ? 1 : text == "Disabled" ? 0 : -1;
                }
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>The PC's ports on the Realtek lanes and what is wrong with them. status is the
        /// receiver's status line, which says each lane's frame size on the PS5.</summary>
        public static DonglePlan Plan(string status)
        {
            DonglePlan plan = new DonglePlan();
            List<Adapter> every = Links.Adapters();
            Dictionary<string, string[]> lanes = Links.LaneParts(status);
            Dictionary<string, List<string>> macs = new Dictionary<string, List<string>>();
            foreach (Adapter a in every)
            {
                if (a.Mac.Length == 0 || a.Mac == "00:00:00:00:00:00") continue;
                if (!macs.ContainsKey(a.Mac)) macs[a.Mac] = new List<string>();
                macs[a.Mac].Add(a.Address);
            }
            foreach (Adapter a in every)
            {
                int copy = a.Address.StartsWith("10.0.2.") ? 1 : a.Address.StartsWith("10.0.3.") ? 2 : a.Address.StartsWith("10.0.4.") ? 3 : 0;
                bool linkLocal = copy == 0 && a.Address.StartsWith("169.254.") && a.Mbit >= Links.RtlMinMbit;
                if (copy == 0 && !linkLocal) continue;
                if (linkLocal) copy = 1;
                string remote = linkLocal ? Links.RtlLinkLocal : Links.RtlLanes[copy - 1];
                string[] part;
                lanes.TryGetValue("RTL#" + copy, out part);
                string chip = part != null ? part[0] : Links.RtlHomeChips[copy - 1];
                int frame = part != null ? Links.StateNumber(part[1], @"MTU (\d+)") : 0;
                int average = part != null ? Links.StateNumber(part[1], @"frames average (\d+) bytes") : 0;
                if (frame == 0) frame = chip.StartsWith("RTL8157") || chip.StartsWith("RTL8159") ? 16000 : 9000;

                DonglePort port = new DonglePort();
                port.Ip = a.Address;
                port.Name = string.IsNullOrEmpty(a.Name) ? a.Address : a.Name;
                port.Lane = chip;
                port.Frame = frame;
                port.Jumbo = frame > 1500 ? frame + 14 : 1514;     // Windows counts the 14-byte Ethernet header
                port.WasMtu = a.Mtu;
                if (a.Mtu > 0 && a.Mtu < frame)
                    plan.Problems.Add(port.Name + ": Jumbo is " + (a.Mtu > 1500 ? a.Mtu + 14 : 1514) + " bytes, the " + chip + " lane's frame size is " + frame);
                if (average > 0 && frame > 1500 && average < SplitFrames)
                    plan.Problems.Add(chip + ": the PS5 is getting ~" + average + "-byte frames from the PC, not " + frame);
                List<string> twins = new List<string>();
                if (a.Mac.Length > 0 && macs.ContainsKey(a.Mac))
                    foreach (string other in macs[a.Mac])
                        if (other != a.Address) twins.Add(other);
                string far = a.Mac.Length > 0 ? Ps5Mac(remote, a.Address) : "";
                if (far.Length > 0 && far == a.Mac)
                {
                    port.Mac = NewMac();
                    port.Why = "the PS5's dongle";
                }
                else if (twins.Count > 0)
                {
                    port.Mac = NewMac();
                    port.Why = "the PC's port on " + twins[0];
                }
                if (port.Mac.Length > 0)
                    plan.Problems.Add(port.Name + ": its MAC address (" + a.Mac + ") is the same as " + port.Why + "'s");
                plan.Ports.Add(port);
            }
            if (plan.Ports.Count > 0 && TaskOffload() == 1)
                plan.Problems.Insert(0, "Windows' task offload (LSO) is on: the Realtek driver then sends ~2 KB frames whatever the Jumbo setting");
            return plan;
        }

        public static string ReportPath
        {
            get { return Path.Combine(Path.GetTempPath(), "psl-configure-dongles.txt"); }
        }

        /// <summary>Runs the changes as administrator (one Windows prompt). True when it started;
        /// the report then appears at ReportPath.</summary>
        public static bool Run(DonglePlan plan)
        {
            List<string> rows = new List<string>();
            foreach (DonglePort port in plan.Ports)
            {
                IPAddress.Parse(port.Ip);                                         // only an address goes into the script
                string mac = Regex.IsMatch(port.Mac ?? "", "^[0-9A-F]{12}$") ? port.Mac : "";
                string why = Regex.Replace(port.Why ?? "", "[^A-Za-z0-9 .-]", "");
                rows.Add("@{ip='" + port.Ip + "'; jumbo=" + port.Jumbo + "; mac='" + mac + "'; why='" + why + "'}");
            }
            string script = Script.Replace("__PORTS__", string.Join(", ", rows.ToArray())).Replace("__REPORT__", ReportPath.Replace("'", "''"));
            string path = Path.Combine(Path.GetTempPath(), "psl-configure-dongles.ps1");
            try { File.Delete(ReportPath); } catch (Exception) { }
            File.WriteAllText(path, script, new UTF8Encoding(true));
            try
            {
                ProcessStartInfo info = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + path + "\"");
                info.UseShellExecute = true;
                info.Verb = "runas";
                info.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(info);
                return true;
            }
            catch (Exception)
            {
                return false;                                                     // the permission prompt was declined
            }
        }

        // Turbo's DONGLE_PS, unchanged
        const string Script = @"
$ErrorActionPreference = 'Continue'
$r = New-Object System.Collections.Generic.List[string]
try {
    $was = (Get-NetOffloadGlobalSetting).TaskOffload
    Set-NetOffloadGlobalSetting -TaskOffload Disabled -ErrorAction Stop
    $r.Add(""PC-wide task offload (LSO): was $was, now Disabled"")
} catch { $r.Add(""PC-wide task offload: FAILED: $($_.Exception.Message)"") }
foreach ($p in @(__PORTS__)) {
    $a = (Get-NetIPAddress -IPAddress $p.ip -ErrorAction SilentlyContinue | Select-Object -First 1).InterfaceAlias
    if (-not $a) { $r.Add(""$($p.ip): no port has this address now""); continue }
    $props = @(Get-NetAdapterAdvancedProperty -Name $a -ErrorAction SilentlyContinue)
    $j = $props | Where-Object { $_.RegistryKeyword -eq '*JumboPacket' } | Select-Object -First 1
    if ($j) {
        $vals = @($j.ValidRegistryValues | Where-Object { $_ -match '^\d+$' } | ForEach-Object { [int]$_ } | Sort-Object)
        $want = $p.jumbo
        if ($vals.Count) {
            $want = $vals | Where-Object { $_ -ge $p.jumbo } | Select-Object -First 1
            if (-not $want) { $want = $vals[-1] }
        }
        try {
            Set-NetAdapterAdvancedProperty -Name $a -RegistryKeyword '*JumboPacket' -RegistryValue $want -NoRestart -ErrorAction Stop
            $r.Add(""${a}: Jumbo Packet $want (was $($j.RegistryValue))"")
        } catch { $r.Add(""${a}: Jumbo Packet FAILED: $($_.Exception.Message)"") }
    } else { $r.Add(""${a}: has no Jumbo Packet setting"") }
    foreach ($q in $props | Where-Object { $_.DisplayName -match 'EEE|Energy.Efficient|Green|Power Sav|Idle Power' }) {
        $off = @($q.ValidDisplayValues) | Where-Object { $_ -match '^(Disabled|Disable|Off)$' } | Select-Object -First 1
        if ($off -and $q.DisplayValue -ne $off) {
            try {
                Set-NetAdapterAdvancedProperty -Name $a -DisplayName $q.DisplayName -DisplayValue $off -NoRestart -ErrorAction Stop
                $r.Add(""${a}: $($q.DisplayName) off"")
            } catch { $r.Add(""${a}: $($q.DisplayName) FAILED: $($_.Exception.Message)"") }
        }
    }
    Disable-NetAdapterLso -Name $a -IPv4 -IPv6 -NoRestart -ErrorAction SilentlyContinue
    if ($p.mac) {
        try {
            Set-NetAdapterAdvancedProperty -Name $a -RegistryKeyword 'NetworkAddress' -RegistryValue $p.mac -NoRestart -ErrorAction Stop
            $r.Add(""${a}: new MAC address $($p.mac) (it shared one with $($p.why))"")
        } catch { $r.Add(""${a}: new MAC address FAILED: $($_.Exception.Message)"") }
    }
    try { Restart-NetAdapter -Name $a -Confirm:$false -ErrorAction Stop; $r.Add(""${a}: restarted"") }
    catch { $r.Add(""${a}: restart FAILED: $($_.Exception.Message)"") }
}
$r | Out-File -Encoding utf8 -FilePath '__REPORT__'
";
    }
}
