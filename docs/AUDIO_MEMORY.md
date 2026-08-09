# Bees audio memory

Compact implementation knowledge for location/background music. This is not a change log.

## Runtime soundtrack model

- `AudioController.SetupMusic()` selects music from `Level.MapData.Location`. A location intro plays once immediately; location/faction loop sources are scheduled to start after that location's serialized intro duration. The loop `AudioSource`s themselves are configured to loop indefinitely.
- Existing soundtrack handoff times are Pluto **17.142 s**, Neptune **14.545 s**, and Uranus **27.826 s**. These values are musical transition points, not generic delays.
- Pluto, Neptune, and Uranus each currently have three synchronized loop layers after the intro: the base location loop, a Human loop, and a Bees loop. `AudioController` pause/resume logic treats all active loop layers as one synchronized set.
- Titania currently has one composed base soundtrack and no separate Human/Bees stems. Titania setup must therefore clear those faction-loop references and tolerate absent optional layers rather than carrying the previous location's sources into the new map.

## Location and map identity

- `ConfigData.Locations` is the semantic location identity on `Data.Map` and the music-selection key in `AudioController`. It is a different contract from map order. Keep its existing numeric values stable: Pluto=0, Neptune=1, Uranus=2, Titania=3.
- `LevelOptions.MapIndex`/`ConfigData.Maps` is an index/order contract. The current authoritative order is **0 Pluto, 1 Neptune, 2 Titania, 3 Uranus**. Do not assume a `Locations` enum number equals a `MapIndex`.
- `Space.unity` serializes `Prefabs.Maps` as Pluto, Neptune, Titania, Uranus, matching current MapIndex order. `Stage.FinalizeSceneWithUserData()` calls `Prefabs.LoadConversions()` before `Pool.Setup()`; `Prefabs.NormalizeMapPrefabs()` reorders by prefab name and fails closed if a required map is missing, preventing inspector-order drift from silently changing map identity.
- `Pool` is also index-based and must mirror `ConfigData.Maps`: Pluto 0, Neptune 1, Titania 2, Uranus 3 for create/get/return paths.
- `UI_Components.Map.Create(...)` overwrites the prefab's serialized `Name`, `Index`, and starting positions from `ConfigData.Maps`; those runtime values are authoritative for a pooled map instance.

## Titania soundtrack analysis

Source runtime asset: `Resources/Music/Titania/Titania Source.mp3`. It preserves the Unity-generated AudioImporter GUID from the originally committed `Music/Titania/Titania, Uranus' Moon.mp3`; the uploaded `Titania, Uranus' Moon brass(1).mp3` is the same audio.

- Source duration is about **350.851 s** at 44.1 kHz stereo.
- Beat analysis places the track around **72.8 BPM**. The opening behaves as a one-time introduction; the reusable main body begins on the strong downbeat around 26.56 s.
- The production split was refined to nearby sample-matched points to reduce loop-seam discontinuity without changing the musical phrase:
  - intro: **0.000000–26.565215 s**
  - loop: **26.565215–185.461179 s**
  - loop duration: **158.895964 s**
- The source contains another traversal of essentially the same long body after that first loop region, followed by roughly **6.49 s** of ending material. That repeated composition is strong evidence that the 158.896 s section is the intended reusable body rather than an arbitrary cut.
- Titania keeps the full authored source as one Unity-imported resource and `TitaniaMusicBuilder` builds two runtime PCM `AudioClip`s at the analyzed sample boundaries. This provides the same one-shot-intro + true `AudioSource.loop` behavior as the other maps without re-encoding the loop or inventing `.meta` GUIDs.
- Because the source importer does not preload audio, `TitaniaMusicBuilder` explicitly loads audio data before `GetData`, copies samples in bounded chunks, and unloads the full source asset after constructing the intro/loop clips.
- `AudioController` creates Titania's intro and loop sources on dedicated child GameObjects, copies the existing music mixer/volume/priority settings, sets intro looping off and loop looping on, and leaves Human/Bees stems intentionally null.
