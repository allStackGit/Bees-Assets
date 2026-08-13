using System;
using System.Collections.Generic;
using System.Linq;
using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    /// <summary>
    /// Pure campaign objective rules shared by mission trigger graphs and scenario
    /// tests. Keeping winner resolution independent of UI, dialogue, and persistence
    /// makes objective behavior deterministic and reusable by future level tooling.
    /// </summary>
    public static class CampaignObjectiveRules
    {
        public static int ResolveEliminationWinner(bool isUserSideKilled,
            bool isAiSideKilled, int userSide, int aiSide)
        {
            if (!isUserSideKilled && !isAiSideKilled)
            {
                throw new InvalidOperationException(
                    "An elimination winner cannot be resolved while both sides are alive.");
            }

            // Preserve the existing campaign rule: a simultaneous wipe is a player loss.
            return isUserSideKilled ? aiSide : userSide;
        }
    }

    public partial class Level
    {
        private List<SavedSquad> _titania1TemporaryPatrolSquads;

        private void Awake()
        {
            // Titania I's authored fixed patrols are requested as exact one-ship squads during
            // SetTriggers(). The old test implementation created such a fleet explicitly, but the
            // active mission removed that seeding while retaining the exact-squad assumption.
            // Supply negative-ID encounter records before SetupLevel reaches SetTriggers; they are
            // removed in Start and therefore can never be written to campaign persistence.
            if (ConfigData.CurrentGameMode != ConfigData.GameModes.Campaign ||
                ConfigData.CurrentShips == null || ConfigData.UserProgressData == null ||
                ConfigData.Configuration == null ||
                ConfigData.UserProgressData.GetCurrentLevel(
                    ConfigData.Configuration.UserSide,
                    ConfigData.GameModes.Campaign) != 7)
            {
                return;
            }

            _titania1TemporaryPatrolSquads = new List<SavedSquad>();
            AddTemporaryTitaniaPatrols(_titania1TemporaryPatrolSquads, ConfigData.ShipTypes.Hornet, 8);
            AddTemporaryTitaniaPatrols(_titania1TemporaryPatrolSquads, ConfigData.ShipTypes.Wasp, 7);
            AddTemporaryTitaniaPatrols(_titania1TemporaryPatrolSquads, ConfigData.ShipTypes.Leafcutter, 2);
        }

        private void Start()
        {
            if (_titania1TemporaryPatrolSquads == null || ConfigData.CurrentShips == null)
            {
                return;
            }

            // LevelConstructor has already built runtime Squads by the time Start runs. Those
            // runtime objects retain their SavedSquad references, so removing the setup-only
            // records here prevents accidental persistence without removing the patrols in play.
            foreach (SavedSquad patrol in _titania1TemporaryPatrolSquads)
            {
                ConfigData.CurrentShips.RemoveSquad(patrol);
            }
            _titania1TemporaryPatrolSquads.Clear();
        }

        /// <summary>
        /// Runs Neptune I with a small continuation component for its legacy two-part success
        /// dialogue. The second section is queued after CloseLevel, when normal Level trigger
        /// polling has already stopped.
        /// </summary>
        public void Neptune1SeizeTheMeansWithEndingContinuation()
        {
            Neptune1SeizeTheMeans();
            Neptune1EndingContinuation continuation = gameObject.GetComponent<Neptune1EndingContinuation>();
            if (continuation == null)
            {
                continuation = gameObject.AddComponent<Neptune1EndingContinuation>();
            }
            continuation.Level = this;
        }

        /// <summary>
        /// Neptune III advances the campaign toward Titania but no longer grants the Carrier.
        /// Carrier acquisition belongs to completion of the Titania sequence.
        /// </summary>
        public void Neptune3PressingForwardWithTitaniaCarrierProgression()
        {
            Neptune3PressingForwardCampaign();
            Stage.CutsceneManager.EndDialogueAction = Neptune3EndingWithoutCarrier;
            Stage.CutsceneManager.HasEndDialogueAction = true;
        }

        public void Neptune3EndingWithoutCarrier()
        {
            Debug.Log("Level 5 complete");
            if (WinningSide == ConfigData.Configuration.AISide)
            {
                ConfigData.CurrentShips.GetFleetShips()
                    .Where(fleetShip => fleetShip.Type == ConfigData.ShipTypes.Factory)
                    .ToList()
                    .ForEach(fleetShip => fleetShip.IsDead = true);
            }

            // Preserve the non-Carrier progression from Neptune III. The Carrier itself is
            // awarded after successful completion of Titania II below.
            ConfigData.UserProgressData.VisibleBeeShipTypes.Add(ConfigData.ShipTypes.Bumblebee);
            ConfigData.UserProgressData.SetShipTypes();
            ConfigData.HasSeenPreLevelIntro = false;
            ConfigData.HasSeenIntermission = false;
            ConfigData.UserProgressData.HasMetAlejandraAndEmilia = true;
            ConfigData.UserProgressData.CampaignScore += State.PlayerScore;
            ConfigData.UserProgressData.AdvanceToNextLevel();
            SaveCampaignProgress();

            State.GameOver = true;
            Stage.Menus.ShowLevelSummary();
        }

        /// <summary>
        /// Titania I contains fixed one-ship patrols. This callable wrapper remains useful for
        /// isolated scenario hosts; normal campaign play receives the same temporary records from
        /// Awake so the legacy setup method itself can remain catalog-compatible.
        /// </summary>
        public void Titania1MinesweeperWithPatrolCompatibility()
        {
            List<SavedSquad> authoredPatrols = new List<SavedSquad>();
            AddTemporaryTitaniaPatrols(authoredPatrols, ConfigData.ShipTypes.Hornet, 8);
            AddTemporaryTitaniaPatrols(authoredPatrols, ConfigData.ShipTypes.Wasp, 7);
            AddTemporaryTitaniaPatrols(authoredPatrols, ConfigData.ShipTypes.Leafcutter, 2);

            try
            {
                Titania1MinesweeperCampaign();
            }
            finally
            {
                foreach (SavedSquad patrol in authoredPatrols)
                {
                    ConfigData.CurrentShips.RemoveSquad(patrol);
                }
            }
        }

        private static void AddTemporaryTitaniaPatrols(
            List<SavedSquad> patrols,
            ConfigData.ShipTypes shipType,
            int count)
        {
            for (int i = 0; i < count; i++)
            {
                FleetShip fleetShip = new FleetShip(
                    Utilities.GetNegativeFleetshipId(), shipType, false, false,
                    0, 0, 0, 0, 0, 0, 0);
                SavedSquad patrol = new SavedSquad(
                    Utilities.GetNegativeSavedSquadId(),
                    fleetShip.Side,
                    $"Titania patrol {Utilities.ConvertShipTypeToName[shipType]}",
                    Vector2.zero,
                    false,
                    false,
                    DefaultShootingStrategy,
                    UnsetColor,
                    null);
                patrol.AddShipToSquad(new SquadShip(fleetShip, Vector2.zero));
                ConfigData.CurrentShips.AddSquad(patrol);
                patrols.Add(patrol);
            }
        }

        /// <summary>
        /// Successful completion of both Titania missions awards and unlocks the Carrier. The
        /// idempotence check protects migrated saves that already received one from old Neptune III.
        /// </summary>
        public void Titania2BeenocularsWithCarrierUnlock()
        {
            Titania2BeenocularsCampaign();
            Stage.CutsceneManager.EndDialogueAction = Titania2CampaignEndingWithCarrierUnlock;
            Stage.CutsceneManager.HasEndDialogueAction = true;
        }

        public void Titania2CampaignEndingWithCarrierUnlock()
        {
            if (WinningSide == ConfigData.Configuration.UserSide)
            {
                bool carrierAlreadyUnlocked =
                    ConfigData.UserProgressData.UnlockedCampaignShips.Contains(ConfigData.ShipTypes.Carrier) ||
                    ConfigData.UserProgressData.VisibleHumanShipTypes.Contains(ConfigData.ShipTypes.Carrier);

                if (!carrierAlreadyUnlocked)
                {
                    ConfigData.CurrentShips.AddShipsToFleet(ConfigData.ShipTypes.Carrier, 1);
                    ConfigData.CurrentShips.BuildNewSquad(
                        $"Squad #{ConfigData.UserProgressData.HumanCampaignSavedSquadNumber++}",
                        ConfigData.Configuration.HumanSide,
                        ConfigData.ShipTypes.Carrier,
                        1);
                    State.PlayerNewShipsReceived += 1;
                }

                if (!ConfigData.UserProgressData.VisibleCodexHumanShipTypes.Contains(ConfigData.ShipTypes.Carrier))
                {
                    ConfigData.UserProgressData.VisibleCodexHumanShipTypes.Add(ConfigData.ShipTypes.Carrier);
                }
                if (!ConfigData.UserProgressData.VisibleHumanShipTypes.Contains(ConfigData.ShipTypes.Carrier))
                {
                    ConfigData.UserProgressData.VisibleHumanShipTypes.Add(ConfigData.ShipTypes.Carrier);
                }
                if (!ConfigData.UserProgressData.UnlockedCampaignShips.Contains(ConfigData.ShipTypes.Carrier))
                {
                    ConfigData.UserProgressData.UnlockedCampaignShips.Add(ConfigData.ShipTypes.Carrier);
                }
                ConfigData.UserProgressData.SetShipTypes();
            }

            Titania2CampaignEnding();
        }

        /// <summary>
        /// Re-applies Uranus I's authored fog flag after the legacy environment setup path.
        /// The mission data requests fog even when the generic Stage controller flag was false.
        /// </summary>
        public void Uranus1OnTheOffensiveWithAuthoredFog()
        {
            Uranus1OnTheOffensive();
            if (!HasPlayer || CurrentLevelOptions == null || CurrentLevelOptions.FogOfWar != 1 ||
                Map == null || Map.FogOfWar == null || State == null)
            {
                return;
            }

            ActivateFogOfWar = true;
            Map.FogOfWar.SetActive(true);
            foreach (Ship ship in State.GetShips(ConfigData.Configuration.UserSide))
            {
                if (ship.HasUserFogOfWarVision)
                {
                    ship.FogOfWarVision.Activate();
                }
            }
        }
    }

    internal sealed class Neptune1EndingContinuation : MonoBehaviour
    {
        private const string ContinuationName = "Level 4 Post-success dialogue";
        internal Level Level;

        private void Update()
        {
            if (Level == null || ConfigData.Configuration == null || Level.IsLevelConnectedToServer ||
                Level.WinningSide != ConfigData.Configuration.UserSide || Level.Stage == null ||
                Level.Stage.CutsceneManager == null || !Level.Stage.CutsceneManager.HitDialogueBreak)
            {
                return;
            }

            Trigger continuation = Level.NextTriggers.Find(trigger =>
                trigger != null && trigger.Name == ContinuationName);
            if (continuation == null || !continuation.Conditional())
            {
                return;
            }

            Level.NextTriggers.Remove(continuation);
            continuation.Action();
            enabled = false;
        }
    }

    /// <summary>
    /// Redirects the two affected campaign ending callbacks after ordinary mission setup. This is
    /// intentionally separate from CampaignMissionCatalog so its authoritative ID/name mapping
    /// remains unchanged.
    /// </summary>
    internal sealed class CampaignCarrierProgressionGuard : MonoBehaviour
    {
        private const float ScanInterval = 0.1f;
        private float _nextScan;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            GameObject host = new GameObject("Campaign Carrier Progression Guard");
            DontDestroyOnLoad(host);
            host.AddComponent<CampaignCarrierProgressionGuard>();
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextScan || ConfigData.CurrentGameMode != ConfigData.GameModes.Campaign ||
                ConfigData.UserProgressData == null || ConfigData.Configuration == null)
            {
                return;
            }
            _nextScan = Time.unscaledTime + ScanInterval;

            int missionId = ConfigData.UserProgressData.GetCurrentLevel(
                ConfigData.Configuration.UserSide,
                ConfigData.GameModes.Campaign);
            if (missionId != 6 && missionId != 8)
            {
                return;
            }

            foreach (Level level in FindObjectsOfType<Level>())
            {
                if (level == null || level.Stage == null || level.Stage.CutsceneManager == null)
                {
                    continue;
                }

                Action ending = level.Stage.CutsceneManager.EndDialogueAction;
                if (ending == null)
                {
                    continue;
                }

                if (missionId == 6 && ending.Method.Name == nameof(Level.Neptune3Ending))
                {
                    level.Stage.CutsceneManager.EndDialogueAction = level.Neptune3EndingWithoutCarrier;
                    level.Stage.CutsceneManager.HasEndDialogueAction = true;
                }
                else if (missionId == 8 && ending.Method.Name == nameof(Level.Titania2CampaignEnding))
                {
                    level.Stage.CutsceneManager.EndDialogueAction = level.Titania2CampaignEndingWithCarrierUnlock;
                    level.Stage.CutsceneManager.HasEndDialogueAction = true;
                }
            }
        }
    }

    /// <summary>
    /// Exit zones are world-space UI and therefore absent from the minimap camera. Mirror any
    /// campaign Zone that appears later in the mission with the existing minimap-only sprite.
    /// </summary>
    internal sealed class CampaignExitZoneMinimapGuard : MonoBehaviour
    {
        private const float ScanInterval = 0.25f;
        private float _nextScan;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            GameObject host = new GameObject("Campaign Exit Zone Minimap Guard");
            DontDestroyOnLoad(host);
            host.AddComponent<CampaignExitZoneMinimapGuard>();
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextScan || ConfigData.CurrentGameMode != ConfigData.GameModes.Campaign)
            {
                return;
            }
            _nextScan = Time.unscaledTime + ScanInterval;

            foreach (Level level in FindObjectsOfType<Level>())
            {
                if (level == null || level.Map == null || level.Stage == null || level.Stage.Prefabs == null ||
                    level.Stage.Prefabs.MinimapCircle == null)
                {
                    continue;
                }

                foreach (Zone zone in level.Map.GetComponentsInChildren<Zone>(true))
                {
                    if (zone == null || zone.transform.Find("Exit Zone Minimap Marker") != null)
                    {
                        continue;
                    }

                    GameObject marker = Instantiate(level.Stage.Prefabs.MinimapCircle, zone.transform);
                    marker.name = "Exit Zone Minimap Marker";
                    marker.transform.localPosition = Vector3.zero;
                    marker.transform.localRotation = Quaternion.identity;
                    marker.transform.localScale = Vector3.one;

                    SpriteRenderer markerRenderer = marker.GetComponent<SpriteRenderer>();
                    SpriteRenderer zoneRenderer = zone.GetComponent<SpriteRenderer>();
                    if (markerRenderer != null && zoneRenderer != null)
                    {
                        markerRenderer.color = zoneRenderer.color;
                    }
                }
            }
        }
    }
}
