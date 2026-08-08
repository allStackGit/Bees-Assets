# Bees audio memory

Compact implementation knowledge for location/background music. This is not a change log.

## Runtime soundtrack model

- `AudioController.SetupMusic()` selects music from `Level.MapData.Location`. A location intro plays once immediately; location/faction loop sources are scheduled to start after that location's serialized intro duration. The loop `AudioSource`s themselves are configured to loop indefinitely.
- Existing soundtrack handoff times are Pluto **17.142 s**, Neptune **14.545 s**, and Uranus **27.826 s**. These values are musical transition points, not generic delays.
- Pluto, Neptune, and Uranus each currently have three synchronized loop layers after the intro: the base location loop, a Human loop, and a Bees loop. `AudioController` pause/resume logic treats all active loop layers as one synchronized set.
- Titania currently has one composed base soundtrack and no separate Human/Bees stems. Titania setup must therefore clear those faction-loop references and tolerate absent optional layers rather than carrying the previous location's sources into the new map.

## Location and map identity

- `ConfigData.Locations` is currently used as the semantic location identity on `Data.Map` and as the music-selection key in `AudioController`; adding Titania does not require a broad location-switch audit elsewhere. Append new enum values rather than inserting them so existing serialized enum numbers remain stable.
- `LevelOptions.MapIndex`/`ConfigData.Maps` is a persisted gameplay/data contract, not just an inspector list. The current authoritative order is **0 Pluto, 1 Neptune, 2 Titania, 3 Uranus**. `ConfigData.Locations` is a separate semantic enum and deliberately keeps its older numeric values stable; do not assume Location enum numbers equal MapIndex values.
- `Space.unity` serializes `Prefabs.Maps` as Pluto, Neptune, Titania, Uranus, which now matches the authoritative MapIndex order. `Stage.FinalizeSceneWithUserData()` still calls `Prefabs.LoadConversions()` before `Pool.Setup()`, and runtime normalization by prefab name remains a fail-closed guard against future inspector reordering.
- `UI_Components.Map.Create(...)` overwrites the prefab's serialized `Name`, `Index`, and starting positions from `ConfigData.Maps`; those runtime values are authoritative for a pooled map instance.

## Titania soundtrack analysis

Source: `Music/Titania/Titania, Uranus' Moon.mp3` (the uploaded `Titania, Uranus' Moon brass(1).mp3` is the same audio).

- Source duration is about **350.851 s** at 44.1 kHz stereo.
- Beat analysis places the track around **72.8 BPM**. The opening behaves as a one-time introduction; the reusable main body begins on the strong downbeat around 26.56 s.
- The production split was refined to nearby sample-matched points to reduce loop-seam discontinuity without changing the musical phrase:
  - intro: **0.000000–26.565215 s**
  - loop: **26.565215–185.461179 s**
  - loop duration: **158.895964 s**
- The source contains another traversal of essentially the same long body after that first loop region, followed by roughly **6.49 s** of ending material. That repeated composition is strong evidence that the 158.896 s section is the intended reusable body rather than an arbitrary cut.
- Titania keeps the full authored source as a single Unity-imported resource and builds two runtime PCM `AudioClip`s at the analyzed sample boundaries. This gives the same one-shot-intro + true `AudioSource.loop` behavior as the other maps without re-encoding the loop or inventing `.meta` GUIDs. The existing source `.meta` is moved with the asset into `Resources/Music/Titania`, so Unity remains the metadata authority.
- `TitaniaMusicBuilder` copies in bounded chunks, then unloads the full source asset after constructing the shorter intro/loop clips. Titania currently has no Human/Bees stems; those remain intentionally null.
