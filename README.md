# Prospero Sender Lite 0.19

A stripped-down sender for a jailbroken PS5: a split-screen file manager (this PC on the
left, the PS5 on the right) that sends files with Prospero Sender Turbo's bonded transfer
system over every link at once. No FTP, no prospero-win, no emulators.

Written in C# (WinForms, .NET Framework 4.8, which Windows 10 and 11 already have).

## Build
Double-click `build-lite-0.19.bat`. It uses the C# compiler that ships in Windows, writes
`prospero-sender-lite-0.19.exe`, runs the self-test, and leaves `build-log-0.19.txt` and
`selftest-0.19.txt` beside it.

## What 0.19 does
- Connect to the PS5 by IP. The receiver (`mux_receiver-85.elf`) is inside the exe and is
  sent to the ELF loader (port 9021) when it isn't running, or on **Inject receiver**.
- PS5 side: **PS5** with **Internal drive**, **M.2 SSD** and any **USB drives** as drives,
  a folder tree, and the open folder's contents. Listed over the receiver's service port (9031).
- Send to the PS5: drag files from the left side or from Windows Explorer onto the PS5 side,
  copy and paste (Ctrl+C, Ctrl+V; a copy made in Explorer pastes too), or **Send to PS5**.
- New folder and Delete on the PS5; New folder, Rename and Delete (to the Recycle Bin) on the PC.
- Bonded sending: every detected link (Realtek RTL8156/8157/8159 and AX88179 adapters on the
  PS5, the PS5's own Ethernet port, the home network), several connections each, 4 MB pieces
  written at their offsets. A link that drops or stalls hands its pieces to the others and rejoins.
- **Files go to links by size** (new in 0.8): large files belong to the fast links (the Realtek
  2.5 / 5 GbE lanes and the USB bridge), each connection sending one file in order, the largest
  files to the fastest link. Small files travel in bundles over the slow links (PS5 Ethernet,
  Wi-Fi). A fast link with nothing left helps the file with the most still to go, from its end.
- LZ4 with learning: files and extensions that don't shrink stop being tried.
- Per-link on/off and connection count; "Also use PS5 Ethernet / Wi-Fi" boxes.
- **Configure dongles** (new in 0.2, Turbo's button): for the PC's ports on the Realtek 2.5 / 5 GbE
  lanes it turns Windows' task offload off, sets Jumbo Packet to match each lane's frame size on
  the PS5, turns Energy-Efficient Ethernet and power saving off, fixes a shared MAC address, and
  restarts the ports. One Windows permission prompt, a report after. A lane whose frames reach
  the PS5 small is flagged in the links table.
- **Payloads** (new in 0.4): a queue of any other .elf / .bin payloads for the ELF loader. Each has
  its own "Wait after" time; the queue checks the PS5 still answers before sending the next.
- **Cancel transfer** / **Cancel all** (new in 0.4; also Del or right-click on a transfer), and a
  Windows notification, sound and flashing taskbar button when a transfer finishes or fails.
- **Quantum Slipstream** (new in 0.5): sends the Quantum Slipstream Drive pkg (inside the exe; a
  `slipstream.pkg` beside the exe is used instead when present) so the receiver asks the PS5's
  installer for it, which switches the M.2 to its fast write mode until the PS5 restarts. Needs
  kstuff loaded. The pkg is deleted from the PS5 two minutes after it is taken.
- **USB bridge cable** (new in 0.6): a Prolific PL27A1 / PL25A1 host-to-host cable is a link like the
  others, up to three of them, through Windows' own WinUSB (no libusb). The cable needs the WinUSB
  driver once (Zadig: "USB-To-USB Cable Bridge", WinUSB, Replace Driver).
- **Traffic log** (new in 0.7): every 5 seconds of a send, `logs\lite-log.txt` gets one line with the
  speed landed, the PC's read speed, how full the read-ahead queue is, and for each link its
  speed, pieces, time on the wire and the PS5's answer time per piece.
- **Receiver...** (new in 0.9): shows and edits the receiver's own settings file on the PS5
  (`/data/prospero-win/receiver.conf`), with one-press "All CPU cores" (`cpus=root`), and shows the
  cores the receiver says it is running on. Saving restarts the receiver.
- **A steady start** (new in 0.10): a send waits while the PS5 is still clearing deleted files from
  the target drive; the large files begin one every quarter second instead of all at once; and
  the mid-size and small files go after the large ones are on their way. The log records how
  long the PS5 took to open each large file.
- Live graph: one band per link, stacked, with the 590 MB/s goal line; a table with each
  link's type, address, typical speed, connections, speed now, peak and bytes sent.

- **Smaller pieces on the fast lanes** (new in 0.11): the 5 GbE and 2.5 GbE lanes send 8 MB pieces
  instead of 32 MB and 16 MB, so the receiver holds about 280 MB at once instead of about 770 MB.
  The PS5 kernel log showed the receiver being stalled for seconds each time its memory passed
  about 1.2 GB.

- **A piece ahead on the fast links** (new in 0.12): each connection on the 5 GbE lane, the 2.5 GbE
  lane and the USB bridge cable sends its next piece while the PS5 is still writing the one before,
  instead of waiting for every answer.

- **12 MB pieces on the 5 GbE lane** (new in 0.14): 16 MB in 0.13, 8 MB before that; the other links are unchanged.

- **Helpers spread out** (new in 0.15): when a link has finished its own files it helps the others,
  but no file takes more than two helper connections, and a helper sends one piece at a time. In 0.14
  a whole link piled onto one file at the end of a send and the PS5 stalled.

- **All the links in view** (new in 0.16): the links table grows to fit its rows (6 to 8), so the
  sixth link no longer hides below a scroll bar.

- **Selected rows stay readable** (new in 0.16): a selected file, folder, transfer or link is white on
  blue whether or not its list has the focus.

- **One huge file uses every link again** (new in 0.17): the two-helpers-a-file rule is for folders;
  a single huge file is joined by all links, as before 0.15.
- **A cap on data in flight** (new in 0.17): all links together keep at most 700 MB unanswered, so
  the PS5's memory can't be overrun whatever the chunk sizes and connection counts.
  `inflight_mb` in `%LOCALAPPDATA%\\prospero-sender-lite\\settings.ini` changes it.

- **Slow links sit out a single huge file** (new in 0.18): one huge file goes over the fast links only
  (the 5 GbE and 2.5 GbE lanes and the USB bridge cable). Folder sends still use every link.

- **More writers on one huge file** (new in 0.19): the PS5 lets 8 connections write into a single huge
  file at once (4 before). `huge_writers` (1-16) in `%LOCALAPPDATA%\\prospero-sender-lite\\settings.ini`
  changes it; close Lite before editing the file.

## Not in 0.19 yet
- Rename on the PS5, copy and paste between PS5 folders, and PS5-to-PC copies: receiver 85
  has no operation for them. They need receiver 86.
- Choosing a lane's frame size (Configure dongles matches whatever the PS5 lane runs), the game cache / mirror copies / hard-drive mode, and the
  Games on PS5 list.
- The cable's "Try USB 3" switch, and queued (overlapped) writes on the cable.

## Files
- `src\Proto.cs` wire format, `src\Links.cs` link detection, `src\Engine.cs` the bonded sender,
  `src\Lz4.cs`, `src\Svc.cs` service port, `src\MainForm.cs`, `src\FilePane.cs`, `src\Graph.cs`,
  `src\Theme.cs`, `src\SelfTest.cs`, `src\Program.cs`.
- The log: `logs\lite-log.txt` beside the exe. Settings: `%LOCALAPPDATA%\prospero-sender-lite\`.
- `src\Dongles.cs` Configure dongles, `src\Payloads.cs` the payload queue, `src\Slipstream.cs`, `src\UsbBridge.cs` the bridge cable.
- A PNG named `ps5.png` beside the exe replaces the console icon.

License: LGPL-2.1-or-later, as Prospero Sender Turbo.
