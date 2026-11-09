# Event network setup

Target: everything running in under 15 minutes (RNF-09).

## What you need

- Laptop running TalkLens
- Your own Wi-Fi router, not connected to the venue network
- A phone sharing its connection, if you want cloud AI

## Steps

1. Connect the laptop to the event router, by cable if you can. Give it a fixed address with a DHCP reservation.
2. Optional: connect the laptop to the phone hotspot as well, by USB or a second Wi-Fi adapter. Check that internet traffic
   goes out through the hotspot: the router has no internet, so give it a higher interface metric or remove its gateway.
3. Allow TalkLens through the firewall. On Windows, in an administrator terminal:

   ```powershell
   New-NetFirewallRule -DisplayName "TalkLens web" -Direction Inbound -Protocol TCP -LocalPort 80,8080-8089,8443-8452 -Action Allow
   New-NetFirewallRule -DisplayName "TalkLens media UDP" -Direction Inbound -Protocol UDP -LocalPort 8189 -Action Allow
   New-NetFirewallRule -DisplayName "TalkLens media TCP" -Direction Inbound -Protocol TCP -LocalPort 8189 -Action Allow
   ```

4. Start TalkLens. On the operator panel, under **Connect**, check that "Address shown to phones" is the address on the event
   router. This matters when the laptop is on two networks.
5. Set `Network__WifiSsid` and `Network__WifiPassword` in `.env` to get a Wi-Fi QR code on the panel.

## Captive portal (opens the app on its own)

Phones check for internet when they join a Wi-Fi network. Sending those checks to the laptop opens the app.

1. Set `Network__HttpPort=80` in `.env`. On Linux, also allow the binding: `sudo setcap cap_net_bind_service=+ep` on the
   `dotnet` binary, or run behind a port redirect.
2. On the router, point all DNS at the laptop: use its DNS hijack or wildcard feature, or set the DHCP DNS server to a small
   resolver on the laptop that answers every name with the laptop's address. Router features differ, so check yours.

TalkLens answers the Android, iOS and Windows probe addresses with a redirect to the app, and redirects any unknown host name
the same way.

Portal mini browsers can be limited. If video does not start in one, have people open the address in their normal browser,
and print that address next to the QR. iPhones may warn that the network has no internet: tell attendees to stay connected.

With wildcard DNS the laptop must reach AI providers through the hotspot's DNS, so do not point the laptop's own DNS at the
router.

## Capacity

Each attendee uses about 2 Mbps at 720p, so a consumer router will saturate well before a large crowd. Measure yours with
the real room before relying on it. When it saturates:

- Set the camera phone to 480p.
- Ask attendees to turn on **Audio and captions only** in Settings, and show the video on the room screen.
- Add professional access points.
