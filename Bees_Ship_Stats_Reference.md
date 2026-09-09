**ram.sql • ship-stats • version 5 • 24 entries**

**Source and scope.** Values are transcribed from settings row Id 16 (userId 0, version 5, name ship-stats) in ram.sql. The current Unity client declares ConfigData.Version = 5. This reference preserves the raw configured values and does not normalize or rebalance them. Supplemental gameplay-role notes at the end of this reference are explicitly identified and are not derived from the raw ship-stats row.

**Reading multi-weapon entries.** Slash-separated values in the core tables correspond by array index. The weapon-system tables expand those arrays into one row per slot. An em dash means the field is empty in the SQL data. ROF means RateOfFire; TSV is the explicit Tsv field stored in version 5.

# At-a-glance TSV

| **Human / support ship** | **TSV** | **Bee ship**  | **TSV** |
|--------------------------|---------|---------------|---------|
| Barge                    | 95      | Beehive       | 320     |
| Beacon                   | 1       | Bumblebee     | 237     |
| Carrier                  | 130     | Carpenter Bee | 170     |
| Cruiser                  | 150     | Honeybee      | 4       |
| Dreadnought              | 42      | Hornet        | 5       |
| Drone                    | 2       | Leafcutter    | 48      |
| Factory                  | 200     | Queen         | 770     |
| Fire Barge               | 320     | Wasp          | 16      |
| Flagship                 | 332     | Yellow Jacket | 11      |
| Frigate                  | 35      |               |         |
| Gunship                  | 18      |               |         |
| Human Target             | 5000000 |               |         |
| Scout                    | 7       |               |         |
| Striker                  | 15      |               |         |
| Warp Gate                | 150     |               |         |

# Core Ship Statistics

## Human / Human-Support Entries

| **Ship type** | **TSV** | **Health** | **Speed** | **Sight** | **Range**     | **Power**     | **Rate of fire** | **Rotation rate** |
|---------------|---------|------------|-----------|-----------|---------------|---------------|------------------|-------------------|
| Barge         | 95      | 2800       | 3.5       | 40        | 80            | 300           | 0                | 0                 |
| Beacon        | 1       | 25         | 0         | 60        | —             | —             | —                | —                 |
| Carrier       | 130     | 1300       | 4         | 40        | —             | —             | —                | —                 |
| Cruiser       | 150     | 400        | 6         | 0         | 120           | 150           | 2                | 180               |
| Dreadnought   | 42      | 550        | 9         | 0         | 80            | 125           | 2                | 150               |
| Drone         | 2       | 40         | 25.5      | 0         | 40            | 15            | 2                | 306               |
| Factory       | 200     | 850        | 4         | 40        | —             | —             | —                | —                 |
| Fire Barge    | 320     | 1800       | 5         | 60        | 0             | 2000          | 0                | 0                 |
| Flagship      | 332     | 1500       | 4         | 0         | 30 / 30 / 180 | 75 / 75 / 350 | 1 / 1 / 3        | 200 / 200 / 100   |
| Frigate       | 35      | 375        | 12        | 0         | 40 / 40       | 80 / 80       | 2 / 2            | 180 / 180         |
| Gunship       | 18      | 300        | 18.75     | 0         | 40            | 60            | 2.5              | 240               |
| Human Target  | 5000000 | 10000000   | 0         | 0         | —             | —             | —                | —                 |
| Scout         | 7       | 50         | 21        | 80        | —             | —             | —                | —                 |
| Striker       | 15      | 300        | 18.75     | 40        | 0             | 150           | 0                | 0                 |
| Warp Gate     | 150     | 1350       | 2         | 40        | —             | —             | —                | —                 |

## Bee Entries

| **Ship type** | **TSV** | **Health** | **Speed** | **Sight** | **Range**               | **Power**               | **Rate of fire**            | **Rotation rate**           |
|---------------|---------|------------|-----------|-----------|-------------------------|-------------------------|-----------------------------|-----------------------------|
| Beehive       | 320     | 2400       | 2         | 40        | —                       | —                       | —                           | —                           |
| Bumblebee     | 237     | 900        | 7         | 0         | 160                     | 200                     | 2.75                        | 180                         |
| Carpenter Bee | 170     | 600        | 2         | 40        | —                       | —                       | —                           | —                           |
| Honeybee      | 4       | 40         | 22.5      | 100       | —                       | —                       | —                           | —                           |
| Hornet        | 5       | 100        | 18        | 0         | 40                      | 20                      | 2                           | 288                         |
| Leafcutter    | 48      | 600        | 10.5      | 0         | 80                      | 60                      | 1.5                         | 250                         |
| Queen         | 770     | 7000       | 4         | 200       | 140 / 70 / 70 / 70 / 70 | 180 / 30 / 30 / 30 / 30 | 1.5 / 0.5 / 0.5 / 0.5 / 0.5 | 125 / 250 / 250 / 250 / 250 |
| Wasp          | 16      | 400        | 15        | 0         | 40                      | 80                      | 2.5                         | 240                         |
| Yellow Jacket | 11      | 200        | 19.5      | 40        | 0                       | 200                     | 0                           | 0                           |

# Weapon and Projectile Statistics

Every configured weapon/projectile field is expanded below. Ships with empty weapon arrays are retained so this section is complete.

## Human / Human-Support Weapon Systems

| **Ship type** | **Slot** | **Weapon type**  | **Range** | **Power** | **ROF** | **Rotation** | **Projectile value** | **Projectile type** | **Sound type**  |
|---------------|----------|------------------|-----------|-----------|---------|--------------|----------------------|---------------------|-----------------|
| Barge         |          | Bomb             | 80        | 300       | 0       | 0            | 0.5                  | None                | None            |
| Beacon        |          | —                | —         | —         | —       | —            | —                    | —                   | —               |
| Carrier       |          | —                | —         | —         | —       | —            | —                    | —                   | —               |
| Cruiser       |          | Beam Cannon      | 120       | 150       | 2       | 180          | 1.75                 | None                | Beam Cannon     |
| Dreadnought   |          | Light Cannon     | 80        | 125       | 2       | 150          | 1                    | Human Medium        | Light Cannon    |
| Drone         |          | Turret           | 40        | 15        | 2       | 306          | 1                    | Human Small         | Small Laser     |
| Factory       |          | —                | —         | —         | —       | —            | —                    | —                   | —               |
| Fire Barge    |          | Bomb             | 0         | 2000      | 0       | 0            | 1.5                  | None                | Fire Barge Bomb |
| Flagship      | 1        | Turret           | 30        | 75        | 1       | 200          | 1                    | Human Small         | Small Laser     |
| Flagship      | 2        | Turret           | 30        | 75        | 1       | 200          | 1                    | Human Small         | Small Laser     |
| Flagship      | 3        | Full Ship Turret | 180       | 350       | 3       | 100          | 1.75                 | Flagship Shot       | Flagship Laser  |
| Frigate       | 1        | Rocket Turret    | 40        | 80        | 2       | 180          | 2                    | Rocket              | Rocket Launch   |
| Frigate       | 2        | Rocket Turret    | 40        | 80        | 2       | 180          | 2                    | Rocket              | Rocket Launch   |
| Gunship       |          | Dual Cannon      | 40        | 60        | 2.5     | 240          | 1                    | Human Small         | Small Laser     |
| Human Target  |          | —                | —         | —         | —       | —            | —                    | —                   | —               |
| Scout         |          | —                | —         | —         | —       | —            | —                    | —                   | —               |
| Striker       |          | Bomb             | 0         | 150       | 0       | 0            | 1                    | None                | Bomb            |
| Warp Gate     |          | —                | —         | —         | —       | —            | —                    | —                   | —               |

## Bee Weapon Systems

| **Ship type** | **Slot** | **Weapon type** | **Range** | **Power** | **ROF** | **Rotation** | **Projectile value** | **Projectile type** | **Sound type** |
|---------------|----------|-----------------|-----------|-----------|---------|--------------|----------------------|---------------------|----------------|
| Beehive       |          | —               | —         | —         | —       | —            | —                    | —                   | —              |
| Bumblebee     |          | Turret          | 160       | 200       | 2.75    | 180          | 1.5                  | Bumblebee Shot      | Bowtie Laser   |
| Carpenter Bee |          | —               | —         | —         | —       | —            | —                    | —                   | —              |
| Honeybee      |          | —               | —         | —         | —       | —            | —                    | —                   | —              |
| Hornet        |          | Eye             | 40        | 20        | 2       | 288          | 1                    | Bee Small           | Small Laser    |
| Leafcutter    |          | Split Shot      | 80        | 60        | 1.5     | 250          | 1.5                  | Split Shot          | Big Laser      |
| Queen         | 1        | Eye             | 140       | 180       | 1.5     | 125          | 1                    | Queen Large         | Queen Laser    |
| Queen         | 2        | Queen Eye       | 70        | 30        | 0.5     | 250          | 1                    | Queen Small         | Small Laser    |
| Queen         | 3        | Queen Eye       | 70        | 30        | 0.5     | 250          | 1                    | Queen Small         | Small Laser    |
| Queen         | 4        | Queen Eye       | 70        | 30        | 0.5     | 250          | 1                    | Queen Small         | Small Laser    |
| Queen         | 5        | Queen Eye       | 70        | 30        | 0.5     | 250          | 1                    | Queen Small         | Small Laser    |
| Wasp          |          | Eye             | 40        | 80        | 2.5     | 240          | 1                    | Bee Medium          | Big Laser      |
| Yellow Jacket |          | Bomb            | 0         | 200       | 0       | 0            | 1                    | None                | None           |

# Reference Notes

- Beacon and Human Target are included because they are explicit ShipType entries in the version-5 ship-stats record.

- Human Target is an extreme-value target entry (10,000,000 health; TSV 5,000,000) and is best treated separately from ordinary balance comparisons.

- The document includes every mechanical field present for each version-5 entry: Health, Range, Power, RateOfFire, RotationRates, Speed, Sight, Tsv, ProjectileValue, ProjectileTypes, WeaponSoundTypes, and WeaponTypes.

- Descriptions and CodexDescription prose are intentionally omitted because they are explanatory text rather than ship statistics.

# Supplemental Gameplay-Role Notes

The following notes describe gameplay behavior/roles that are not represented by the raw `ship-stats` fields above.

- **Yellow Jacket** — A swarm-oriented suicide bomber. It is intended to operate as part of a swarm and detonate on/near enemies, so isolated 1v1 performance is not a representative measure of its intended effectiveness.
- **Barge** — Has no guns. It can use **Barge Charge** to collide/charge into other ships, damaging both itself and the ships it hits.
- **Fire Barge** — Has no conventional gun attack. It can **self-detonate**, or detonate when killed, producing a massive area-of-effect explosion that destroys/damages the Fire Barge itself and can also hit nearby enemy **and friendly** ships. Its effectiveness therefore depends heavily on positioning, proximity, and multi-ship context.

