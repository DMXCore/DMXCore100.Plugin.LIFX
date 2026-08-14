# DMX Core 100 — LIFX Plugin

Drives **LIFX** WiFi color bulbs and SuperColour / pixel fixtures from DMX
channel data over the [LIFX LAN protocol](https://lan.developer.lifx.com/).
No cloud account — UDP on the local network only, device port **56700**.

## Setup

Lights and the DMX Core must be on the same LAN (or have UDP 56700 routed
between them). Give each bulb a **static DHCP lease** so the mapping's IP
does not move.

On the Core's **Outputs** page, add an output of type **LIFX**:

1. **Protocol** — Color (3 ch RGB), Color + CT (4 ch RGB + color
   temperature), or Pixel (RGB × discovered zones).
2. **Destination Address** — the device IP. Use **Discover**, or type a
   known address. Pixel devices show a zone count, e.g.
   `Bar (192.168.1.30, LIFX SuperColour Tube, 52 px)`; picking one also
   fills the mapping's **Pixels** field automatically.
3. **Start Channel** — DMX start address of that device's channels.

In the fixture editor, patch bulbs with the plugin's **LIFX / Color Bulb**
profile (personalities **RGB** or **RGB+CT**). Pixel mappings take
`zones × 3` channels from discovery (SuperColour Tube is typically 52 or
55 pixels). The Mapped Device selector prefills from an existing LIFX
mapping. Presets, cues, effects, and Fixture Control then drive the device
through the normal lighting pipeline.

The Core rate-limits each mapping to 20 updates/second and coalesces
latest-wins. Color mappings convert RGB to LIFX HSBK and send `SetColor`
with a 75 ms duration. Pixel mappings send `Set64` (Tube / Luna / tiles)
or `SetExtendedColorZones` (Beam / strips).

Requires a Core whose plugin SDK contract is **1.4** or newer (output
protocols, destination discovery, plugin fixture profiles, and mapping
fields).

| Protocol | Channels | Notes |
|---|---|---|
| `LIFX_COLOR` | RGB | White is mixed in RGB; kelvin stays at 3500 K |
| `LIFX_COLOR_CT` | RGB + ColorTemperature | CT 0 = 1500 K (warm), 255 = 9000 K (cool) |
| `LIFX_PIXEL` | RGB × zones | SuperColour Tube/Luna, Beam, Z, Neon, String, Tile. The Pixels field (stamped by Discover) sets the channel count; discovery results also persist across restarts. |

## Troubleshooting

- **No lights in Discover:** confirm the bulbs are powered and on the same
  subnet, then press Discover again. The plugin broadcasts LIFX
  `GetService` (not mDNS), then queries labels, product, and zone/tile
  geometry for SuperColour / strips.
- **Pixel mapping has 0 channels:** set the mapping's **Pixels** field, or
  run Discover on the Pixel protocol and pick the device (which fills it).
  Give the fixture a static DHCP lease.
- **Bulb does not follow cues:** check the mapping's IP, that the fixture
  is patched to the LIFX profile, and that the output is enabled.
- **Wrong bulb:** destination is the IP address. Re-run Discover after a
  DHCP change, or set a static lease.
- **Plugin will not load:** the device firmware must expose SDK 1.4+.

## Development

```shell
dotnet test tests/DMXCore100.LIFX.Tests
./pack.sh            # or pack.ps1 — produces artifacts/lifx-plugin.dmxplugin
```

```powershell
pwsh ./deploy-dev.ps1     # pack and upload to localhost:8080 (prompts for PIN)
```

Iterate with `tools/DMXCore100.LIFX.DevHost` (F5 in Visual Studio) and the
unit tests — both use [`TestPluginHost`](https://www.nuget.org/packages/DMXCore.PluginSdk.Testing).
Core 2026.8+ hot-reloads an uploaded `.dmxplugin`; older firmware applies
it on the next device restart.

```text
discover
send 192.168.1.10 255 0 0
sendct 192.168.1.10 255 180 80 0
sendpixel 192.168.1.30 255 0 0
r                    # shutdown + initialize again
d                    # dump registered protocols / profiles
```

Every push to `main` recreates the rolling `latest` release carrying the
packed `.dmxplugin`.

## License

[MIT](LICENSE)
