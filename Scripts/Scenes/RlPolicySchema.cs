using Assets.Scripts;
using Assets.Scripts.Entities.Ships;
using System;
using System.Collections.Generic;

/// <summary>
/// Frozen neural-policy ABI. A checkpoint is compatible only while this contract remains identical.
/// Curriculum, rewards, maps, matchup distributions and trainer hyperparameters may evolve without
/// changing this schema; observation/action meaning, ordering, normalization and capacities may not.
/// </summary>
internal static class RlPolicySchema
{
    internal const int Version = 2;
    internal const int ExpectedObservationSize = 4685;
    internal const int ExpectedContinuousActions = 4;
    internal const int ExpectedWeaponCommandBranchSize = 33;
    internal const int ExpectedSpecialActionBranchSize = 5;
    internal const int ExpectedAllyTargetBranchSize = 65;
    internal const int ExpectedEnemyTargetBranchSize = 65;
    internal const int ExpectedMapObjectTargetBranchSize = 65;

    internal const string Signature =
        "bees-rl-v2|obs=4685|cont=4|disc=33,5,65,65,65|shipbits=6|weaponbits=6|mapbits=4|" +
        "allies=64|enemies=64|weapons=16|enemy-mounts=16|mining=8|map-objects=64|moving-asteroids=48|" +
        "self=29|capability=12|parent-carrier=19|entity=19|weapon=19|enemy-mount=22|mining-slot=7|" +
        "map-slot=12|moving-asteroid-slot=11|objective=16|grid=13x13|entity-order=distance,type,fleet-id,runtime-id";

    internal static void ValidateOrThrow()
    {
        List<string> errors = new List<string>();
        Check(errors, RlCombatPerception.ObservationSize, ExpectedObservationSize, "observation size");
        Check(errors, RlOneVsOneAgent.ContinuousActionCount, ExpectedContinuousActions, "continuous actions");
        Check(errors, RlOneVsOneAgent.WeaponCommandBranchSize, ExpectedWeaponCommandBranchSize, "weapon branch");
        Check(errors, RlOneVsOneAgent.SpecialActionBranchSize, ExpectedSpecialActionBranchSize, "special branch");
        Check(errors, RlOneVsOneAgent.AllyTargetBranchSize, ExpectedAllyTargetBranchSize, "ally target branch");
        Check(errors, RlOneVsOneAgent.EnemyTargetBranchSize, ExpectedEnemyTargetBranchSize, "enemy target branch");
        Check(errors, RlOneVsOneAgent.MapObjectTargetBranchSize, ExpectedMapObjectTargetBranchSize, "map-object target branch");

        Check(errors, RlCombatPerception.ShipTypeBitCount, 6, "ship type bits");
        Check(errors, RlCombatPerception.WeaponTypeBitCount, 6, "weapon type bits");
        Check(errors, RlCombatPerception.MapObjectTypeBitCount, 4, "map-object type bits");
        Check(errors, RlCombatPerception.MaxObservedAllies, 64, "ally slots");
        Check(errors, RlCombatPerception.MaxObservedEnemies, 64, "enemy slots");
        Check(errors, RlCombatPerception.MaxWeaponSlots, 16, "weapon slots");
        Check(errors, RlCombatPerception.MaxObservedEnemyWeaponMounts, 16, "enemy weapon-mount slots");
        Check(errors, RlCombatPerception.ObjectiveObservationSize, 16, "objective channels");
        Check(errors, RlCombatPerception.NavigationGridSize, 13, "navigation grid width");

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
