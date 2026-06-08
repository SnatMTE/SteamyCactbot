# Snat's CactBridge

A [Dalamud](https://github.com/goatcorp/Dalamud) plugin that integrates [Cactbot](https://github.com/OverlayPlugin/cactbot) into Final Fantasy XIV.

> **This is still in development.** UI elements are all over the place, and the actual text display is currently not easy to see.

## How it works

- **Local relay HTTP server** - spins up a lightweight TCP-based HTTP server that proxies the Cactbot raidboss overlay from the remote source, injects a relay script inline, and serves your custom `raidboss-user.js` triggers.
- **Embedded headless Chromium** - uses PuppeteerSharp to download and run a full headless Chromium browser (~150 MB, cached after first launch) that loads the proxied Cactbot overlay. No external browser or overlay app needed.
- **WebSocket connection** - connects to OverlayPlugin's `ws://127.0.0.1:10501/ws` endpoint to subscribe to real-time events (zone changes, combat state, log lines, broadcast messages).
- **ImGui overlay window** - renders Cactbot alerts directly in-game using Dalamud's ImGui rendering, so you see triggers without alt-tabbing.
- **Chat relay** - optionally outputs alerts to the in-game chat via the announcement channel.

## Features

- **Designed for Steam Deck and Windows** - built to run on both platforms without extra hassle.
- **Chat announcements** - outputs Cactbot triggers and alerts directly to the in-game chat via announcement channels.
- **Built-in overlay** - includes an integrated overlay window so you don't need a separate overlay application.

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
