# Carrier deck variants

Carrier deck artwork is a visual derivative of the persisted squad color; it is not separately persisted or sent to BeesServer. `SavedSquad.Color` remains the single source of truth, so a saved squad reconstructs the same deck on another device without a save/schema change.

`Scripts/Entities/Ships/CarrierDeckVariants.cs` owns color-to-deck matching and runtime slicing. The authored sheet is `Sprites/Ships/Parts/carrier_alts_deck.png`: 672x224 pixels, arranged as 7 columns by 2 rows of 96x112 cells. Thirteen cells are used (top row left-to-right, then the first six cells of the bottom row); the final bottom-right cell is blank.

Runtime loading uses the identical PNG bytes stored as `Resources/Sprites/carrier_alts_deck.bytes`. Loading as raw bytes and decoding with `ImageConversion.LoadImage` preserves the authored 672x224 dimensions independently of Unity texture-import NPOT settings. Do not replace that resource with an imported Texture2D unless its import contract is made explicit and validated.

Carrier deck matching uses bounded HSV neighborhoods around each authored accent color. An unmatched squad color keeps the normal carrier deck. When matched, the normal squad recolor still runs first and the selected deck is rendered as a transparent overlay. `Carrier.SetColor()` owns the in-level overlay; `DragIcon.SetColor()` owns the live Squad Maker overlay. Because saved-squad loading calls `DragIcon.SetColor(savedColor)`, the deck is reconstructed when a squad is reopened as well as when the picker changes.

`Tests/EditMode/CarrierDeckVariantsTests.cs` protects the 13 color anchors, optional/unmatched behavior, 96x112 slicing geometry, and raw resource decoding.
