# Campaign memory

Compact repository-learning notes for campaign development. Detailed planned mission intent lives in `docs/CAMPAIGN_DESIGN_GUIDE.md`.

## Source priority

- `docs/CAMPAIGN_DESIGN_GUIDE.md` is **guiding, non-authoritative** planning information supplied by the developer. Use it to interpret unfinished work and future consequences, but never silently override current runtime code, authored maps/obstacle prefabs, dialogue, or server-backed campaign data.
- `SaveData/` is local/test user state and is never design authority.
- Current implemented mission identity is reconstructed from `CampaignMissionCatalog`, `LevelIntro`, level data, mission triggers, exact maps/obstacle prefabs, ship mechanics, and dialogue together.

## Durable campaign design model

- Campaign failure is generally intended to move the war forward with consequences rather than behave as a simple retry screen. Planned consequences include skipping missions, strengthening later Bee forces, losing resources/ships/intelligence, making the next encounter harder, and reducing information available during later preparation.
- Side objectives and mission performance are intended to compound across the campaign. The planned final Earth mission can change objective based on earlier side objectives and intelligence.
- The current scripted Pluto/Neptune/Titania/Uranus arc is only an early portion of the planned campaign. The design outline continues through Saturn, Jupiter, Europa, the Asteroid Belt, Mars, Venus/Sun, Moon, and Earth. Uranus `A New Threat` is therefore a temporary implementation endpoint, not the intended campaign ending.
- The guiding outline calls Titania mission 1 **Invaluable Time**; current runtime/catalog terminology is **Minesweeper**. Treat Invaluable Time as design intent for the mission (clear patrols/gain time for Titania to restart systems and evacuate), while the authored Minesweeper obstacle/Fire-Tank demolition maze defines the current tactical implementation.
- Guiding Minesweeper/Invaluable Time failure consequence: Bee-noculars should start materially harder and some resources that could have been gained there should be lost. This is planning guidance only until represented in runtime state.

## Active Minesweeper implementation

- Mission 7 now dispatches through `Titania1MinesweeperCampaign()` in `Scripts/Levels/Titania1Minesweeper.cs`; completion/terminal dispatch is `Titania1MinesweeperEnding()`.
- This active path deliberately does **not** manufacture/save the old temporary 7 Wasp + 8 Hornet + 2 Leafcutter persistent Bee fleet on mission entry. It uses existing campaign fleet state for its patrol compositions.
- The active ending cancels its 90-second reinforcement timer, closes gameplay, lets the win/loss dialogue complete, then adds `State.PlayerScore` to campaign score, advances the campaign, saves user/squad/fleet state, sets `GameOver`, and shows the level summary.
- The older `Titania1Minesweeper()` body remains in `LeveLTriggers.cs` as unreachable legacy source because the GitHub connector has no safe surgical edit for that very large file. Do not route mission 7 back to it; it contains obsolete test-only persistent Bee seeding.
- Fire Tank discovery/tutorial dialogue remains intentionally disabled only to speed current testing; it is still intended gameplay onboarding and should be restored when normal mission pacing returns.
