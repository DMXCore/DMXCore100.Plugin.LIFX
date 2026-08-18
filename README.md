# DMX Core 100 — LIFX Plugin

Drives **LIFX** WiFi color bulbs and SuperColour / pixel fixtures from DMX
Core fixture or playback data over the [LIFX LAN protocol](https://lan.developer.lifx.com/).
No cloud account required.

## Setup

Lights and the DMX Core must be on the same LAN (or have UDP 56700 routed
between them). Give each bulb a **static DHCP lease** so the mapping's IP
does not move.

On the Core's **Outputs** page, add an output of type **LIFX**:

1. **Protocol** — Color (RGB), Color + CT (RGB + color temperature),
   Color RGBW, Color RGBW + CT — each in 8-bit or **16-bit** — or Pixel
   (RGB × discovered zones, 8- or 16-bit).
2. **Destination Address** — the device IP. Use **Discover**, or type a
   known address. Pixel devices show a zone count, e.g.
   `Bar (192.168.1.30, LIFX SuperColour Tube, 52 px)`; picking one also
   fills the mapping's **Pixels** field automatically. Tick **16-bit** on a
   Pixel mapping for six channels per pixel.
3. **Start Channel** — DMX start address of that device's channels.

In the fixture editor, patch bulbs with the plugin's **LIFX / Color Bulb**
profile; its personalities match the color protocols one-to-one (**RGB**,
**RGB+CT**, **RGBW**, **RGBW+CT**, and the same four as **16-bit**).
Pixel mappings take `zones × 3` channels (`zones × 6` in 16-bit) from
discovery. The Mapped Device selector prefills from an existing LIFX
mapping. Presets, cues, effects, and Fixture Control then drive the device
through the normal lighting pipeline.

The Core rate-limits each mapping to 20 updates/second and coalesces
latest-wins. Color mappings convert RGB(W) to LIFX HSBK and send
`SetColor` with a 75 ms duration. Pixel mappings send `Set64` (Tube /
Luna / tiles) or `SetExtendedColorZones` (Beam / strips).

**RGBW:** LIFX bulbs have no separate white emitter, so the White channel
is mixed additively into the color (it lifts red, green, and blue equally —
desaturating the color and raising brightness), and the bulb renders that
white portion at the mapping's color temperature (3500 K when there is no
CT channel). White alone gives a full-brightness white; half red plus half
white gives a pastel red.

**16-bit:** every function takes two adjacent channels, coarse then fine
(`R, R fine, G, G fine, B, B fine[, W, W fine][, CT, CT fine]`), and the
plugin feeds the full 16-bit value into LIFX's 16-bit HSBK, so long fades
stay smooth at low levels instead of stepping through 256 values.

Requires a Core whose plugin SDK contract is **1.8** or newer (16-bit
plugin fixture profiles; the 8-bit features need 1.6).

| Protocol | Channels | Notes |
|---|---|---|
| `LIFX_COLOR` | RGB | White is mixed in RGB; kelvin stays at 3500 K |
| `LIFX_COLOR_CT` | RGB + ColorTemperature | CT 0 = 1500 K (warm), 255 = 9000 K (cool) |
| `LIFX_COLOR_RGBW` | RGB + White | White mixed additively at 3500 K |
| `LIFX_COLOR_RGBW_CT` | RGB + White + ColorTemperature | White mixed additively at the CT kelvin |
| `LIFX_COLOR_16` | RGB, coarse + fine (6 ch) | 16-bit brightness and hue |
| `LIFX_COLOR_CT_16` | RGB + CT, coarse + fine (8 ch) | |
| `LIFX_COLOR_RGBW_16` | RGB + W, coarse + fine (8 ch) | |
| `LIFX_COLOR_RGBW_CT_16` | RGB + W + CT, coarse + fine (10 ch) | |
| `LIFX_PIXEL` | RGB × zones (× 6 with **16-bit** ticked) | SuperColour Tube/Luna, Beam, Z, Neon, String, Tile. The Pixels field (stamped by Discover) sets the channel count; discovery results also persist across restarts. |

**SuperColour Tube pixel count:** the Tube's firmware reports a 5×11 tile
(55 zones) but only 52 light — device indexes 0–1 are the two zones on
top, 2–4 are dead, and 5–54 run from the top of the side down to the base
(measured on hardware). The plugin exposes it as **52 pixels** in order
(pixel 1–2 top, 3–52 down the side) and routes around the gap; a mapping
created with an older version that stored **Pixels = 55** still works
(the last three pixels are ignored) but should be re-picked from Discover
so the fixture footprint matches.

## Troubleshooting

- **No lights in Discover:** confirm the bulbs are powered and on the same
  subnet, then press Discover again. The plugin broadcasts LIFX
  `GetService` (not mDNS), then queries labels, product, and zone/tile
  geometry for SuperColour / strips, re-asking devices that did not answer.
  A device that is busy being streamed to sometimes drops a request; a
  scan never removes a previously seen device or forgets its geometry, so
  the list only ever grows (a device whose IP is taken over by another
  LIFX device is replaced).
- **Pixel mapping has 0 channels:** set the mapping's **Pixels** field, or
  run Discover on the Pixel protocol and pick the device (which fills it).
  Give the fixture a static DHCP lease.
- **Bulb does not follow cues:** check the mapping's IP, that the fixture
  is patched to the LIFX profile, and that the output is enabled.
- **Wrong bulb:** destination is the IP address. Re-run Discover after a
  DHCP change, or set a static lease.
- **Plugin will not load:** the device firmware must expose SDK 1.8+
  (1.0.x of this plugin needs 1.6+).

## Development

```shell
dotnet test tests/DMXCore100.LIFX.Tests
./pack.sh            # or pack.ps1 — produces artifacts/lifx-plugin.dmxplugin
```

```powershell
pwsh ./deploy-dev.ps1     # pack and upload to localhost:8080 (prompts for PIN)
```

Until the SDK version this plugin needs is on nuget.org, restore it from
`local-feed/` (see the comment in `nuget.config`): pack `src/PluginSdk` and
`src/PluginSdk.Testing` from the Software repo with `-p:Version=1.8.0 -o
<this repo>/local-feed`. The `.nupkg` files are git-ignored.

Iterate with `tools/DMXCore100.LIFX.DevHost` (F5 in Visual Studio) and the
unit tests — both use [`TestPluginHost`](https://www.nuget.org/packages/DMXCore.PluginSdk.Testing).
Core 2026.8+ hot-reloads an uploaded `.dmxplugin`; older firmware applies
it on the next device restart.

```text
discover
send 192.168.1.10 255 0 0
sendct 192.168.1.10 255 180 80 0
sendrgbw 192.168.1.10 128 0 0 128
sendmode LIFX_COLOR_16 192.168.1.10 0 128 0 0 0 0   # red fine byte only
sendpixel 192.168.1.30 255 0 0
r                    # shutdown + initialize again
d                    # dump registered protocols / profiles
```

Every push to `main` publishes a GitHub Release marked latest (immutable
`plugin-<sha>` tag) carrying the packed `.dmxplugin`. The product build
downloads it with GitHub’s “latest release” mode, not a moving `latest` tag.

## License

[MIT](LICENSE)
