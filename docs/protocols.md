# Candela.Protocols — connecting a lighting console

`Candela.Protocols` receives control data from lighting consoles and show-control systems and feeds it into the DMX
input hub (`Candela.Dmx.Input.DmxInputHub`). The engine reads merged universes from the hub once per frame, and the
fixtures follow the console. This page covers what works, which ports are used, how to connect a grandMA3, an ETC Eos
or an Onyx, how universes are numbered, and the API for the rest of the program.

Module rules (see [ARCHITECTURE.md](ARCHITECTURE.md)): `Candela.Protocols` references only `Candela.Core` and
`Candela.Dmx`. Receivers run on their own background threads and only call the thread-safe hub. A run-time problem
(port in use, interface missing, device unplugged, malformed packet) becomes a status message. It never becomes an
unhandled exception.

## What works

The external-control panel lists all 15 protocols from the requirements, in this order. Every entry carries an
honest support level:

| Protocol | Support | Transport | What Candela does |
|---|---|---|---|
| **Art-Net** | Implemented | UDP 6454, broadcast + unicast | Art-Net 4 receiver: ArtDmx → hub, ArtPoll → ArtPollReply (consoles list Candela as a node), ArtSync, ArtTimeCode → hub timecode, other nodes' ArtPollReply listed |
| BlackTrax | Planned | RTTrPM (UDP) | Tracking receiver planned. BlackTrax-driven fixtures already work through the console's Art-Net/sACN output |
| CITP | Planned | TCP/UDP | Patch exchange (CAEX), media thumbnails, peer discovery planned |
| Compulite VC | Planned | proprietary | The Vector visualiser link is not publicly documented. Vector consoles output Art-Net and sACN |
| **ETC Eos** | Partial (via OSC) | UDP 8001 (configurable) | Eos OSC output: show name, active/pending/previous cue, cue-fire events, command line, live/blind. Levels come from the Eos Art-Net/sACN output |
| **EntTec DMX USB Pro** | Implemented | USB serial | Widget labels 3, 5, 6, 8, 9 and 10. DMX from a console's XLR cable goes into one universe |
| Kinesys | Planned | proprietary | Automation positions for moving trusses planned (needs Kinesys' protocol documentation) |
| LasergraphDSP | Planned | proprietary | Native laser frames planned. DMX/Art-Net-controlled lasers already work |
| MA-Net2 | Needs vendor SDK | proprietary | Needs MA Lighting's licensed SDK. Set the grandMA2 to output Art-Net or sACN instead |
| MA-Net3 | Needs vendor SDK | proprietary | Needs MA Lighting's licensed SDK. Set the grandMA3 to output Art-Net or sACN instead |
| NDI | Needs vendor SDK | NDI SDK | The NDI SDK cannot be bundled. Planned as an optional video-texture plug-in |
| **OSC** | Implemented | UDP 8000 (configurable) | OSC 1.0/1.1 messages and bundles, mapped to DMX with address patterns. Raw messages are exposed |
| PangolinLD | Planned | proprietary | Native laser frames planned. DMX/Art-Net-controlled lasers already work |
| **PosiStageNet** | Implemented | UDP multicast 236.10.10.10:56565 | PSN 2.0 info and data packets fill a tracker table (position, speed, orientation, acceleration, target position) |
| **sACN** | Implemented | UDP 5568, multicast 239.255.x.y + unicast | E1.31-2018 receiver: priority, sequence rule, stream termination, synchronisation, universe discovery |

**Acceptance path.** Connect a real console over Art-Net and run a timecode show. The console turns timecode into
DMX, Candela receives the DMX, and the fixtures follow pan, tilt, colour, intensity, zoom and the rest. If the
console (or a timecode source on the network) also sends ArtTimeCode, Candela shows the position in the
external-control panel.

## Universe numbering and the offset setting

Candela uses **internal universes numbered from 1**, the way consoles show them. They are the numbers in fixture
patches and in the hub.

* **Art-Net** addresses universes with a 15-bit *port-address* `Net:SubNet:Universe`, counted from **0**: Net 0–127,
  SubNet 0–15, Universe 0–15, so port-address = Net × 256 + SubNet × 16 + Universe. Candela maps
  **internal universe = port-address + offset**. The offset is the *Art-Net universe offset* setting
  (`AppSettings.ArtNetUniverseOffset`, `ProtocolOptions.ArtNetUniverseOffset`), and its default is **1**.
* **sACN** universes are already counted from 1 (1–63999), so internal universe = sACN universe. The offset does not
  apply.

| Console universe | Art-Net port-address | Internal universe (offset 1) | sACN universe |
|---|---|---|---|
| 1 | 0:0:0 (0) | 1 | 1 |
| 2 | 0:0:1 (1) | 2 | 2 |
| 16 | 0:0:15 (15) | 16 | 16 |
| 17 | 0:1:0 (16) | 17 | 17 |
| 257 | 1:0:0 (256) | 257 | 257 |

With the default offset 1, grandMA2/3, ETC Eos, Onyx, ChamSys and most other consoles line up: "universe 1" on the
console is universe 1 in Candela. Set the offset to **0** only when the sending software calls port-address 0:0:1
"universe 1". The tell-tale sign is that everything arrives one universe too low. Packets that map below universe 1
are counted as *ignored*.

**Subscribed universes** (`ProtocolOptions.SubscribedUniverses`) are the internal universes Candela wants. The engine
sets them from the patch, and the default is 1–4. They matter in two places:

* **ArtPollReply.** Candela announces them, four per reply, in as many replies as needed (BindIndex 1, 2, …). grandMA3
  and Eos in *unicast* mode only send universes that some node announced.
* **sACN multicast.** Candela joins the group 239.255.{u >> 8}.{u & 0xff} of each subscribed universe, plus the
  discovery universe 64214.

Data for other universes is still accepted (Art-Net broadcast, sACN unicast) unless
`AcceptUnsubscribedUniverses` is turned off.

## Network setup

* **Interface.** `ProtocolOptions.NetworkInterface` takes an IPv4 address (`192.168.1.20`), an adapter name or id
  (`eth0`, `Ethernet 2`), or empty for all interfaces. `ProtocolManager.GetNetworkInterfaces()` lists the adapters
  with name, IPv4 address, mask, broadcast and MAC for the settings window. An interface that does not exist (for
  example, a USB adapter that is unplugged) gives an **Error** status that names the adapters that do exist.
* **Ports.** Art-Net 6454, sACN 5568, OSC 8000, Eos OSC 8001, PSN 56565. Allow them through the firewall for UDP in.
  Candela opens its sockets with SO_REUSEADDR, so it can share 6454 or 5568 with other lighting software on the same
  computer (console offline editors, other visualisers). If another program holds a port exclusively, the status reads
  *"UDP port … is already in use by another program"*.
* **Binding.** Linux and macOS deliver broadcast and multicast datagrams only to sockets bound to the wildcard
  address. On those systems Candela binds to 0.0.0.0 and uses the selected interface for ArtPollReply fields and
  multicast membership. On Windows it binds to the selected interface.
* **IP plan.** The console and the Candela computer must be in the same subnet. Classic Art-Net uses 2.x.x.x/8 or
  10.x.x.x/8. Most consoles also work in an ordinary 192.168.x.x/24 network. With several network cards, select the
  one that faces the lighting network.
* **Multicast.** With a selected interface, sACN and PosiStageNet groups are joined on that interface. With *all
  interfaces*, they are joined on every operational adapter, so multicast from the lighting network arrives even when
  the computer's default route points at Wi-Fi or the office LAN, or when an isolated lighting network has no gateway.
  If a multicast group cannot be joined on any adapter (VPN adapters, containers), the status
  becomes **Warning** and lists the universes that failed. Unicast still works: set the console to send sACN unicast
  to Candela's IP. On Linux each socket holds 20 memberships by default, so Candela joins further groups through extra
  membership sockets. At most 512 universes are joined.

## Connecting a console

The menu names below follow current console software. They move between versions, so look for the setting by what it
does: *protocol output on*, *network port*, *destination* (broadcast or Candela's IP), and *universe mapping*.

### grandMA3 (Art-Net)

1. Connect the console (or onPC with a node, or the computer running grandMA3 onPC) to the same network as Candela.
   Give both addresses in one subnet, for example console 2.0.0.1/8 and Candela 2.0.0.10/8.
2. On the console, open **Menu → DMX Protocols → Art-Net**. Turn Art-Net on and choose the interface that faces the
   lighting network.
3. Add an output line: **Mode = Output**, *Local universe* = the first patched universe (1), *Amount* = the number of
   universes, and the Art-Net universe the line starts at. With the default mapping, local universe 1 goes out as
   0:0:0. For *Destination*, choose broadcast, or unicast to Candela's IP.
4. In Candela, select Art-Net in the external-control panel and press Start. Within a few seconds Candela should
   appear in the grandMA3 Art-Net node list as **Candela**, a DMX node with output ports for the subscribed universes.
5. Check the panel: every universe shows packets per second (around 30–44 Hz) and the console's IP as the source.
   If everything is one universe off, check the universe offset (above).
6. Run the timecode show. Candela follows whatever the console outputs. If the console uses ArtSync, Candela applies
   each frame of all universes at once (no tearing between universes).

**grandMA3 over sACN:** open **Menu → DMX Protocols → sACN**, enable it on the same interface, and add an output line
(local universe 1 → sACN universe 1, priority 100). Choose multicast, or unicast to Candela's IP. Then start sACN in
Candela. grandMA2 works the same way through **Setup → Network → Network Protocols** (Art-Net or sACN). MA-Net2/3
themselves cannot be received (see the table above).

### ETC Eos family (Eos, Ion Xe, Gio, Element 2, Nomad)

1. Network: in the Eos shell / ECU, open **Settings → Network → Interface Protocols**. Enable **sACN** (preferred on
   Eos) and/or **Art-Net** on the port that faces Candela.
2. In Eos, open **Setup → System Settings → Output** (or **Network → Output** in newer versions). Make sure sACN or
   Art-Net output is enabled. Eos sends sACN universe *n* for Eos universe *n*, and Art-Net 0:0:0 for universe 1, so
   Candela's default offset 1 matches.
3. In Candela, start **sACN** (or Art-Net). Eos sends sACN multicast by default, so Candela joins the subscribed
   universes. If the status shows a multicast warning, enable unicast sACN to Candela's IP in Eos, or fix multicast
   on the network.
4. Optional, cue information: in Eos **Setup → System → Show Control → OSC**, enable **OSC TX**, set **OSC UDP TX IP
   Address** to Candela's IP and **OSC UDP TX Port** to 8001. Then start **ETC Eos** in Candela. The panel shows the
   show name, the active and pending cues, and fade progress. Fixture levels still come from sACN/Art-Net.

### Onyx (Obsidian)

1. In Onyx, open the DMX output configuration (**Settings → DMX Output** / *Onyx Manager → DMX Outputs*,
   depending on the version). For each used universe, choose **Art-Net** or **sACN** as the output and pick the network
   adapter that faces Candela.
2. Art-Net: choose broadcast or unicast to Candela's IP. Onyx universe 1 goes out as 0:0:0, which matches Candela's
   default offset 1. sACN: multicast or unicast, universe 1 → 1.
3. Start the same protocol in Candela and check the per-universe packet rates in the panel.

ChamSys MagicQ, Hog 4, Avolites Titan and lighting software such as QLC+, Resolume or LightKey work the same way:
enable Art-Net or sACN output and point it at the network Candela is on. If a program numbers Art-Net universes from 1
(0:0:1 = "universe 1"), set Candela's offset to 0.

### No console at hand: the built-in test console

`ArtNetTestConsole` plays an `ArtNetTestPattern` over Art-Net. The pattern sweeps pan and tilt along sine curves (each
fixture phase-shifted), runs a colour chase, sends a dimmer wave and makes the zoom breathe. Every frame carries
ArtSync and ArtTimeCode. Point it at `127.0.0.1:6454` to drive a local Candela, or at a node to drive a real rig. It is
meant for `Candela.Cli` and the editor, which build it from the patch. `ArtNetTestPattern.DemoRig(...)` builds a
12-channel moving-head + RGBW-par rig that needs no GDTF. The background loop never stops on an error (network down,
failing encoder); the last one is shown in `ArtNetTestConsole.LastError`. The engine can plug GDTF-based encoders from `Candela.Dmx`
into the same show through `ITestPatternEncoder`.

## Protocol details

### Art-Net 4

* **ArtDmx (0x5000).** Requires ProtVer ≥ 14. Lengths 1–512 that fit the datagram are accepted (the specification asks
  for even lengths; odd lengths from older consoles are unambiguous and accepted). Bit 15 of the port-address is
  ignored. The hub source is `DmxSourceId("artnet", "<sender IP>/<physical>")`, merged HTP/LTP by the hub at priority
  100. When an ArtPollReply from the sender is known, its short name becomes the source name.
* **Sequence.** Sequence 0 disables the check. Otherwise a frame 1–19 steps behind the last one of the same stream is
  dropped (counted as a sequence error). A bigger step back is taken as a sender restart.
* **ArtSync (0x5200).** After a controller sends ArtSync, its ArtDmx frames are held and released together on its next
  ArtSync. ArtSync from another IP does not release them (Art-Net 4 rule). After 4 s without ArtSync, output is
  immediate again. Can be turned off with `ArtNetSync = false`.
* **ArtPoll (0x2000) → ArtPollReply (0x2100).** Replies are unicast to the poller on port 6454, and also to its source
  port when that differs. They are broadcast as well when `ArtNetBroadcastPollReply` is on, for Art-Net 3 controllers.
  Targeted polls (flag bit 5) are answered only for binds inside the requested port-address range. Reply fields:
  * IP of the interface facing the poller, and port 0x1936;
  * firmware 1, Net/SubSwitch, OEM `0xFFFF` and ESTA `0x7FF0` (both configurable);
  * short name *Candela* and the long name;
  * node report `#0001 [nnnn] …`;
  * NumPorts, and PortTypes `0x80` (DMX512 output from Art-Net);
  * GoodOutputA: bit 7 when data arrived within 4 s, bit 3 when merging, bit 1 when the hub merges LTP;
  * GoodOutputB `0xF0` (RDM disabled, continuous output, no RDM discovery), and SwOut;
  * Style StNode, MAC of the interface, BindIp and BindIndex;
  * Status1 `0xD0`, Status2 `0x08` (15-bit port-addresses), and RefreshRate 44.
* **ArtTimeCode (0x9700).** Fields are range-checked: frames below the type's rate, hours up to 23, type 0–3. Valid
  packets go to `DmxInputHub.SubmitTimecode` and raise `ArtNetReceiver.TimeCodeReceived`.
* **ArtPollReply from others** fills `ArtNetReceiver.Nodes` (consoles and nodes on the network). Candela's own looped
  replies are ignored. All other opcodes are counted as ignored.

### sACN (ANSI E1.31-2018)

Each layer of every packet is validated:

* **Root layer:** preamble 0x0010, postamble 0, the ACN identifier, flags/length 0x7xxx within the datagram, vector
  0x00000004 (data) or 0x00000008 (extended), and the CID.
* **Framing layer:** vector 0x00000002, priority 0–200, universe 1–63999.
* **DMP layer:** vector 0x02, type 0xA1, first address 0, increment 1, and a property count that fits.

Accepted data is handled like this:

* **Start codes.** Only start code 0 is dimmer data. Others, such as 0xDD per-address priority, are ignored.
* **Sequence rule.** Per CID and universe, a packet is discarded when −20 < (new − last) ≤ 0, as a signed 8-bit
  difference.
* **Options.** Preview data is ignored unless `SacnAcceptPreviewData` is on. *Stream_Terminated* removes the source
  from the hub at once (`hub.Terminate`), and the data in that packet is ignored.
* **Synchronisation.** Data with a non-zero sync address is held until the synchronisation packet for that address
  arrives, as long as sync packets keep coming (2.5 s). Otherwise it is output immediately. The sync universe's
  multicast group is joined on demand.
* **Universe discovery** (universe 64214) fills `SacnReceiver.DiscoveredSources`: name, IP and every page of universes.
* **Hub.** Each source is submitted with its priority as `DmxSourceId("sacn", "<IP>", cid)` and its source name. The
  hub merges the sources of the highest priority.

`SacnSender` sends data, termination, synchronisation and discovery packets, to multicast or unicast, for tests and
tools.

### OSC

Candela parses OSC 1.0/1.1 messages and bundles (nested up to 8 levels) with the argument types `i f s S b h t d c r m
T F N I [ ]`. Messages without a type-tag string are accepted with no arguments. Every message is raised through
`MessageReceived` and kept in `RecentMessages` (the last 128), so console integrations can use the raw data.

DMX mapping uses patterns from `ProtocolOptions.OscPatterns`. The defaults are `/dmx/{universe}/{channel}` and
`/dmx/{universe}`.

* **Segments.** `{universe}` is an internal universe from 1. `{channel}` is 1–512 and is the first slot written. `*`
  matches any one segment, and anything else is literal (case-sensitive).
* **Options** follow the address: `universe=N` (used when there is no `{universe}` segment) and
  `scale=auto|byte|normalized|percent`.
* **Values.** In `auto`, integers are 0–255, floats are 0–1 and T/F are 255/0. A blob writes its bytes to consecutive
  slots. Several numeric arguments write consecutive slots. Strings and NaN are skipped. Messages that match no
  pattern are counted as ignored.
* **Examples:** `/dmx/1/12 ,i 255`, `/dmx/1/12 ,f 0.5`, `/dmx/2 ,b <512 bytes>`,
  `/fader/{channel} universe=3 scale=percent`.
* **Senders.** Each sender IP keeps its own universe buffers, which the hub merges HTP. OSC senders only send changes,
  while the hub expires silent sources after 3 s, so held universes are re-submitted every second.

### ETC Eos (via OSC)

`EtcEosReceiver` listens for Eos OSC output on `EosOscPort` (default 8001) and keeps `EosConsoleState`:

* show name, and live/blind state;
* the active cue (list/number, text, fade progress 0–1), the pending cue and the previous cue;
* the last cue fired (also raised as `CueFired`), and the command line.

It does not drive DMX. Use Eos sACN/Art-Net output for levels.

### PosiStageNet 2.0

Packets are trees of chunks, each with a 32-bit little-endian header: id, 15-bit length and a has-subchunks bit.

* **Info packets (0x6756)** carry the system name and tracker names.
* **Data packets (0x6755)** carry trackers with position, speed, orientation (rotation vector), status, acceleration,
  target position and timestamp.
* **Version.** Only PSN 2.x headers are accepted. Unknown chunks are skipped.

`PsnReceiver.Trackers` is a table keyed by server address and tracker id. Partial updates merge into the previous
values, and trackers silent for 10 s drop out. Values stay in the tracking system's own coordinate frame (metres,
m/s, m/s², radians): how it maps to Candela's Z-up stage frame depends on the tracking system's calibration.
`PsnPacketWriter` builds packets for tests and simulators.

### EntTec DMX USB Pro

* **Frames.** Each frame is `0x7E, label, length LSB, length MSB, data (0–600), 0xE7`. `EnttecProFrameDecoder`
  reassembles frames from arbitrary chunks. It skips garbage and resynchronises after a corrupted frame.
* **Start-up.** Candela sends label 8 (send every received frame), label 3 (get parameters: firmware, break and MAB
  time in 10.67 µs units, rate) and label 10 (serial number, 8 BCD digits).
* **Label 5 (received DMX).** The widget's status byte flags receive overruns and queue overflows. Those frames are
  dropped as bad. With start code 0, the slots go to universe `EnttecUniverse` (default 1) as
  `DmxSourceId("enttec", "<port>")`.
* **Label 9 (change of state)** updates a held copy of the universe.
* **Label 6.** `EnttecDmxUsbPro.SendDmx` outputs DMX through the widget.
* **Transport.** The widget is reached through `IEnttecTransport`. `SerialPortEnttecTransport` wraps
  `System.IO.Ports.SerialPort`, and tests use an in-memory fake. An unplugged widget turns the status into **Error**
  ("disconnected").

## Status and statistics

`IExternalProtocol.Status` returns a `ProtocolStatus` snapshot:

* **State:** Stopped, Running, Warning, Error or Unavailable.
* **Message:** a one-line summary, for example "Listening on UDP 6454 (eth0 2.0.0.10); announcing 4 universe(s)…".
* **LastError**, and **Warnings**.
* **Counters:** Packets (accepted), BadPackets (malformed), IgnoredPackets (well-formed but not for Candela), the last
  packet time, and the last bad packet with its reason and sender.
* **Universes:** per universe, the packets, sequence errors, last packet time, measured packets per second and last
  source.

`StatusChanged` is raised when the state, message, error or warnings change. It is not raised per packet.

Planned and vendor-SDK protocols can be selected, but `Start` returns false with **Unavailable** and the reason.

## Using it from code

```csharp
var hub = new DmxInputHub();
using var protocols = new ProtocolManager(hub);           // all 15 protocols, display order

var options = ProtocolOptions.FromSettings(appSettings) with
{
    SubscribedUniverses = patch.UsedUniverses,              // announce/join what the patch needs
    EnttecSerialPort = "COM3",
};
var statuses = protocols.Apply(new[] { ProtocolIds.ArtNet, ProtocolIds.Sacn }, options);
protocols.StatusChanged += (_, e) => ui.Refresh(e.Status);

// Every frame, on the main thread:
if (hub.TryRead(universe, buffer, out var version) && version != lastVersion) { /* evaluate fixtures */ }
```

`Apply` stops protocols that are no longer enabled. It starts new ones, and restarts a running one only when an
option *that protocol uses* changed (`ExternalProtocolBase.OptionsSignature`: for Art-Net the interface, subscribed
universes, offset, port, names and codes; for sACN the interface, subscribed universes, port and preview/multicast
switches; and so on). Editing an OSC pattern or the EntTec port therefore does not interrupt Art-Net or sACN. Two protocols configured on the same
UDP port are refused: the later one in display order gets an Error status. `ProtocolManager.GetNetworkInterfaces()`
and `GetSerialPorts()` feed the settings window.

## Performance and robustness

* **Allocation-free receive path.** Receive loops use `Socket.ReceiveFrom(Span<byte>, SocketFlags, SocketAddress)`
  into one reused buffer, and senders are identified by integers. Art-Net, sACN and EntTec processing allocates
  nothing once a sender and universe have been seen (checked by tests). OSC necessarily allocates its message objects.
* **Isolation.** A failing datagram handler is counted as a bad packet. It never stops the receive thread. Exceptions
  thrown by `StatusChanged` subscribers are contained as well, because status changes are often raised on receive,
  timer and serial-reader threads, where an escaping exception would end the program.
* **Bounded state.** Art-Net and sACN track at most 4096 streams (sender/port/universe or CID/universe); idle streams
  (10 s) make room, and further live ones are ignored with a warning. OSC holds at most 1024 sender universes.
* **Fuzzing.** Thousands of random, truncated and bit-flipped datagrams go through every parser and receiver in the
  tests. None throws, and every receiver still works afterwards.
* **Clean stop.** Stopping a protocol closes its socket or port, joins its thread and withdraws its sources from the
  hub. The universes it fed are dropped immediately instead of after the 3 s source timeout.

## Not implemented yet

* CITP, BlackTrax RTTrPM, Kinesys, PangolinLD, LasergraphDSP and Compulite VC (planned).
* NDI, MA-Net2 and MA-Net3 (need vendor SDKs).
* An Eos parameter/patch sync over OSC (TCP) beyond cue events.
* Unsolicited ArtPollReply on node changes (consoles poll every few seconds, so the node list stays current).
* sACN per-address priority (start code 0xDD) is ignored rather than merged per slot.
* RDM.
