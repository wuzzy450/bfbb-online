# BFBB Online: online co-op for Battle for Bikini Bottom

Play **SpongeBob SquarePants: Battle for Bikini Bottom DELUXE** together with your friends, in
Dolphin. The whole party shares one campaign: spatulas, socks, shinies, tasks and unlocked areas.

- Everyone can be in a different level at the same time.
- In the same level you see each other run around, with name tags over your heads.
- Collect, smash, press or defeat something and it happens in everyone's game.

> **Status:** work in progress. Played by two players on two PCs over the internet (thanks Mickey),
> and the bugs found there are fixed. An automated suite with bot players runs on every change:
> all 66 gameplay scenes plus the final-boss scenes load. Not yet tested in a real 2-player
> final-boss fight. Bug reports welcome in
> [Issues](https://github.com/wuzzy450/bfbb-online/issues).

**Community:** join the [Discord](https://discord.gg/nuvpXycag8) · support the project on
[Ko-fi](https://ko-fi.com/wuzzy450)

## Install (every player, 2 minutes)

You need Windows 10/11 (64-bit), **Dolphin**, and **BFBB DELUXE 1.1** set up with
**Heavy Mod Manager** (press **Launch Game** in it once, so it builds the game folder).
No game files are included: BFBB Online patches **your own** copy when you press Play.

1. Download **[BFBB-Online.zip](https://github.com/wuzzy450/bfbb-online/raw/main/release/BFBB-Online.zip)**.
2. Unzip it into your Dolphin folder, so you get `Dolphin-x64\BFBB-Online\`.
3. Double-click `BFBB-Online\BFBB-Online.exe`. If Windows warns you, click **More info**, then
   **Run anyway**.

That's it. The launcher finds Dolphin and your Deluxe game by itself. If it can't, use the two
links in its window to point at them.

> Everyone must use the **same** BFBB Online version and BFBB DELUXE **1.1**.

## Play

**The host** (one person):

1. Type your **name**, pick a **color**, and type a **party** name.
2. Tick **Host the server on this PC** and press **Play**.
3. The launcher shows an address. Send it to your friends:
   - same house or Wi-Fi: your local address
   - over the internet: your public IP address plus `:25599`. To find it, open
     [whatismyipaddress.com](https://whatismyipaddress.com/), wait for the **IPv4** box to load,
     and send your friends that number with `:25599` on the end (it looks like `12.34.56.78:25599`, with your own numbers).
     The launcher's log also prints it once the router port opens (`friends connect to ...`).

   > Your public IP shows roughly where you live. Only send it to people you know and trust,
   > in a private message, never in a public chat, stream or video.

**Friends:**

1. Type your name, pick a color, and type the **same party name** as the host.
2. Paste the host's address into **Server** and press **Play**.

Dolphin starts with the online patch. Start a new game or load your save as usual. The first
player in a new party brings their progress; everyone joining later gets the party's progress
the next time their game loads a level.

## Good to know

- **Shinies** are one party wallet: collecting adds for everyone, spending takes from everyone.
- **Enemies** move the same for everyone and chase whoever is closest. A kill counts once.
- **Bosses** are one fight with one shared health bar.
- **Races and timed challenges** are per player; the reward counts once for the party.
- **Story cutscenes** play for everyone in that level. Teammates elsewhere see them later.
- When a teammate talks to an NPC in your level, a small line at the bottom of the screen asks
  whether to **watch along** (Up = yes, Down = no). Signs and hints don't ask.
- **Patrick and Sandy:** switch at a bus stop like normal; your teammates see the character you
  picked, with their moves, slides and ground pounds.
- **Cannons** fire for everyone while anyone stands in their zone. **Moving platforms** are in the
  same place in every game.
- **Teleport to a teammate:** double-click (or right-click) their name in the launcher's party list.

## Problems

| Problem | Fix |
|---|---|
| *"unexpected game code at ..."* | Your game isn't Deluxe 1.1. Press **Launch Game** in Heavy Mod Manager again and retry. |
| Windows Firewall asks about `BFBB-Online.exe` | Click **Allow** (the host must allow it). |
| NKit warning in Dolphin | Harmless; BFBB Online turns it off for its own launches. |
| Friends can't connect over the internet | See *Playing over the internet* below. |
| Anything else | Open an [issue](https://github.com/wuzzy450/bfbb-online/issues) with what happened. |

<details>
<summary><b>Advanced: internet play, dedicated server, building from source</b></summary>

### Playing over the internet

The host's launcher has **Open the port on my router (UPnP)** ticked by default: it asks the
router to forward **TCP 25599** and the log shows the address to give friends. If the log says
UPnP failed, forward **TCP 25599** to the host PC by hand and give friends your public IP (the
**IPv4** number on [whatismyipaddress.com](https://whatismyipaddress.com/); share it privately). No
router access? Use Tailscale, ZeroTier or Radmin VPN and connect to the host's address on that
network. First-session checklist: [docs/INTERNET.md](docs/INTERNET.md).

### Dedicated server (always on)

`BFBB-Online\server\start_server.bat` runs the server without a game window. Settings are in
`server\server.json`: port, where party saves go, and **map sims** (the server runs a small
headless copy of each level that has players, ~15-20% of a CPU core and ~500 MB RAM each;
*max sims* caps how many).

### Building from source

Needs Windows, Python with `pip install ziglang`, and the [.NET 8 SDK](https://dotnet.microsoft.com/download).

```powershell
powershell -ExecutionPolicy Bypass -File tools\package.ps1
```

This builds the in-game patch (`patch\build.py`), the launcher and the server, and writes
`release\BFBB-Online.zip`.

### Project layout

| Path | What |
|---|---|
| `patch/` | in-game patch (C + PowerPC asm, built with zig), hooks into the Deluxe 1.1 DOL |
| `client/` | launcher (C#, WinForms): starts Dolphin, talks to the game through a mailbox in RAM |
| `server/` | party server and map sims; `server/Dedicated` is the standalone server |
| `shared/` | protocol and mailbox layout shared by launcher and server |
| `docs/` | [DESIGN.md](docs/DESIGN.md), [PROTOCOL.md](docs/PROTOCOL.md), [INTERNET.md](docs/INTERNET.md) |
| `release/` | prebuilt `BFBB-Online.zip` |

### For AI agents

- `patch/build.py` builds the in-game patch into `patch/out/bbol_patch.json`; the launcher
  (`client/BfbbOnline.csproj`) embeds it and applies it to the player's own Deluxe 1.1 `main.dol`.
- Game and launcher talk through a mailbox in game RAM: `patch/src/mailbox.h` = `shared/Mailbox.cs`.
- Self-test (two bot games + server on one PC): `BFBB-Online.exe --selftest [--sims]`, writes
  `selftest-report.md` next to the exe.

</details>

## Credits

- **wuzzy450**: creator
- **Mickey**: tester

## License

MIT, see [LICENSE](LICENSE). BFBB Online is an unofficial fan project, not affiliated with
Nickelodeon, THQ Nordic, Purple Lamp, Heavy Iron or the Dolphin project. No game files are included.
