# Live link

Lets a tool on the same PC preview looks in the running game, so its sliders and timeline
show on the real shaders. It is off by default: turn on **Live link** in RumbleReShaded's settings.

It listens on `127.0.0.1:47810`, for one client at a time. Messages are JSON, one per line, UTF-8.

## Messages

Send a hello first. Anything else as the first line, or nothing for two seconds, closes the connection.

```json
{ "hello": "rrs-live-link", "version": 1 }
```

The reply lists what is installed, and `features` what this end can do beyond looks (left out by versions
that only do looks). `player` is the player's own value.

```json
{ "hello": "rrs-live-link", "version": 1, "features": ["globals"], "packs": [
  { "id": "UltraShade", "name": "UltraShade", "parameters": [
    { "property": "_Saturation", "label": "Saturation", "min": 0, "max": 2, "default": 1, "player": 1 }
  ] }
] }
```

Then set a pack's look, as often as you like. Each one replaces the last for that pack: a slider left out
goes back to the player's value. Sliders are named by label or shader property, and kept in range.

```json
{ "look": { "pack": "UltraShade", "on": true, "values": { "Saturation": 0.4 } } }
```

Hand one pack back:

```json
{ "release": "UltraShade" }
```

With `"globals"` in `features`, set shader globals as a mod does with `RRS.SetGlobal`, to preview what a
mod would drive during play - its beat, say. Names are `RRS.SetGlobal`'s keys: `mymod.beat` is read in a
shader as `_RRS_mymod_beat`. A `null` puts one back to 0. Send them as often as they change, every frame
for a clock.

```json
{ "globals": { "mymod.running": 1, "mymod.beat": 0.25, "mymod.health": 1 } }
```

Closing the connection hands back every pack and puts every global the link set back to 0. A mod setting
the same global at the same time fights the link over it, so preview with one
or the other.

## What it can't do

Looks go through `RRS.Override`, so they are temporary, and the player's settings are never written. They
stack with other mods' overrides, and the link's goes on top. A pack the link hasn't touched is left alone.
