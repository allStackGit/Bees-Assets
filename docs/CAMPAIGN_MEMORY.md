# Campaign memory

Compact repository-learning notes for campaign development. Detailed planned mission intent lives in `docs/CAMPAIGN_DESIGN_GUIDE.md`.

## Source priority

- `docs/CAMPAIGN_DESIGN_GUIDE.md` is **guiding, non-authoritative** planning information supplied by the developer. Use it to interpret unfinished work and future consequences, but never silently override current runtime code, authored maps/obstacle prefabs, dialogue, or server-backed campaign data.
- The developer-supplied **Mission Scripting** document is the preferred dialogue/script authority when it gives a more coherent version of a currently implemented scene. Mechanics still come from runtime code/assets. In particular, its Titania arrival story and Titania-to-Uranus intermission better explain the current campaign than the older compressed/obsolete dialogue paths.
- `SaveData/` is local/test user state and is never design authority.
- Current implemented mission identity is reconstructed from `CampaignMissionCatalog`, `LevelIntro`, level data, mission triggers, exact maps/obstacle prefabs, ship mechanics, and dialogue together.

## Durable campaign design model

- Campaign failure is generally intended to move the war forward with consequences rather than behave as a simple retry screen. Planned consequences include skipping missions, strengthening later Bee forces, losing resources/ships/intelligence, making the next encounter harder, and reducing information available during later preparation.
- Side objectives and mission performance are intended to compound across the campaign. The planned final Earth mission can change objective based on earlier side objectives and intelligence.
- The current scripted Pluto/Neptune/Titania/Uranus arc is only an early portion of the planned campaign. The design outline continues through Saturn, Jupiter, Europa, the Asteroid Belt, Mars, Venus/Sun, Moon, and Earth. Uranus `A New Threat` is therefore a temporary implementation endpoint, not the intended campaign ending.
- The guiding outline calls Titania mission 1 **Invaluable Time**; current runtime/catalog terminology is **Minesweeper**. Treat Invaluable Time as design intent for the mission (clear patrols/gain time for Titania to restart systems and evacuate), while the authored Minesweeper obstacle/Fire-Tank demolition maze defines the current tactical implementation.
- Guiding Minesweeper/Invaluable Time failure consequence: Bee-noculars should start materially harder and some resources that could have been gained there should be lost. This is planning guidance only until represented in runtime state.

## Active campaign mission split

- `LeveLTriggers.cs` is a legacy campaign monolith. New campaign fixes should be extracted **mission-by-mission into `Level` partial files**, not performed through GitHub Actions source-edit workflows and not split by arbitrary line count. `CampaignMissionCatalog` selects the active implementation, allowing legacy methods to remain temporarily unreachable while they are migrated safely.
- Current extracted active methods include `Neptune3PressingForwardCampaign()`, `Titania1MinesweeperCampaign()`, and `Titania2BeenocularsCampaign()`.
- Source-inspection tests must combine the `Scripts/Levels/*.cs` partial sources instead of assuming all campaign methods live in `LeveLTriggers.cs`.
- Do not use GitHub Actions as a remote source editor. The temporary exact-string editing workflows were brittle against source drift and introduced unnecessary Actions-token/branch-permission failure modes. Source edits should be ordinary commits to logically sized files. Do not execute tests/workflows without explicit developer permission.

## Dialogue architecture and indexing

- `CutsceneManager.Setup()` constructs flat `List<DialogueLine>` collections, while mission code frequently selects dialogue by hard-coded `GetRange(start, count)` indices. **Inserting or deleting a line inside one of those lists can silently invalidate multiple triggers.** Prefer named helper sections/overrides or migrate a mission to a small partial before changing indexed structure.
- `CampaignDialogueOverrides` supplies dialogue corrections that do not need to mutate the large `CutsceneManager` initializer. It currently provides the Mission-Scripting version of Neptune-to-Titania and the newly recovered Titania-to-Uranus intermission.
- Reinforcements has a two-line pre-mission briefing; both lines must be shown before runtime starts at dialogue index 2.
- Pressing Forward failure dialogue has two unconditional opening lines, an optional Factory-abandonment exchange, and an unconditional final `Full speed ahead!`; do not implement the no-Factory path as one contiguous two-line slice.
- Minesweeper dialogue index contract: `0` briefing; `1-11` first Fire Tank discovery tutorial; `12-15` first demolition follow-up; `16-20` success; `21-30` failure.
- Bee-noculars dialogue index contract: `0` briefing; `1-10` mission start; `11` optional harder-start line after prior failure; `12-25` upload updates; `26-30` abort/failure; `31-32` success. The older trigger method used incorrect ranges; active `Titania2BeenocularsCampaign()` uses the corrected slices.
- The Mission Scripting document contains a real Titania-to-Uranus intermission. The previous repository-memory conclusion that no such transition had been authored was wrong. Its A.M.I. subsection is conditional on successful Bee-noculars, but `UserProgressData` currently has no persisted A.M.I./Bee-noculars-success flag. Do not play that conditional section unconditionally; add explicit outcome state when Titania 2 progression is completed.

## Active Minesweeper implementation

- Mission 7 dispatches through `Titania1MinesweeperCampaign()` in `Scripts/Levels/Titania1Minesweeper.cs`; completion/terminal dispatch is `Titania1MinesweeperEnding()`.
- This active path deliberately does **not** manufacture/save the old temporary 7 Wasp + 8 Hornet + 2 Leafcutter persistent Bee fleet on mission entry. It uses existing campaign fleet state for its patrol compositions.
- The first visible Fire Tank now restores the intended tutorial dialogue (`1-11`); destroying that tank triggers the follow-up (`12-15`). It had only been disabled temporarily for fast testing, not removed from the design.
- The active ending cancels its 90-second reinforcement timer, closes gameplay, lets the win/loss dialogue complete, then adds `State.PlayerScore` to campaign score, advances the campaign, saves user/squad/fleet state, sets `GameOver`, and shows the level summary.
- The older `Titania1Minesweeper()` body remains in `LeveLTriggers.cs` as unreachable legacy source because whole-file remote replacement of the monolith is unnecessarily risky. Do not route mission 7 back to it; it contains obsolete test-only persistent Bee seeding.
