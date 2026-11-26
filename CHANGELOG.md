# Changelog

## 0.2.1 (2026-11-25)

"Start with Windows" now reflects the real Windows startup entry, and Pothole puts that entry back whenever this PC uses it for DNS, so a restart never leaves the PC without DNS.

## 0.2.0 (2026-11-25)

- New **Use on this PC** switch at the top: points this PC's own DNS (IPv4 and IPv6) at Pothole, and puts back what was there before when you untick it. Windows asks for permission each time. Turning it on also turns on "start with Windows", so the PC never ends up without DNS after a restart.
- Pothole now also listens on IPv6 (`::1`), so Windows can't go around it through IPv6.
- Exiting while this PC uses Pothole offers to put the PC's DNS back first. While stopped, the header warns that this PC can't look up sites.

## 0.1.1 (2026-11-25)

Typing something that isn't a site name under Allow & block now keeps your text after the warning, so you can fix it instead of retyping.

## 0.1.0 (2026-11-25)

First version: ad-blocking DNS server with one-click Start/Stop, dashboard (totals, 24-hour chart, top sites, blocked sites and devices), activity log with block/allow buttons, block lists (StevenBlack, HaGeZi, OISD or any hosts/domain/Adblock list, refreshed daily), your own allow and block lists, upstream DNS presets, pause blocking, tray icon and start with Windows.
