# Pothole

Pothole is a simple ad-blocking DNS server for Windows. Run it on one PC and point your devices (or your whole home router) at it: ads, trackers and known malware sites stop loading on everything that uses it.

It's one program file. No installer, nothing else to set up.

## Download

Get `Pothole.exe` from the [Releases page](https://github.com/DontGoToCollege/Pothole/releases). Put it in any folder you like; it keeps its settings in a `Pothole-Data` folder next to it.

## How to use it

1. Open `Pothole.exe` and press **Start**. (Windows may ask whether to let it through the firewall: allow it on private networks so other devices can reach it.)
2. The top of the window shows this PC's address, for example `192.168.1.20`.
3. Point a device at it: in the device's Wi-Fi or network settings, set the **DNS server** to that address.
4. To cover every device at home at once, log in to your router and set its DNS server to that address.
5. For the PC Pothole runs on, tick **Use on this PC** at the top. Windows asks for permission; unticking it puts your old setting back.

Keep the PC on and Pothole running, or those devices won't be able to look up sites. If something stops working, press **Stop** and set DNS back to automatic.

If your router also hands out IPv6 DNS servers, some devices may use those and skip Pothole. If ads still get through on a device, look for an IPv6 DNS setting in your router and point it at the PC too, or turn it off.

## What's in the window

- **Dashboard**: how many requests went through, how many were blocked, a chart of the last 24 hours, and the most visited sites, most blocked sites and busiest devices.
- **Activity**: every lookup as it happens. Select one and press **Block this site** or **Always allow this site**.
- **Block lists**: ready-made lists of ad and tracker sites (StevenBlack, HaGeZi, OISD). Tick the ones you want, or add your own by web address. They update themselves once a day.
- **Allow & block**: your own lists. *Always allow* wins over everything, so use it when a block list breaks a site you need. Rules cover subdomains too.
- **Settings**: which DNS provider Pothole asks for allowed sites (Cloudflare, Google, Quad9 or your own), start with Windows, and keep running in the tray.

**Pause blocking** (top right, or in the tray menu) lets everything through without stopping the server. Closing the window keeps Pothole running in the tray; use **Exit** from the tray menu to quit.

## License

MIT: free for anyone to use, change and share. See [LICENSE](LICENSE).
