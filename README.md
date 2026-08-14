# DMX Core 100 — LIFX Plugin

Discovers LIFX fixtures on the LAN and drives colour, power, and effects over
the [LIFX LAN protocol](https://lan.developer.lifx.com/). No LIFX cloud
account — UDP on the local network only.

## User guide

### What you get

Once the plugin is running, LIFX lights on the same LAN can be discovered and
tested from the plugin settings page, or driven from DMX Core cues, scripts,
and MQTT.

| Capability | What it does |
|---|---|
| Discovery | Finds bulbs, SuperColour tubes/Lunas, strips, and tiles; skips LIFX switches |
| Colour | Sets RGB on a light or on `all`. Pixel fixtures get per-zone colour |
| White | Shortcut for RGB 255,255,255 |
| Effects | Chase, sinewave, rainbow, and pixel-chase, from settings or MQTT |
| Power | Turns lights on or off |
| Identify | Paints each light a different hue so you can match Light 1–12 |
| Per-light test | Light 1–12 toggles send the test colour in scan order |
| Follow fixture | Tracks a Fixture Control fixture’s RGB + intensity |
| Master dimmer | Optional follow of `system.masterdimmer` |
| Cue-end blackout | Optional power-off when a top-level cue finishes |
| Trigger | Fires `LIFX-DISCOVERED` after a pass that found at least one light |

Known IPs from the last discovery are stored in the plugin state blob and
probed first on the next run.

### Setup

Lights and the DMX Core must be on the same LAN (or have UDP 56700 routed
between them).

1. Web UI → **Settings → Remote Control**: enable the MQTT broker if you want
   to send commands from scripts or another MQTT client.
2. **Settings → Plugins**: enable **LIFX**. Open the LIFX plugin page.

On that page:

1. Toggle **Discover now** to scan the LAN (or leave **Discover on startup** on).
2. Names from the last scan show on the plugin status line (the settings
   form cannot rename its fields after load).
3. Toggle **Identify lights** to paint each fixture a different colour, or
   **Light 1** … **Light 12** / **Test all lights** to send the test colour.
   Toggle off to turn those lights off.
4. **Chase** / **Sinewave** run those effects on every discovered light.
5. **Test red / green / blue** set the colour the test toggles send.
6. Optionally turn on **Follow master dimmer** and **Blackout when a cue
   ends**, and fill in **Follow fixture** with a Fixture Control code.

The UDP socket always binds all interfaces.

| Setting | Default | Purpose |
|---|---|---|
| Discover now | off | Rising edge scans the LAN |
| Discover on startup | on | Broadcast-discover when the plugin starts |
| Discovery timeout | 5 s | How long to wait for `GetService` replies |
| Identify lights | off | Distinct hue per discovered light |
| Test all lights | off | Send the test colour to every light; off powers them down |
| Chase | off | RGB chase on every discovered light |
| Sinewave | off | Hue sinewave on every discovered light |
| Light 1 … Light 12 | off | Test colour for the *n*th light from the last scan |
| Test red / green / blue | 255 / 80 / 0 | Colour used by the test toggles |
| Follow master dimmer | off | Scale LIFX brightness with the DMX Core master |
| Blackout when a cue ends | off | Power off when a top-level cue finishes |
| Follow fixture | empty | Fixture Control code to track, see below |
| Default fade | 45 ms | LIFX interpolation time for colour commands |
| Default brightness | 100 % | Scales colour and effect output |

### Follow fixture

Patch a dummy RGB fixture in Lighting Setup (or pick an existing one) and
put its code in **Follow fixture**. Every discovered LIFX light then tracks
that fixture’s colour and intensity from Fixture Control, cues, presets,
and scripts — including fades — instead of a static CueCode→look table.

Intensity 0 powers the LIFX lights off. Identify, test toggles, chase, and
sinewave on the plugin page take priority until you turn them off.

The fixture must be visible to the plugin as entity `fixture.YOURCODE` (or
the bare code) with `dmx.getFixture` levels (`red` / `green` / `blue` /
`intensity`, 0–1).

### Commands

`serial` is the device hardware id, lowercase.

```text
dmxcore/{serial}/lifx/command          text or JSON commands
dmxcore/{serial}/lifx/lights           retained discovered-light list
dmxcore/{serial}/lifx/{lightId}/set    per-light colour / power / effect
dmxcore/{serial}/lifx/{lightId}/state  retained colour + effect
```

Text on the command topic:

```text
discover
list
identify
white all
color all 255 0 0
colour Kitchen 0 128 255 50 200
power all on
effect all chase
effect all sinewave 400
effect all rainbow
effect Tube pixel-chase
effect all stop
```

`color` / `colour` is `color <target> <r> <g> <b> [brightness] [fade_ms]`.
Brightness is 0–1, or 0–100 if the value is greater than 1. Target is `all`,
a light label, an IP, or the hex id from the lights list. `white` is
`white [target] [brightness]`.

JSON:

```json
{"cmd":"color","target":"all","r":255,"g":0,"b":0,"brightness":1,"fade_ms":45}
{"cmd":"effect","target":"all","name":"chase","speed_ms":400}
{"cmd":"power","target":"all","on":true}
{"cmd":"identify"}
{"cmd":"white","target":"all"}
{"cmd":"list"}
{"cmd":"discover"}
```

Per-light `.../set` also accepts `ON` / `OFF` and a bare effect name
(`chase`, `sinewave`, `rainbow`, `pixel-chase`, `stop`).

From a DMX Core script:

```javascript
const serial = "device-serial";
dmx.mqtt.publish("dmxcore/" + serial + "/lifx/command", "color all 255 80 0");
```

Or map the **Lights discovered** plugin trigger (`LIFX-DISCOVERED`) in the
normal trigger UI.

### Ideas

- **Show follow:** patch a dummy RGB fixture, put its code in **Follow
  fixture**, and program that fixture in your cues / Fixture Control. Turn
  on **Follow master dimmer** if house LIFX should also track the grand
  master.
- **Warm wash:** publish `color all 255 140 40` from a script.
- **Pixel check:** `effect Tube rainbow` or `effect Tube pixel-chase` to
  confirm SuperColour mapping without a console.
- **Presence:** when the venue closes, `power all off`.

### Troubleshooting

- **No lights found:** confirm the fixtures are powered and on the same
  subnet, then toggle **Discover now** again. The plugin listens on every
  local interface.
- **Commands do nothing:** the plugin needs at least one successful
  discovery first. Check `dmxcore/{serial}/lifx/lights` for the current
  list, and that MQTT is connected (Settings → Remote Control).
- **Wrong light:** target by label, IP, or the hex `id` from the lights
  payload — `all` hits every discovered fixture. Use **Identify lights**
  to see which physical fixture is Light 1, 2, …
- **Steppy colour:** raise **Default fade** (try 60 ms). Chase and
  sinewave send on a timer; rainbow on a zone fixture paints every pixel
  in one pass.

## Development

```shell
dotnet test tests/DMXCore100.LIFX.Tests
./pack.sh            # or pack.ps1 — produces artifacts/lifx-plugin.dmxplugin
```

Iterate with `tools/DMXCore100.LIFX.DevHost` (F5 in Visual Studio) and the
unit tests — both use [`TestPluginHost`](https://www.nuget.org/packages/DMXCore.PluginSdk.Testing),
the same tester as the [example plugin](https://github.com/DMXCore/DMXCore100.ExamplePlugin).
The device loads plugins in-process and cannot unload .NET assemblies, so
uploading a new `.dmxplugin` needs a full device restart. The DevHost
recycles `Initialize` / `Shutdown` in the same process (`r`) and talks to
real bulbs over the LAN.

```text
discover
identify
white all
color all 255 0 0
effect all chase
effect all stop
cueend
s follow-fixture HOUSE
fixture HOUSE 255 140 40 80
s follow-master true
v 0.4                # master dimmer
r                    # shutdown + initialize again
d                    # dump published lights / triggers
```

Every push to `main` recreates the rolling `latest` release carrying the
packed `.dmxplugin`; the DMX Core 100 product build downloads it from there
and bundles it as a built-in plugin.

## License

[MIT](LICENSE)
