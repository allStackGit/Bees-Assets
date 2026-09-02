using Assets.Scripts;
using Assets.Scripts.Entities.Ships;
using System;
using System.Collections.Generic;

/// <summary>
/// Frozen neural-policy ABI. A checkpoint is compatible only while this contract remains identical.
/// Curriculum, rewards, maps, matchup distributions and non-architectural trainer hyperparameters
/// may evolve without changing this schema; observation/action meaning, ordering, normalization,
/// capacities, behavior identity and network architecture may not.
/// </summary>
internal static class RlPolicySchema
{
    internal const int Version = 3;
    internal const string ExpectedBehaviorName = "BeesRL1v1";
    internal const int ExpectedObservationSize = 4685;
    internal const int ExpectedContinuousActions = 34;
    internal const int ExpectedWeaponFireBranchCount = 16;
    internal const int ExpectedWeaponFireBranchSize = 2;
    internal const int ExpectedDiscreteBranchCount = 20;
    internal const int ExpectedSpecialActionBranchSize = 5;
    internal const int ExpectedAllyTargetBranchSize = 65;
    internal const int ExpectedEnemyTargetBranchSize = 65;
    internal const int ExpectedMapObjectTargetBranchSize = 65;

    internal const string Signature =
        "bees-rl-v3|behavior=BeesRL1v1|network=ff-128x2|normalize=true|obs=4685|cont=34|disc=2x16,5,65,65,65|" +
        "weapon-aim=slotwise-xy|weapon-fire=slotwise-cease-or-fire|" +
        "shipbits=6|weaponbits=6|mapbits=4|shipmap=v1-0..23|weaponmap=v1-0..9|" +
        "allies=64|enemies=64|weapons=16|enemy-mounts=16|mining=8|map-objects=64|moving-asteroids=48|" +
        "self=29|capability=12|parent-carrier=19|entity=19|weapon=19|enemy-mount=22|mining-slot=7|" +
        "map-slot=12|moving-asteroid-slot=11|objective=16|grid=13x13|entity-order=distance,type,fleet-id,runtime-id";

    internal static void ValidateOrThrow()
    {
        List<string> errors = new List<string>();
        if (!string.Equals(RlOneVsOneAgent.BehaviorName, ExpectedBehaviorName, StringComparison.Ordinal))
        {
            errors.Add($"behavior expected {ExpectedBehaviorName} but was {RlOneVsOneAgent.BehaviorName}");
        }

        Check(errors, RlCombatPerception.ObservationSize, ExpectedObservationSize, "observation size");
        Check(errors, RlOneVsOneAgent.ContinuousActionCount, ExpectedContinuousActions, "continuous actions");
        Check(errors, RlOneVsOneAgent.WeaponFireBranchCount, ExpectedWeaponFireBranchCount, "weapon fire branch count");
        Check(errors, RlOneVsOneAgent.WeaponFireBranchSize, ExpectedWeaponFireBranchSize, "weapon fire branch size");
        Check(errors, RlOneVsOneAgent.DiscreteBranchCount, ExpectedDiscreteBranchCount, "discrete branch count");
        Check(errors, RlOneVsOneAgent.SpecialActionBranchSize, ExpectedSpecialActionBranchSize, "special branch");
        Check(errors, RlOneVsOneAgent.AllyTargetBranchSize, ExpectedAllyTargetBranchSize, "ally target branch");
        Check(errors, RlOneVsOneAgent.EnemyTargetBranchSize, ExpectedEnemyTargetBranchSize, "enemy target branch");
        Check(errors, RlOneVsOneAgent.MapObjectTargetBranchSize, ExpectedMapObjectTargetBranchSize, "map-object target branch");
        ValidateDiscreteBranchSizes(errors);

        Check(errors, RlCombatPerception.ShipTypeBitCount, 6, "ship type bits");
        Check(errors, RlCombatPerception.WeaponTypeBitCount, 6, "weapon type bits");
        Check(errors, RlCombatPerception.MapObjectTypeBitCount, 4, "map-object type bits");
        Check(errors, RlCombatPerception.MaxObservedAllies, 64, "ally slots");
        Check(errors, RlCombatPerception.MaxObservedEnemies, 64, "enemy slots");
        Check(errors, RlCombatPerception.MaxWeaponSlots, 16, "weapon slots");
        Check(errors, RlCombatPerception.MaxObservedEnemyWeaponMounts, 16, "enemy weapon-mount slots");
        Check(errors, RlCombatPerception.ObjectiveObservationSize, 16, "objective channels");
        Check(errors, RlCombatPerception.NavigationGridSize, 13, "navigation grid width");

        ValidateFrozenEnumMappings(errors);
        ValidateEnumRange<ConfigData.ShipTypes>(errors, RlCombatPerception.ShipTypeBitCount, "ship type");
        ValidateEnumRange<ConfigData.WeaponTypes>(errors, RlCombatPerception.WeaponTypeBitCount, "weapon type");

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "RL policy ABI v" + Version + " no longer matches its frozen contract: " +
                string.Join("; ", errors));
        }
    }

    internal static bool TryValidateShip(Ship ship, out string error)
    {
        if (ship == null)
        {
            error = "RL policy cannot bind a null ship.";
            return false;
        }

        int shipType = (int)ship.ShipType;
        int shipTypeLimit = 1 << RlCombatPerception.ShipTypeBitCount;
        if (shipType < 0 || shipType >= shipTypeLimit)
        {
            error = $"RL policy ABI cannot encode ship type {ship.ShipType} ({shipType}); " +
                    $"the frozen {RlCombatPerception.ShipTypeBitCount}-bit field supports 0-{shipTypeLimit - 1}.";
            return false;
        }

        if (ship.Weapons != null && ship.Weapons.Count > RlCombatPerception.MaxWeaponSlots)
        {
            error = $"RL policy ABI cannot control {ship.ShipType}: it has {ship.Weapons.Count} authored weapon slots, " +
                    $"but the frozen policy supports {RlCombatPerception.MaxWeaponSlots}. " +
                    "Increase the ABI before canonical training rather than aliasing excess weapons.";
            return false;
        }

        if (ship.Weapons != null)
        {
            int weaponTypeLimit = 1 << RlCombatPerception.WeaponTypeBitCount;
            for (int i = 0; i < ship.Weapons.Count; i++)
            {
                if (ship.Weapons[i] == null)
                {
                    continue;
                }
                int weaponType = (int)ship.Weapons[i].Type;
                if (weaponType < 0 || weaponType >= weaponTypeLimit)
                {
                    error = $"RL policy ABI cannot encode weapon type {ship.Weapons[i].Type} ({weaponType}) on " +
                            $"{ship.ShipType}; the frozen {RlCombatPerception.WeaponTypeBitCount}-bit field supports " +
                            $"0-{weaponTypeLimit - 1}.";
                    return false;
                }
            }
        }

        error = null;
        return true;
    }

    private static void ValidateDiscreteBranchSizes(List<string> errors)
    {
        int[] branchSizes = RlOneVsOneAgent.CreateDiscreteBranchSizes();
        Check(errors, branchSizes.Length, ExpectedDiscreteBranchCount, "discrete branch array length");
        if (branchSizes.Length != ExpectedDiscreteBranchCount)
        {
            return;
        }

        for (int slot = 0; slot < ExpectedWeaponFireBranchCount; slot++)
        {
            Check(errors, branchSizes[slot], ExpectedWeaponFireBranchSize, $"weapon fire branch {slot}");
        }
        Check(errors, branchSizes[RlOneVsOneAgent.SpecialActionBranch], ExpectedSpecialActionBranchSize, "special branch array entry");
        Check(errors, branchSizes[RlOneVsOneAgent.AllyTargetBranch], ExpectedAllyTargetBranchSize, "ally target branch array entry");
        Check(errors, branchSizes[RlOneVsOneAgent.EnemyTargetBranch], ExpectedEnemyTargetBranchSize, "enemy target branch array entry");
        Check(errors, branchSizes[RlOneVsOneAgent.MapObjectTargetBranch], ExpectedMapObjectTargetBranchSize, "map-object target branch array entry");
    }

    private static void ValidateFrozenEnumMappings(List<string> errors)
    {
        // Existing identities are part of the policy vocabulary. New enum values may be appended
        // within the reserved bit range, but existing values must never be renumbered for ABI v3.
        CheckEnum(errors, ConfigData.ShipTypes.Barge, 0, "ship");
        CheckEnum(errors, ConfigData.ShipTypes.Beacon, 1, "ship");
        CheckEnum(errors, ConfigData.ShipTypes.Beehive, 2, "ship");
        CheckEnum(errors, ConfigData.ShipTypes.Bumblebee, 3, "ship");
        CheckEnum(errors, ConfigData.ShipTypes.CarpenterBee, 4, "ship");
        CheckEnum(errors, ConfigData.ShipTypes.Carrier, 5, "ship");
        CheckEnum(errors, ConfigData.ShipTypes.Cruiser, 6, "ship");
        CheckEnum(errors, ConfigData.ShipTypes.Dreadnought, 7, "ship");
        CheckEnum(errors, ConfigData.ShipTypes.Drone, 8, "ship");
        CheckEnum(errors, ConfigData.ShipTypes.Factory, 9, "ship");
        CheckEnum(errors, ConfigData.ShipTypes.FireBarge, 10, "ship");
        CheckEnum(errors, ConfigData.ShipTypes.Flagship, 11, "ship");
        CheckEnum(errors, ConfigData.ShipTypes.Frigate, 12, "ship");
        CheckEnum(errors, ConfigData.ShipTypes.Gunship, 13, "ship");
        CheckEnum(errors, ConfigData.ShipTypes.Honeybee, 14, "ship");
        CheckEnum(errors, ConfigData.ShipTypes.Hornet, 15, "ship");
        CheckEnum(errors, ConfigData.ShipTypes.Leafcutter, 16, "ship");
        CheckEnum(errors, ConfigData.ShipTypes.Queen, 17, "ship");
        CheckEnum(errors, ConfigData.ShipTypes.Scout, 18, "ship");
        CheckEnum(errors, ConfigData.ShipTypes.Striker, 19, "ship");
        CheckEnum(errors, ConfigData.ShipTypes.WarpGate, 20, "ship");
        CheckEnum(errors, ConfigData.ShipTypes.Wasp, 21, "ship");
        CheckEnum(errors, ConfigData.ShipTypes.YellowJacket, 22, "ship");
        CheckEnum(errors, ConfigData.ShipTypes.HumanTarget, 23, "ship");

        CheckEnum(errors, ConfigData.WeaponTypes.Bomb, 0, "weapon");
        CheckEnum(errors, ConfigData.WeaponTypes.BeamCannon, 1, "weapon");
        CheckEnum(errors, ConfigData.WeaponTypes.LightCannon, 2, "weapon");
        CheckEnum(errors, ConfigData.WeaponTypes.Turret, 3, "weapon");
        CheckEnum(errors, ConfigData.WeaponTypes.FullShipTurret, 4, "weapon");
        CheckEnum(errors, ConfigData.WeaponTypes.RocketTurret, 5, "weapon");
        CheckEnum(errors, ConfigData.WeaponTypes.DualCannon, 6, "weapon");
        CheckEnum(errors, ConfigData.WeaponTypes.Eye, 7, "weapon");
        CheckEnum(errors, ConfigData.WeaponTypes.QueenEye, 8, "weapon");
        CheckEnum(errors, ConfigData.WeaponTypes.SplitShot, 9, "weapon");
    }

    private static void CheckEnum<T>(List<string> errors, T value, int expected, string label) where T : Enum
    {
        int actual = Convert.ToInt32(value);
        if (actual != expected)
        {
            errors.Add($"{label} enum {value} expected id {expected} but was {actual}");
        }
    }

    private static void Check(List<string> errors, int actual, int expected, string label)
    {
        if (actual != expected)
        {
            errors.Add($"{label} expected {expected} but was {actual}");
        }
    }

    private static void ValidateEnumRange<T>(List<string> errors, int bits, string label) where T : Enum
    {
        int limit = 1 << bits;
        Array values = Enum.GetValues(typeof(T));
        for (int i = 0; i < values.Length; i++)
        {
            int value = Convert.ToInt32(values.GetValue(i));
            if (value < 0 || value >= limit)
            {
                errors.Add($"{label} enum value {values.GetValue(i)}={value} exceeds {bits}-bit range 0-{limit - 1}");
            }
        }
    }
}
