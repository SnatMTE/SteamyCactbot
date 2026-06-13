# Snat's CactBridge

A [Dalamud](https://github.com/goatcorp/Dalamud) plugin that integrates [Cactbot](https://github.com/OverlayPlugin/cactbot) raidboss alerts, encounter timelines, and a real-time damage meter directly into Final Fantasy XIV - no external browser or overlay app required. Alerts are sent as native FFXIV toast notifications by default, keeping everything within the game UI.

> **This is still in development.** UI elements are still being refined.

## How it works

- **Local relay HTTP server** - spins up a lightweight TCP-based HTTP server that proxies the Cactbot raidboss overlay from the remote source, injects a relay script inline, and serves custom `raidboss-user.js` triggers.
- **Embedded headless Chromium** - uses PuppeteerSharp to download and run a full headless Chromium browser (~150 MB, cached after first launch) that loads the proxied Cactbot overlay. No external browser or overlay app needed.
- **WebSocket connection** - connects to OverlayPlugin's `ws://127.0.0.1:10501/ws` endpoint to subscribe to real-time events (zone changes, combat state, log lines, broadcast messages).
- **ImGui overlay windows** - renders Cactbot alerts, encounter timelines, and a DPS meter directly in-game using Dalamud's ImGui rendering, so you see everything without alt-tabbing.
- **Chat relay & toast notifications** - by default, alerts are sent as native FFXIV toast notifications to keep everything within the game UI. Optionally, alerts can also be output to the in-game chat via the announcement channel.

## Features

- **Raidboss alerts overlay** - real-time Cactbot trigger alerts (Info / Alert / Alarm) rendered with per-type colours, smooth fade-out, and configurable display. Uses native FFXIV toast notifications by default.
- **Encounter timeline overlay** - upcoming boss abilities with countdown timers, progress bars, and configurable look-ahead.
- **Damage meter overlay** - real-time DPS meter with party DPS, personal DPS, encounter stats, and per-column colour scheme.
- **Customisable text appearance** - multiple font presets (FFXIV Axis, Dalamud Default, Dalamud Mono, FFXIV Jupiter, FFXIV Trump Gothic), adjustable font scale, custom text colours, and optional text outlines.
- **Toast notifications** - alerts can be sent as native FFXIV toasts via the game's toast system (default mode).
- **Chat announcements** - outputs Cactbot triggers and alerts directly to the in-game chat via announcement channels.
- **Server info bar (DTR) entries** - optional party DPS and personal DPS displayed in the server info bar.
- **Move mode** - drag any overlay to reposition it with `/cactbridge move`. Lock positions to prevent accidental dragging.
- **Configurable overlay sizing** - adjust width and height independently for each overlay.
- **Per-overlay enable/disable** - toggle each overlay (alerts, timeline, damage meter) on or off from the settings.
- **Background browser status** - see the headless Chromium state (Downloading, Launching, Running, Error) in the config window.
- **Auto-reconnecting WebSocket** - persistent connection to OverlayPlugin with automatic reconnect on failure.
- **ACT log forwarding** - forwards ACT log lines and zone change events into the headless browser's Cactbot event system, ensuring timelines load and update correctly.
- **Designed for Steam Deck and Windows** - built to run on both platforms without extra hassle.

## Similar Plugins

- There is a plugin similar to this but I honestly don't remember what it is called as I believe they renamed themselves. The logic worked kinda the same but the approach is different and I was aiming for a one click solution - no messing around with browser paths etc.

## Status

This plugin was created purely for my portfolio to demonstrate working with **C#** and various **APIs** (Dalamud, PuppeteerSharp, WebSocket, HTTP). There will be no support provided, but assuming the Dalamud API doesn't massively change, I will keep it updated.

## Requirements

- [Dalamud](https://github.com/goatcorp/Dalamud) / [XIVLauncher](https://github.com/goatcorp/FFXIVQuickLauncher)
- [IINACT](https://github.com/marzent/IINACT) - must be running with its WebSocket server on port **10501** (default)
- [Cactbot](https://github.com/OverlayPlugin/cactbot)

## Installation

This plugin will **not** be submitted to the official Dalamud plugin repository.

To install it, add the following URL to Dalamud's settings under external plugin repositories:

- `https://snatmte.github.io/CactBridge/plugin.json`

## Disclaimer

Use at your own risk. This plugin is provided as-is with no guarantees of stability, compatibility, or ongoing support.
