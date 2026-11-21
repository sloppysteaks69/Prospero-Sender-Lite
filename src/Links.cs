// SPDX-License-Identifier: LGPL-2.1-or-later
// Prospero Sender Lite: which links reach the PS5 (ported from pw_bonded.py's
// auto_routes, link_name, lane_parts and separate_lanes).
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PSLite
{
    /// <summary>What a link has carried. Kept by the PS5's address on it, so it outlives a re-detect.</summary>
    class LinkStat
    {
        public long Bytes;          // file bytes landed on the PS5 over this link
        public long Wire;           // bytes put on the wire (smaller with LZ4)
        public int Connections;     // connections open right now
        public long Pieces;         // pieces answered
        public long SendTicks;      // Stopwatch ticks spent putting pieces on the wire (on the cable: waiting for it too)
        public long AnswerTicks;    // ... and waiting for the PS5's answer to each, which is its write
    }

    /// <summary>One link to the receiver: the PS5's address on it and the PC's.</summary>
    class Route
    {
        public string Remote;       // the PS5's address on this link
        public string Local;        // the PC's address on it (connections bind to it)
        public int Port = Proto.Port;
        public int Threads = 4;     // connections it gets
        public bool Lane;           // a USB adapter the receiver drives itself (not the PS5's own networking)
        public bool Wifi;           // the home network (Wi-Fi, or Ethernet through a router)
        public bool Fast;           // gets the large files: a Realtek lane or the USB bridge cable
        public int BigSlots = 1;    // large files it sends at once
        public int PieceBytes = Proto.ChunkSize;   // a large file's piece on this link
        public int Ahead = 1;       // pieces a connection keeps on their way at once (0.12: 2 on the fast links)
        public bool OnUsb2;         // an adapter on one of the PS5's USB 2.0 ports: 480 Mbit/s whatever its Ethernet says
        public bool Usb;            // the USB bridge cable (Remote is "usb", "usb2" or "usb3")
        public int UsbIndex;        // which cable: 0 the first
        public string Name = "";    // RTL8157, AX88179, PS5 Ethernet, Home network
        public string Kind = "";    // 5 GbE, 2.5 GbE, 1 GbE, Ethernet, Wi-Fi / LAN
        public string State = "";   // what the receiver says about its adapter
        public int Mbit;            // the link's line rate, 0 when not known
        public int Mtu;             // a Realtek lane's frame size on the PS5, 0 when the receiver doesn't say
        public int FrameAvg;        // the average frame the PS5 got on it, bytes (0: not said)
        public double EstimateMb;   // what Turbo measured this kind of link at, MB/s
        public bool Enabled = true; // "Use this device"
        public bool Up;             // answered the last check
        public LinkStat Stat = new LinkStat();
    }

    class Adapter
    {
        public string Address;
        public int Mbit;
        public int Mtu;
        public string Name;
        public string Mac = "";     // "aa:bb:cc:dd:ee:ff", "" when not known
    }

    static class Links
    {
        public const string DirectPs5 = "10.0.0.2";               // the PS5's own Ethernet port (receiver's address)
        public const string DirectLinkLocal = "169.254.77.4";
        public const string AxLinkLocal = "169.254.77.2";
        public const string RtlLinkLocal = "169.254.77.3";
        public static readonly string[] RtlLanes = { "10.0.2.2", "10.0.3.2", "10.0.4.2" };
        public static readonly string[] AxLanes = { "10.0.1.2", "10.0.5.2", "10.0.6.2" };
        public static readonly string[] RtlHomeChips = { "RTL8156", "RTL8157", "RTL8159" };
        public const int RtlMinMbit = 2500;                        // a PC port linked this fast is a Realtek lane's

        public static List<Adapter> Adapters()
        {
            List<Adapter> found = new List<Adapter>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    IPInterfaceProperties props;
                    try { props = ni.GetIPProperties(); }
                    catch (Exception) { continue; }
                    int mtu = 0;
                    try
                    {
                        IPv4InterfaceProperties v4 = props.GetIPv4Properties();
                        if (v4 != null) mtu = v4.Mtu;
                    }
                    catch (Exception) { }
                    long speed = 0;
                    try { speed = ni.Speed; } catch (Exception) { }
                    string mac = "";
                    try
                    {
                        byte[] raw = ni.GetPhysicalAddress().GetAddressBytes();
                        List<string> hex = new List<string>();
                        foreach (byte b in raw) hex.Add(b.ToString("x2"));
                        mac = string.Join(":", hex.ToArray());
                    }
                    catch (Exception) { }
                    foreach (UnicastIPAddressInformation ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        Adapter a = new Adapter();
                        a.Address = ua.Address.ToString();
                        a.Mbit = speed > 0 ? (int)(speed / 1000000) : 0;
                        a.Mtu = mtu;
                        a.Name = ni.Description;
                        a.Mac = mac;
                        found.Add(a);
                    }
                }
            }
            catch (Exception) { }
            return found;
        }

        static string Subnet(string address)
        {
            int dot = address.LastIndexOf('.');
            return dot < 0 ? address : address.Substring(0, dot + 1);
        }

        /// <summary>The address Windows would send to remote from.</summary>
        static string SourceFor(string remote)
        {
            try
            {
                using (Socket probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    probe.Connect(IPAddress.Parse(remote), 9);
                    return ((IPEndPoint)probe.LocalEndPoint).Address.ToString();
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The PC's address on remote's /24, which ties a connection to that link.</summary>
        public static string LocalFor(string remote, List<Adapter> adapters)
        {
            string net = Subnet(remote);
            foreach (Adapter a in adapters)
                if (a.Address.StartsWith(net)) return a.Address;
            string source = SourceFor(remote);
            return source != null && source.StartsWith(net) ? source : null;
        }

        /// <summary>The first of these PC addresses a connection to remote:port can be made from.</summary>
        static string AnswersFrom(List<string> locals, string remote, int port)
        {
            if (locals.Count == 0) return null;
            bool[] ok = new bool[locals.Count];
            Task[] tasks = new Task[locals.Count];
            for (int i = 0; i < locals.Count; i++)
            {
                int n = i;
                tasks[i] = Task.Factory.StartNew(delegate { ok[n] = Proto.Reachable(locals[n], remote, port, 1500); });
            }
            try { Task.WaitAll(tasks, 4000); } catch (Exception) { }
            for (int i = 0; i < locals.Count; i++)
                if (ok[i]) return locals[i];
            return null;
        }

        /// <summary>The receiver's status line by lane: "RTL#2" -> { chip, state }.
        /// Adapters it didn't find are left out.</summary>
        public static Dictionary<string, string[]> LaneParts(string status)
        {
            Dictionary<string, string[]> found = new Dictionary<string, string[]>();
            foreach (string raw in (status ?? "").Split(';'))
            {
                string part = raw.Trim();
                if (part.Length == 0 || part.EndsWith("not plugged in") || part.EndsWith("not built in") || part.EndsWith("unplugged"))
                    continue;
                int colon = part.IndexOf(':');
                if (colon < 0) continue;
                string name = part.Substring(0, colon).Trim();
                string state = part.Substring(colon + 1).Trim();
                int copy = 1;
                int hash = name.LastIndexOf(" #");
                if (hash >= 0)
                {
                    int number;
                    if (int.TryParse(name.Substring(hash + 2), out number)) copy = number;
                    name = name.Substring(0, hash);
                }
                string kind = name.StartsWith("RTL") ? "RTL" : name.StartsWith("AX") ? "AX" : name;
                name = name.Replace(" (UNTESTED on PS5)", "").Trim();
                found[kind + "#" + copy] = new string[] { name, state };
            }
            return found;
        }

        /// <summary>A number the receiver gives in a lane's state, e.g. @"MTU (\d+)"; 0 when absent.</summary>
        public static int StateNumber(string state, string pattern)
        {
            Match m = Regex.Match(state ?? "", pattern);
            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        }

        public static int LinkMbit(string state)
        {
            Match m = Regex.Match(state ?? "", @"(\d+) Mbit/s link");
            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        }

        static string SpeedKind(int mbit)
        {
            if (mbit >= 10000) return "10 GbE";
            if (mbit >= 5000) return "5 GbE";
            if (mbit >= 2500) return "2.5 GbE";
            if (mbit >= 1000) return "1 GbE";
            if (mbit > 0) return mbit + " Mbit";
            return "Ethernet";
        }

        /// <summary>What Turbo measured each kind of link at (pw_bonded.LINK_ESTIMATE_MB).</summary>
        static double Estimate(Route r)
        {
            if (r.Usb) return 180;
            if (r.OnUsb2) return 40;
            if (r.Name.StartsWith("RTL8159")) return 1000;
            if (r.Name.StartsWith("RTL8157")) return 590;
            if (r.Name.StartsWith("RTL8156")) return 290;
            if (r.Name.StartsWith("AX")) return 110;
            if (r.Wifi) return 15;
            return 110;                                   // the PS5's own Ethernet port
        }

        static int DefaultThreads(Route r, bool others)
        {
            // 0.8: 16 on a 5 / 10 GbE lane, 12 on 2.5 GbE, 8 on the bridge cable, 4 on a 1 Gbit link,
            // 2 on Wi-Fi. A link sends about half as many large files at once (BigSlots), so two
            // connections work each file: while the PS5 writes one piece the next is on its way.
            if (r.Usb) return 8;
            if (r.OnUsb2) return 3;          // a USB 2.0 port carries 480 Mbit/s at most
            if (r.Name.StartsWith("RTL8157") || r.Name.StartsWith("RTL8159")) return 16;
            if (r.Name.StartsWith("RTL8156")) return 12;
            if (r.Wifi) return others ? 2 : 4;
            return 4;
        }

        static Route Lane(string remote, string local, bool rtl, int copy, Dictionary<string, string[]> lanes, List<Adapter> adapters)
        {
            Route r = new Route();
            r.Remote = remote;
            r.Local = local;
            r.Lane = true;
            string[] part;
            lanes.TryGetValue((rtl ? "RTL#" : "AX#") + copy, out part);
            string chip = part != null ? part[0] : (rtl ? RtlHomeChips[Math.Min(copy, 3) - 1] : "AX88179");
            bool home = !rtl || chip.StartsWith(RtlHomeChips[Math.Min(copy, 3) - 1]);
            r.Name = chip + (copy > 1 && (!rtl || !home) ? " #" + copy : "");
            r.State = part != null ? part[1] : "";
            r.Mbit = LinkMbit(r.State);
            r.Mtu = StateNumber(r.State, @"MTU (\d+)");
            r.FrameAvg = StateNumber(r.State, @"frames average (\d+) bytes");
            if (r.Mbit == 0)
                foreach (Adapter a in adapters)
                    if (a.Address == local) r.Mbit = a.Mbit;
            r.Kind = SpeedKind(r.Mbit);
            // the receiver says which bus the adapter is on: "adapter on USB 2.0"
            if (r.State.IndexOf("on USB 2", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                r.OnUsb2 = true;
                r.Mbit = r.Mbit > 0 ? Math.Min(r.Mbit, 480) : 480;
                r.Kind = "480 Mbit (USB 2.0)";
            }
            return r;
        }

        /// <summary>Every link the PC has to the PS5 at host: the receiver's USB adapter lanes
        /// (Realtek, AX88179), the PS5's own Ethernet port when it is cabled to the PC, and the
        /// home network. status is the receiver's status line ("" when it isn't known).</summary>
        public static List<Route> Detect(string host, string status)
        {
            return Detect(host, status, null);
        }

        /// <summary>trace, when given, gets a line for every PC port and every check made: what
        /// the log shows when a link that should be there isn't.</summary>
        public static List<Route> Detect(string host, string status, List<string> trace)
        {
            if (trace == null) trace = new List<string>();
            List<Adapter> adapters = Adapters();
            Dictionary<string, string[]> lanes = LaneParts(status);
            List<Route> routes = new List<Route>();
            foreach (Adapter a in adapters)
                trace.Add("PC port " + a.Address + ": " + (a.Mbit > 0 ? a.Mbit + " Mbit/s" : "speed unknown") + ", MTU " + a.Mtu + ", " + a.Name);

            string direct = LocalFor(DirectPs5, adapters);
            if (direct != null && host != DirectPs5)
            {
                Route r = new Route();
                r.Remote = DirectPs5; r.Local = direct; r.Name = "PS5 Ethernet";
                routes.Add(r);
            }
            // PC ports on 169.254.x.x (no DHCP answered yet): the fast ones are a Realtek
            // lane's, the others the AX88179's or the PS5's own port
            List<string> linkLocals = new List<string>();
            foreach (Adapter a in adapters)
                if (a.Address.StartsWith("169.254.")) linkLocals.Add(a.Address);
            if (linkLocals.Count == 0)
            {
                // Windows doesn't always list a port's 169.254 address: ask which it would send from
                string source = SourceFor(DirectLinkLocal);
                if (source != null && source.StartsWith("169.254.")) linkLocals.Add(source);
            }
            if (direct != null) trace.Add("PS5 Ethernet: the PC is on its 10.0.0.x link at " + direct);
            else if (linkLocals.Count == 0) trace.Add("PS5 Ethernet: not looked for, because no PC port has a 169.254.x.x or 10.0.0.x address (is the cable in, and that PC port on automatic addressing?)");
            if (direct == null && linkLocals.Count > 0)
            {
                string stock = AnswersFrom(linkLocals, DirectLinkLocal, Proto.Port);
                trace.Add("PS5 Ethernet (" + DirectLinkLocal + "): " + (stock != null ? "answers from the PC's " + stock : "no answer from the PC's " + string.Join(", ", linkLocals.ToArray())));
                if (stock != null)
                {
                    Route r = new Route();
                    r.Remote = DirectLinkLocal; r.Local = stock; r.Name = "PS5 Ethernet";
                    routes.Add(r);
                    linkLocals.Remove(stock);
                }
            }
            List<string> fast = new List<string>(), slow = new List<string>();
            foreach (string address in linkLocals)
            {
                int mbit = 0;
                foreach (Adapter a in adapters)
                    if (a.Address == address) mbit = a.Mbit;
                if (mbit >= RtlMinMbit) fast.Add(address); else slow.Add(address);
            }
            for (int pass = 0; pass < 2; pass++)
            {
                bool rtl = pass == 0;
                string[] set = rtl ? RtlLanes : AxLanes;
                for (int n = 0; n < set.Length; n++)
                {
                    string local = LocalFor(set[n], adapters);
                    if (local != null && host != set[n])
                    {
                        routes.Add(Lane(set[n], local, rtl, n + 1, lanes, adapters));
                    }
                    else if (n == 0 && local == null)
                    {
                        string lane = rtl ? RtlLinkLocal : AxLinkLocal;
                        string found = AnswersFrom(rtl ? fast : slow, lane, Proto.Port);
                        if (found != null) routes.Add(Lane(lane, found, rtl, 1, lanes, adapters));
                    }
                }
            }
            Route homeRoute = new Route();
            homeRoute.Remote = host;
            homeRoute.Local = LocalFor(host, adapters);
            homeRoute.Wifi = true;
            homeRoute.Name = "Home network";
            routes.Add(homeRoute);

            // USB bridge cables on this PC, up to the three the receiver drives: they lead the list
            int cables = Math.Min(UsbPipe.Plugged(), 3);
            trace.Add("USB bridge cables plugged into the PC: " + cables);
            for (int i = cables - 1; i >= 0; i--)
            {
                Route cable = new Route();
                cable.Usb = true;
                cable.UsbIndex = i;
                cable.Lane = true;
                cable.Remote = i == 0 ? "usb" : "usb" + (i + 1);
                cable.Name = "USB bridge" + (i > 0 ? " " + (i + 1) : "");
                cable.Kind = "USB cable";
                routes.Insert(0, cable);
            }
            foreach (Route r in routes)
                trace.Add("link " + r.Name + ": PS5 " + r.Remote + " from the PC's " + (r.Local ?? (r.Usb ? "USB port" : "default route")));
            foreach (Route r in routes)
            {
                if (r.Wifi)
                {
                    // the PC port's line rate says nothing about this link: the PS5's end is
                    // its Wi-Fi (or its Ethernet through the router), so no rate is shown
                    r.Kind = "Wi-Fi / LAN";
                }
                else if (!r.Lane)
                {
                    foreach (Adapter a in adapters)
                        if (a.Address == r.Local) r.Mbit = a.Mbit;
                    r.Kind = SpeedKind(r.Mbit);
                }
                r.Threads = DefaultThreads(r, routes.Count > 1);
                r.EstimateMb = Estimate(r);
                // large files a link sends at once, about one per 300 MB/s (Turbo 6.09's numbers)
                r.Fast = r.Usb || (r.Lane && r.Name.StartsWith("RTL") && !r.OnUsb2);
                bool fiveGig = r.Name.StartsWith("RTL8157") || r.Name.StartsWith("RTL8159");
                r.BigSlots = r.Usb ? 2 : fiveGig ? 8 : r.Name.StartsWith("RTL8156") ? 4 : 1;
                // Turbo sends a folder of files in its own 4-8 MB pieces; piece_mb 32 is only for one huge file.
                // 0.11: 8 MB pieces on the fast lanes. With 32 MB and 16 MB pieces the receiver held about 770 MB of
                // pieces at once and the PS5 kernel stalled it ("FMEM allocation timeout") each time it passed about 1.2 GB.
                // 0.14: 12 MB on the 5 GbE lane (0.13 had 16 MB, which brought the memory stalls back; 8 MB stays on the 2.5 GbE lane).
                r.PieceBytes = fiveGig ? 12 * 1024 * 1024 : (!r.Usb && r.Name.StartsWith("RTL8156")) ? 8 * 1024 * 1024 : Proto.ChunkSize;
                // 0.12: the 5 GbE and 2.5 GbE lanes and the USB bridge cable send a second piece while the PS5
                // writes the first, so the link isn't idle during the write (Turbo's sliding window, at 2).
                r.Ahead = (r.Usb || ((fiveGig || r.Name.StartsWith("RTL8156")) && !r.OnUsb2)) ? 2 : 1;
            }
            return routes;
        }

        /// <summary>Marks each route Up or not, all asked at once.</summary>
        public static void Check(List<Route> routes)
        {
            Task[] tasks = new Task[routes.Count];
            for (int i = 0; i < routes.Count; i++)
            {
                Route r = routes[i];
                tasks[i] = Task.Factory.StartNew(delegate
                {
                    if (!r.Usb)
                    {
                        r.Up = Proto.Reachable(r.Local, r.Remote, r.Port, 2000);
                        return;
                    }
                    try
                    {
                        // the receiver answers on the cable only while it is reading it
                        r.State = UsbPipe.Ping(r.UsbIndex).Ends();
                        r.Up = true;
                    }
                    catch (Exception e)
                    {
                        r.Up = false;
                        r.State = e.Message;
                    }
                });
            }
            try { Task.WaitAll(tasks, 6000); } catch (Exception) { }
        }

        /// <summary>The links a send uses. File data goes over one kind of link: the USB adapter
        /// lanes when one answers, else the PS5's own networking. Both kinds busy at once has
        /// powered the PS5 off (Turbo), so the PS5's Ethernet and Wi-Fi join the lanes
        /// only when their boxes are ticked.</summary>
        public static List<Route> Choose(List<Route> routes, bool ethernetToo, bool wifiToo)
        {
            List<Route> lanes = new List<Route>(), own = new List<Route>(), extra = new List<Route>();
            foreach (Route r in routes)
            {
                if (!r.Enabled || !r.Up) continue;
                if (r.Lane) lanes.Add(r);
                else
                {
                    own.Add(r);
                    if (r.Wifi ? wifiToo : ethernetToo) extra.Add(r);
                }
            }
            if (lanes.Count == 0) return own;
            lanes.AddRange(extra);
            return lanes;
        }
    }
}
