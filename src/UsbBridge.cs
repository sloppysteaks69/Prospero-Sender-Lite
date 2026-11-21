// SPDX-License-Identifier: LGPL-2.1-or-later
// Prospero Sender Lite: the USB bridge cable (ported from pw_usblink.py).
// A USB host-to-host bridge cable (Prolific PL27A1, USB 3; or PL25A1, USB 2.0) from the
// PC to the PS5, where the receiver's usb_link.c answers. The cable is one ordered byte
// stream each way: a whole packet is written as one USB transfer, and its one-byte
// answer (1 done, 0 failed) comes back in the same order. A transfer whose length is a
// multiple of 512 gets one pad byte, so the PS5's read, which returns at a short USB
// packet, never waits for more.
//
// Turbo reached the cable through libusb. Lite talks to Windows' own WinUSB directly, so
// nothing extra ships with the exe. The cable still needs the WinUSB driver once: in
// Zadig, Options > List All Devices, pick "USB-To-USB Cable Bridge", choose WinUSB and
// press Replace Driver.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace PSLite
{
    class UsbLinkException : IOException
    {
        public UsbLinkException(string message) : base(message) { }
    }

    class UsbPipe
    {
        public const string DriverHint = "give the cable the WinUSB driver once: in Zadig, Options > List All Devices, pick \"USB-To-USB Cable Bridge\", choose WinUSB and press Replace Driver";
        const string Vendor = "vid_067b";
        static readonly string[] Products = { "pid_27a1", "pid_25a1" };
        const int PadUnit = 512;                 // a multiple of every USB bulk packet size
        const int AnswerTimeoutMs = 60000;
        public const int PingTimeoutMs = 3000;

        // ---- Windows: SetupAPI, WinUSB ----
        static Guid UsbDeviceInterface = new Guid("A5DCBF10-6530-11D2-901F-00C04FB951ED");
        const uint DigcfPresent = 0x02, DigcfDeviceInterface = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        struct DeviceInterfaceData
        {
            public int Size;
            public Guid InterfaceClass;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct InterfaceDescriptor
        {
            public byte Length, DescriptorType, InterfaceNumber, AlternateSetting, NumEndpoints, InterfaceClass, InterfaceSubClass, InterfaceProtocol, Interface;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PipeInformation
        {
            public int PipeType;                 // 2: bulk
            public byte PipeId;
            public ushort MaximumPacketSize;
            public byte Interval;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct SetupPacket
        {
            public byte RequestType, Request;
            public ushort Value, Index, Length;
        }

        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr deviceInfo, ref Guid interfaceClass, uint index, ref DeviceInterfaceData data);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref DeviceInterfaceData data, IntPtr detail, uint detailSize, out uint required, IntPtr deviceInfo);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("winusb.dll", SetLastError = true)]
        static extern bool WinUsb_Initialize(SafeFileHandle device, out IntPtr handle);

        [DllImport("winusb.dll", SetLastError = true)]
        static extern bool WinUsb_Free(IntPtr handle);

        [DllImport("winusb.dll", SetLastError = true)]
        static extern bool WinUsb_GetAssociatedInterface(IntPtr handle, byte index, out IntPtr associated);

        [DllImport("winusb.dll", SetLastError = true)]
        static extern bool WinUsb_QueryInterfaceSettings(IntPtr handle, byte alternate, out InterfaceDescriptor descriptor);

        [DllImport("winusb.dll", SetLastError = true)]
        static extern bool WinUsb_QueryPipe(IntPtr handle, byte alternate, byte pipeIndex, out PipeInformation information);

        [DllImport("winusb.dll", SetLastError = true)]
        static extern bool WinUsb_WritePipe(IntPtr handle, byte pipe, byte[] buffer, uint length, out uint transferred, IntPtr overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        static extern bool WinUsb_ReadPipe(IntPtr handle, byte pipe, byte[] buffer, uint length, out uint transferred, IntPtr overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        static extern bool WinUsb_ControlTransfer(IntPtr handle, SetupPacket setup, byte[] buffer, uint length, out uint transferred, IntPtr overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        static extern bool WinUsb_SetPipePolicy(IntPtr handle, byte pipe, uint policy, uint valueLength, ref uint value);

        [DllImport("winusb.dll", SetLastError = true)]
        static extern bool WinUsb_AbortPipe(IntPtr handle, byte pipe);

        // ---- finding the cables ----

        static bool IsCable(string path)
        {
            string lower = path.ToLowerInvariant();
            if (lower.IndexOf(Vendor) < 0) return false;
            foreach (string product in Products)
                if (lower.IndexOf(product) >= 0) return true;
            return false;
        }

        /// <summary>The device interface paths registered under one interface class.</summary>
        static List<string> Paths(Guid interfaceClass)
        {
            List<string> found = new List<string>();
            IntPtr set = SetupDiGetClassDevs(ref interfaceClass, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
            if (set == IntPtr.Zero || set.ToInt64() == -1) return found;
            try
            {
                for (uint index = 0; index < 512; index++)
                {
                    DeviceInterfaceData data = new DeviceInterfaceData();
                    data.Size = Marshal.SizeOf(typeof(DeviceInterfaceData));
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref interfaceClass, index, ref data)) break;
                    uint needed;
                    SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out needed, IntPtr.Zero);
                    if (needed < 8 || needed > 4096) continue;
                    IntPtr detail = Marshal.AllocHGlobal((int)needed);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);      // the detail structure's own size
                        if (SetupDiGetDeviceInterfaceDetail(set, ref data, detail, needed, out needed, IntPtr.Zero))
                        {
                            string path = Marshal.PtrToStringUni(new IntPtr(detail.ToInt64() + 4));
                            if (!string.IsNullOrEmpty(path)) found.Add(path);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detail);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
            return found;
        }

        /// <summary>The interface classes the cable's WinUSB driver registered. Zadig gives each
        /// install its own, kept in the registry under the device (the chip is a composite
        /// device, so the bridge is usually its own "MI_xx" entry).</summary>
        static List<Guid> DriverClasses()
        {
            List<Guid> found = new List<Guid>();
            try
            {
                using (RegistryKey usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB"))
                {
                    if (usb == null) return found;
                    foreach (string device in usb.GetSubKeyNames())
                    {
                        if (!IsCable(device)) continue;
                        using (RegistryKey deviceKey = usb.OpenSubKey(device))
                        {
                            if (deviceKey == null) continue;
                            foreach (string instance in deviceKey.GetSubKeyNames())
                            {
                                try
                                {
                                    using (RegistryKey parameters = deviceKey.OpenSubKey(instance + @"\Device Parameters"))
                                    {
                                        if (parameters == null) continue;
                                        List<string> texts = new List<string>();
                                        string[] many = parameters.GetValue("DeviceInterfaceGUIDs") as string[];
                                        if (many != null) texts.AddRange(many);
                                        string one = parameters.GetValue("DeviceInterfaceGUID") as string;
                                        if (one != null) texts.Add(one);
                                        foreach (string text in texts)
                                        {
                                            Guid parsed;
                                            if (Guid.TryParse(text, out parsed) && !found.Contains(parsed)) found.Add(parsed);
                                        }
                                    }
                                }
                                catch (Exception) { }
                            }
                        }
                    }
                }
            }
            catch (Exception) { }
            return found;
        }

        /// <summary>How many bridge cables are plugged into this PC, with or without the driver.</summary>
        public static int Plugged()
        {
            int count = 0;
            try
            {
                foreach (string path in Paths(UsbDeviceInterface))
                    if (IsCable(path)) count++;
            }
            catch (Exception) { }
            return count;
        }

        /// <summary>The cables WinUSB can open, in a steady order: cable 1, 2, 3.</summary>
        static List<string> Cables()
        {
            List<string> found = new List<string>();
            List<Guid> classes = DriverClasses();
            classes.Add(UsbDeviceInterface);                   // a cable whose whole device has the WinUSB driver
            foreach (Guid interfaceClass in classes)
            {
                foreach (string path in Paths(interfaceClass))
                {
                    if (!IsCable(path) || found.Contains(path)) continue;
                    // the whole-device entry of a cable whose bridge has its own MI_xx entry is the same cable
                    if (interfaceClass == UsbDeviceInterface && found.Count > 0) continue;
                    found.Add(path);
                }
            }
            found.Sort(StringComparer.OrdinalIgnoreCase);
            return found;
        }

        // ---- one open cable, shared by every connection on its route ----

        static readonly object sharedGate = new object();
        static readonly UsbPipe[] shared = new UsbPipe[3];

        /// <summary>Cable index's pipe, opened once for every thread on its route. WinUSB lets one
        /// handle hold the interface, so a broken pipe is let go of before a new one is opened.</summary>
        public static UsbPipe Shared(int index)
        {
            lock (sharedGate)
            {
                UsbPipe pipe = shared[index];
                if (pipe != null && !pipe.broken) return pipe;
                if (pipe != null) pipe.Close();
                shared[index] = null;
                pipe = new UsbPipe(index);
                shared[index] = pipe;
                return pipe;
            }
        }

        public static void CloseAll()
        {
            lock (sharedGate)
                for (int i = 0; i < shared.Length; i++)
                    if (shared[i] != null) { shared[i].Close(); shared[i] = null; }
        }

        class Waiter
        {
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public int Result = -1;              // 1 done, 0 failed, -1 the answer never came
            public long Written;                 // ticks: when its packet was on the cable
        }

        SafeFileHandle file;
        IntPtr first = IntPtr.Zero, data = IntPtr.Zero;
        byte outPipe, inPipe;
        volatile bool broken;
        bool closed;
        readonly object writeGate = new object();
        readonly ConcurrentQueue<Waiter> waiting = new ConcurrentQueue<Waiter>();
        byte[] scratch;

        UsbPipe(int index)
        {
            List<string> cables = Cables();
            if (index >= cables.Count)
            {
                if (Plugged() > index) throw new UsbLinkException("the USB cable is there but Windows has no WinUSB driver for it: " + DriverHint);
                throw new UsbLinkException(index == 0 ? "no USB bridge cable (Prolific PL27A1) found on this PC" : "USB bridge cable " + (index + 1) + " isn't plugged into this PC");
            }
            // read and write access, shared, opened for overlapped use as WinUSB requires
            file = CreateFile(cables[index], 0xC0000000, 3, IntPtr.Zero, 3, 0x40000080, IntPtr.Zero);
            if (file.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                throw new UsbLinkException("the USB cable is there but can't be opened (Windows error " + error + (error == 5 ? ": another program holds it" : "") + "): " + DriverHint);
            }
            if (!WinUsb_Initialize(file, out first))
            {
                int error = Marshal.GetLastWin32Error();
                file.Close();
                throw new UsbLinkException("the USB cable is there but WinUSB can't use it (Windows error " + error + "): " + DriverHint);
            }
            // the data channel is whichever interface has a bulk IN and OUT pair
            if (BulkPair(first)) data = first;
            for (byte i = 0; data == IntPtr.Zero && i < 4; i++)
            {
                IntPtr other;
                if (!WinUsb_GetAssociatedInterface(first, i, out other)) continue;
                if (BulkPair(other)) data = other; else WinUsb_Free(other);
            }
            if (data == IntPtr.Zero)
            {
                Close();
                throw new UsbLinkException("the USB cable has no interface with bulk IN and OUT endpoints: " + DriverHint);
            }
            // what Linux's plusb does on bind (enable, reset both pipes, peer); harmless if the chip ignores it
            uint got;
            SetupPacket setup = new SetupPacket();
            setup.RequestType = 0xC0; setup.Request = 3; setup.Value = 0x99;
            WinUsb_ControlTransfer(first, setup, null, 0, out got, IntPtr.Zero);
            uint writeTimeout = 30000;
            WinUsb_SetPipePolicy(data, outPipe, 3, 4, ref writeTimeout);      // PIPE_TRANSFER_TIMEOUT
            Thread reader = new Thread(Answers);
            reader.IsBackground = true;
            reader.Name = "usb answers";
            reader.Start();
        }

        bool BulkPair(IntPtr handle)
        {
            InterfaceDescriptor descriptor;
            if (!WinUsb_QueryInterfaceSettings(handle, 0, out descriptor)) return false;
            bool haveOut = false, haveIn = false;
            byte o = 0, n = 0;
            for (byte i = 0; i < descriptor.NumEndpoints; i++)
            {
                PipeInformation info;
                if (!WinUsb_QueryPipe(handle, 0, i, out info) || info.PipeType != 2) continue;
                if ((info.PipeId & 0x80) != 0)
                {
                    if (!haveIn) { haveIn = true; n = info.PipeId; }
                }
                else if (!haveOut) { haveOut = true; o = info.PipeId; }
            }
            if (!haveOut || !haveIn) return false;
            outPipe = o;
            inPipe = n;
            return true;
        }

        /// <summary>Both ends as the bridge chip sees them (Prolific's vendor request 0xF1), e.g.
        /// "PC end USB 3, PS5 end USB 2.0"; "" when the chip doesn't say.</summary>
        public string Ends()
        {
            try
            {
                byte[] two = new byte[2];
                uint got;
                SetupPacket setup = new SetupPacket();
                setup.RequestType = 0xC0; setup.Request = 0xF1; setup.Length = 2;
                if (broken || !WinUsb_ControlTransfer(first, setup, two, 2, out got, IntPtr.Zero) || got < 2) return "";
                return End("PC", two[0]) + ", " + End("PS5", two[1]);
            }
            catch (Exception)
            {
                return "";
            }
        }

        static string End(string name, byte status)
        {
            if ((status & 2) == 0) return name + " end not connected";
            return name + " end " + ((status & 4) != 0 ? "USB 3" : "USB 2.0");
        }

        void Answers()
        {
            byte[] buffer = new byte[16384];
            while (!broken)
            {
                uint got;
                if (!WinUsb_ReadPipe(data, inPipe, buffer, (uint)buffer.Length, out got, IntPtr.Zero))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (broken) return;
                    if (error == 121) continue;                 // a timed-out read: nothing came
                    Break();
                    return;
                }
                for (int i = 0; i < got; i++)
                {
                    Waiter waiter;
                    if (!waiting.TryDequeue(out waiter)) continue;
                    waiter.Result = buffer[i] == 1 ? 1 : 0;
                    waiter.Done.Set();
                }
            }
        }

        /// <summary>The cable stopped: every frame still waiting for its answer is lost, and the
        /// next Shared() opens it afresh.</summary>
        void Break()
        {
            broken = true;
            Waiter waiter;
            while (waiting.TryDequeue(out waiter)) waiter.Done.Set();     // Result stays -1
        }

        public void Close()
        {
            Break();
            lock (writeGate)
            {
                if (closed) return;
                closed = true;
                try
                {
                    if (data != IntPtr.Zero) WinUsb_AbortPipe(data, inPipe);
                    if (data != IntPtr.Zero && data != first) WinUsb_Free(data);
                    if (first != IntPtr.Zero) WinUsb_Free(first);
                }
                catch (Exception) { }
                data = first = IntPtr.Zero;
                try { if (file != null) file.Close(); } catch (Exception) { }
            }
        }

        /// <summary>Writes one packet (its header, then size bytes of body) as a single transfer and
        /// waits for its answer: true when the PS5 did what was asked.</summary>
        public bool Send(byte[] head, byte[] body, int size, int timeoutMs)
        {
            long wire, answer;
            return Send(head, body, size, timeoutMs, out wire, out answer);
        }

        /// <summary>wireTicks: Stopwatch ticks until the packet was on the cable (its turn on the cable
        /// included); answerTicks: the wait for the PS5's answer after that.</summary>
        public bool Send(byte[] head, byte[] body, int size, int timeoutMs, out long wireTicks, out long answerTicks)
        {
            object token = Post(head, body, size, out wireTicks);
            return Collect(token, timeoutMs, out answerTicks);
        }

        /// <summary>Writes one packet as a single transfer and returns without waiting: Collect takes
        /// the token and waits for that packet's answer.</summary>
        public object Post(byte[] head, byte[] body, int size, out long wireTicks)
        {
            wireTicks = 0;
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            if (broken) throw new UsbLinkException("the USB cable stopped answering");
            Waiter waiter = new Waiter();
            lock (writeGate)
            {
                if (broken || closed) throw new UsbLinkException("the USB cable stopped answering");
                int total = head.Length + size;
                int padded = total % PadUnit == 0 ? total + 1 : total;
                if (scratch == null || scratch.Length < padded) scratch = new byte[Math.Max(padded, Proto.ChunkSize + 8192)];
                Buffer.BlockCopy(head, 0, scratch, 0, head.Length);
                if (size > 0) Buffer.BlockCopy(body, 0, scratch, head.Length, size);
                if (padded != total) scratch[total] = 0;
                waiting.Enqueue(waiter);               // before the write: answers come back in this order
                uint wrote;
                if (!WinUsb_WritePipe(data, outPipe, scratch, (uint)padded, out wrote, IntPtr.Zero) || wrote != padded)
                {
                    int error = Marshal.GetLastWin32Error();
                    Break();
                    throw new UsbLinkException("writing to the USB cable failed (Windows error " + error + ")");
                }
                waiter.Written = System.Diagnostics.Stopwatch.GetTimestamp();
            }
            wireTicks = waiter.Written - began;
            return waiter;
        }

        public bool Collect(object token, int timeoutMs, out long answerTicks)
        {
            Waiter waiter = (Waiter)token;
            answerTicks = 0;
            if (!waiter.Done.Wait(timeoutMs > 0 ? timeoutMs : AnswerTimeoutMs) || waiter.Result < 0)
            {
                // an answer that never came: later answers would go to the wrong frames
                Break();
                throw new UsbLinkException("the PS5 didn't answer over the USB cable: is the receiver running?");
            }
            answerTicks = System.Diagnostics.Stopwatch.GetTimestamp() - waiter.Written;
            return waiter.Result == 1;
        }

        /// <summary>Opens cable index and asks the receiver for a folder the PS5 always has, which it
        /// answers only when it is reading the cable.</summary>
        public static UsbPipe Ping(int index)
        {
            UsbPipe pipe = Shared(index);
            byte[] ask = Proto.Packet(Proto.OpMkdir, false, 0, 0, 0, 0, System.Text.Encoding.UTF8.GetBytes("/data/prospero-win"));
            pipe.Send(ask, null, 0, PingTimeoutMs);
            return pipe;
        }
    }
}
