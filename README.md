# DMX Core 100 — LIFX Plugin

Discovers LIFX fixtures on the LAN and drives colour, power, and effects over
the [LIFX LAN protocol](https://lan.developer.lifx.com/). No LIFX cloud
account — UDP on the local network only.

## User guide

### What you get

Once the plugin is running, LIFX lights on the same LAN can be discovered and
controlled from DMX Core scripts, MQTT, or the DevHost console.

| Capability | What it does |
|---|---|
| Discovery | Finds bulbs, SuperColour tubes/Lunas, strips, and tiles; skips LIFX switches |
| Colour | Sets RGB on a light or on `all`. Pixel fixtures get per-zone colour |
| Effects | Chase, sinewave, rainbow, and pixel-chase |
| Power | Turns lights on or off |
| Trigger | Fires `LIFX-DISCOVERED` after a pass that found at least one light |

Known IPs from the last discovery are stored in the plugin state blob and
probed first on the next run.

### Setup

Lights and the DMX Core must be on the same LAN (or have UDP 56700 routed
between them).

1. Web UI → **Settings → Remote Control**: enable the MQTT broker if you want
   to send commands from scripts or another MQTT client.
2. **Settings → Plugins**: enable **LIFX**.

Optional settings:

| Setting | Default | Purpose |
|---|---|---|
| Bind IP | `0.0.0.0` | Local interface for LIFX UDP. Leave as-is unless you need a specific NIC |
| Discover on startup | on | Broadcast-discover when the plugin starts |
| Discovery timeout | 5 s | How long to wait for `GetService` replies |
| Default fade | 45 ms | LIFX interpolation time for colour commands |
| Default brightness | 100 % | Scales colour and effect output |

### Commands

`serial` is the device hardware id, lowercase.

```
dmxcore/{serial}/lifx/command          text or JSON commands
dmxcore/{serial}/lifx/lights           retained discovered-light list
dmxcore/{serial}/lifx/{lightId}/set    per-light colour / power / effect
dmxcore/{serial}/lifx/{lightId}/state  retained colour + effect
```

Text on the command topic:

```
discover
list
color all 255 0 0
color Kitchen 0 128 255 50 200
power all on
effect all chase
effect all sinewave 400
effect all rainbow
effect Tube pixel-chase
effect all stop
```

`color` is `color <target> <r> <g> <b> [brightness] [fade_ms]`. Brightness is
0–1, or 0–100 if the value is greater than 1. Target is `all`, a light label,
an IP, or the hex id from the lights list.

JSON:

```json
{"cmd":"color","target":"all","r":255,"g":0,"b":0,"brightness":1,"fade_ms":45}
{"cmd":"effect","target":"all","name":"chase","speed_ms":400}
{"cmd":"power","target":"all","on":true}
{"cmd":"discover"}
```

Per-light `.../set` also accepts `ON` / `OFF` and a bare effect name
(`chase`, `sinewave`, `rainbow`, `pixel-chase`, `stop`).

From a DMX Core script:

```javascript
dmx.mqtt.publish("dmxcore/" + /* device serial */ + "/lifx/command", "color all 255 80 0");
```

Or map the **Lights discovered** plugin trigger (`LIFX-DISCOVERED`) in the
normal trigger UI.

### Ideas

- **Warm wash:** a custom-menu button that publishes `color all 255 140 40`.
- **Show start:** a cue-started script that runs `effect all chase`, and a
  cue-ended script that runs `effect all stop` then `color all 0 0 0`.
- **Pixel check:** `effect Tube rainbow` or `effect Tube pixel-chase` to
  confirm SuperColour mapping without a console.
- **Presence:** when the venue closes, `power all off`.

### Troubleshooting

- **No lights found:** confirm the fixtures are powered and on the same
  subnet. Try a specific **Bind IP** if the device has more than one NIC,
  then publish `discover` again.
- **Commands do nothing:** the plugin needs at least one successful
  discovery first. Check `dmxcore/{serial}/lifx/lights` for the current
  list, and that MQTT is connected (Settings → Remote Control).
- **Wrong light:** target by label, IP, or the hex `id` from the lights
  payload — `all` hits every discovered fixture.
- **Steppy colour:** raise **Default fade** (try 60 ms). Chase and
  sinewave send on a timer; rainbow on a zone fixture paints every pixel
  in one pass.

## Development

```
dotnet test tests/DMXCore100.LIFX.Tests
./pack.sh            # or pack.ps1 — produces artifacts/lifx-plugin.dmxplugin
```

`tools/DMXCore100.LIFX.DevHost` is an interactive console harness (F5 in
Visual Studio) against an in-memory host. Discovery and colour commands use
the real LAN client, so you can talk to bulbs on your network without a
device.

```
discover
color all 255 0 0
effect all chase
effect all stop
d                    # dump published lights / triggers
```

Every push to `main` recreates the rolling `latest` release carrying the
packed `.dmxplugin`; the DMX Core 100 product build downloads it from there
and bundles it as a built-in plugin.

## License

[MIT](LICENSE)
