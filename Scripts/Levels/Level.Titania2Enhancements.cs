using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    public partial class Level
    {
        internal void StartTitania2Enhancements(HumanTarget titania)
        {
            if (titania == null || _titania2Resolved)
            {
                return;
            }

            // Titania II owns its own target durability. Pluto IV also uses HumanTarget, so keep
            // this mission-specific rather than changing the shared prefab or CreateHumanTarget().
            titania.MaxHealth = 10000;
            titania.Health = 10000;
            titania.OriginalHealth = 10000;

            // Titania II uses the authored Planetary Shield HUD exactly as labeled. Only its fill
            // source changes to Titania's health; Pluto IV keeps its own mission-specific setup.
            Stage.Menus.Counter.SetActive(false);
            Stage.Menus.PlutoShield.SetActive(true);
            UpdateTitania2BaseHealth(titania, Stage.Menus.PlutoShieldHealthBar);

            ScaledTimer baseHealthTimer = new ScaledTimer();
            baseHealthTimer.Reuse(0.25f, () =>
            {
                if (!_titania2Resolved && titania != null && !titania.IsDead)
                {
                    UpdateTitania2BaseHealth(titania, Stage.Menus.PlutoShieldHealthBar);
                }
            }, true);
            AddTitania2Timer(baseHealthTimer);

            // Mirror every authored attack after a short interval. The reduced compositions match
            // the roughly 30% Titania II difficulty reduction applied to the primary wave schedule.
            ScheduleTitania2ExtraWave(15f, -1f, -0.65f,
                (ConfigData.ShipTypes.Hornet, 3));
            ScheduleTitania2ExtraWave(20f, 1f, -0.85f,
                (ConfigData.ShipTypes.Wasp, 3));
            ScheduleTitania2ExtraWave(25f, -0.65f, -1f,
                (ConfigData.ShipTypes.Honeybee, 1));
            ScheduleTitania2ExtraWave(30f, -1f, 0.65f,
                (ConfigData.ShipTypes.Leafcutter, 1));

            ScheduleTitania2ExtraWave(75f, -1f, -0.7f,
                (ConfigData.ShipTypes.Hornet, 4),
                (ConfigData.ShipTypes.Wasp, 3));
            ScheduleTitania2ExtraWave(135f, 1f, -0.85f,
                (ConfigData.ShipTypes.Honeybee, 1),
                (ConfigData.ShipTypes.YellowJacket, 3));
            ScheduleTitania2ExtraWave(225f, -1f, 0.65f,
                (ConfigData.ShipTypes.Leafcutter, 3),
                (ConfigData.ShipTypes.Hornet, 6));
            ScheduleTitania2ExtraWave(315f, -0.55f, -1f,
                (ConfigData.ShipTypes.Wasp, 4),
                (ConfigData.ShipTypes.YellowJacket, 4));
            ScheduleTitania2ExtraWave(390f, -1f, 0.7f,
                (ConfigData.ShipTypes.Leafcutter, 3),
                (ConfigData.ShipTypes.Hornet, 6));
            ScheduleTitania2ExtraWave(405f, 0.55f, -1f,
                (ConfigData.ShipTypes.Honeybee, 2),
                (ConfigData.ShipTypes.YellowJacket, 4));
        }

        private static void UpdateTitania2BaseHealth(HumanTarget titania, GameObject healthBar)
        {
            float fraction = titania.MaxHealth <= 0
                ? 0f
                : Mathf.Clamp01((float)titania.Health / titania.MaxHealth);
            healthBar.transform.localScale = new Vector2(fraction * 150f, 1f);
        }

        private void ScheduleTitania2ExtraWave(
            float delay,
            float normalizedX,
            float normalizedY,
            params (ConfigData.ShipTypes type, int shipCount)[] composition)
        {
            ScaledTimer timer = new ScaledTimer(delay, () =>
            {
                if (_titania2Resolved)
                {
                    return;
                }

                List<SavedSquad> squads = new List<SavedSquad>();
                foreach (var requested in composition)
                {
                    SavedSquad squad = ConfigData.CurrentShips.GetSquadByComposition(
                        this, requested.type, requested.shipCount, true, true);
                    if (squad != null)
                    {
                        squads.Add(squad);
                    }
                }

                AddTitania2BeeWave(squads, normalizedX, normalizedY);
                AddReinforcementsToHivemindCommandQueue();
            });
            AddTitania2Timer(timer);
        }
    }

    [DefaultExecutionOrder(1600)]
    internal sealed class Titania2EnhancementGuard : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            GameObject host = new GameObject("Titania II Enhancement Guard");
            DontDestroyOnLoad(host);
            host.AddComponent<Titania2EnhancementGuard>();
        }

        private void Update()
        {
            if (ConfigData.CurrentGameMode != ConfigData.GameModes.Campaign)
            {
                return;
            }

            foreach (Level level in FindObjectsOfType<Level>())
            {
                if (level == null || level.CurrentLevelOptions == null || level.CurrentLevelOptions.Id != 8 ||
                    level.Stage == null || level.Stage.Menus == null)
                {
                    continue;
                }

                Titania2EnhancementMarker marker = level.GetComponent<Titania2EnhancementMarker>();
                if (marker != null)
                {
                    if (level.State.GameOver || !level.IsLevelConnectedToServer)
                    {
                        level.Stage.Menus.PlutoShield.SetActive(false);
                    }
                    continue;
                }

                if (level.Stage.Menus.Clock == null || !level.Stage.Menus.Clock.activeInHierarchy)
                {
                    continue;
                }

                HumanTarget titania = level.State.GetShips()
                    .OfType<HumanTarget>()
                    .FirstOrDefault(ship => !ship.IsDead);
                if (titania == null)
                {
                    continue;
                }

                level.StartTitania2Enhancements(titania);
                level.gameObject.AddComponent<Titania2EnhancementMarker>();
            }
        }
    }

    internal sealed class Titania2EnhancementMarker : MonoBehaviour
    {
    }
}
