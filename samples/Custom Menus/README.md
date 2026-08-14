# LIFX custom menu

Import [`LIFX.json`](LIFX.json) under **Device → Custom Menus** the same way as
the [host samples](https://github.com/DMXCore/DmxCore100/tree/main/samples/Custom%20Menus).

Custom menus cannot publish MQTT directly (OSC is the only raw-message item
type). Colour buttons play cues that should set the fixture you put in
**Follow fixture**. Effect buttons still play cues; drive those looks from a
script (below) or the plugin page toggles.

## After import

1. Patch a dummy RGB fixture and put its code in **Follow fixture** on the
   LIFX plugin page.
2. Create cues whose codes match the buttons: `WARM`, `WHITE`, `PARTY`,
   `RED`, `GREEN`, `BLUE`, `COOL`, `CHASE`, `SINE`, `RAINBOW`, `PIXEL`,
   `IDENTIFY`, `STOP`, `BLACK`. Program the followed fixture in each colour
   cue (warm wash, white, off, …).
3. Turn on **Follow master dimmer** if the **Dimmer** slider should scale LIFX
   brightness. Turn on **Blackout when a cue ends** if stopping playback should
   power the bulbs off.

To run a LIFX effect from a menu button, change it to **Run Script**:

```javascript
dmx.mqtt.publish("dmxcore/" + /* device serial */ + "/lifx/command", "effect all chase");
```
