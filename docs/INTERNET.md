# Two PCs / internet: first-session checklist

Host (the PC that runs the server):

1. Unzip, run `BFBB-Online.exe`, press Play once alone to check the game starts patched.
2. Tick **Host the server on this PC** and **Open the port on my router (UPnP)**, press Play.
3. Read the log:
   - `router port 25599 opened (UPnP) ... friends connect to <ip>:25599`: give friends that ip.
   - `UPnP failed` / `no UPnP router answered`: forward **TCP 25599** to this PC in the router's
     web page (port forwarding / virtual server), and look up the public IP (e.g. whatismyip.com).
4. Windows Firewall asks about `BFBB-Online.exe`: allow it on **private and public** networks.
   If you clicked it away: Windows Security > Firewall > Allow an app > BFBB-Online.

Friend:

1. Same zip, same Deluxe 1.1 game (Heavy Mod Manager > Launch Game once).
2. **Server** = the host's public IP (or Tailscale/ZeroTier/Radmin address), port 25599, the same
   **Party** name as the host. Untick Host. Press Play. The host finds their public IP as the
   **IPv4** number on https://whatismyipaddress.com/ and should only send it privately to people
   they trust (it shows roughly where they live).
3. The party list shows both names and where each of you is.

Quick checks once both are in:

- Same level: you see each other, names over heads, shinies picked up by one count for both.
- Different levels: works; progress merges whenever someone changes level.
- Double-click a name in the party list to travel to that player.
- Enemies: run past one near your friend: it should chase whoever is closer.

If the friend can't connect:

- From outside your network, test the port with an online "open port checker" for TCP 25599
  while the host's launcher is running.
- Carrier-grade NAT (some mobile / fiber ISPs): port forwarding can't work; use Tailscale or
  ZeroTier (both free), then connect to the host's address on that virtual network.
- Both must use the same BFBB Online build (the server says so if the versions differ).

Lag: the game runs locally for each player; teammates and enemies driven by another PC are
~1 round trip behind. Up to ~150 ms ping plays fine.
