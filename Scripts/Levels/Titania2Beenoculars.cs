using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    public partial class Level
    {
        private static readonly Vector2 Titania2Center = new Vector2(-32f, 55f);
        private bool _titania2Resolved;
        private readonly List<ScaledTimer> _titania2MissionTimers = new List<ScaledTimer>();

        public void Titania2BeenocularsCampaign()
        {
            const float survivalDuration = 450f;

            _titania2Resolved = false;
            _titania2MissionTimers.Clear();
            HasContinuousTriggers = true;

            StageTitania2HumanFleetPreservingFormations();

            Stage.Menus.SetMissionStatus("Survive and defend Titania!");
            Stage.CutsceneManager.Setup(Titania2CampaignEnding);

            Stage.Menus.TogglePausePanel();
            Stage.CutsceneManager.PlayDialogueSection(
                Stage.CutsceneManager.Titania_Beenoculars.GetRange(1, 10));

            NextTriggers.Add(new Trigger(() => Stage.CutsceneManager.HitDialogueBreak, () =>
            {
                Stage.Menus.TogglePausePanel();
                Stage.EnablePlayerControl();

                HumanTarget titania = CreateHumanTarget(Titania2Center);

                TMP_Text clockText = Stage.Menus.Clock.transform.GetChild(0).GetComponent<TMP_Text>();
                // Match the established timed-mission layout used by Pluto 4 so the speed
                // control does not cover the countdown clock.
                Stage.Menus.GameSpeedButton.GetComponent<RectTransform>().anchoredPosition = new Vector2(-290, -15);
                Stage.Menus.Clock.SetActive(true);
                float endTime = Time.time + survivalDuration;

                bool playedTenPercent = false;
                bool playedTwentyFourPercent = false;
                bool playedFiftyPercent = false;
                bool playedSeventyFivePercent = false;
                bool playedNinetyPercent = false;

                ScaledTimer survivalClock = new ScaledTimer();
                survivalClock.Reuse(1f, () =>
                {
                    if (_titania2Resolved)
                    {
                        return;
                    }

                    float timeLeft = Mathf.Max(0f, endTime - Time.time);
                    float uploadProgress = 1f - (timeLeft / survivalDuration);

                    if (!playedTenPercent && uploadProgress >= 0.10f)
                    {
                        playedTenPercent = true;
                        Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Titania_Beenoculars.GetRange(12, 3));
                    }
                    if (!playedTwentyFourPercent && uploadProgress >= 0.24f)
                    {
                        playedTwentyFourPercent = true;
                        Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Titania_Beenoculars.GetRange(15, 3));
                    }
                    if (!playedFiftyPercent && uploadProgress >= 0.50f)
                    {
                        playedFiftyPercent = true;
                        Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Titania_Beenoculars.GetRange(18, 3));
                    }
                    if (!playedSeventyFivePercent && uploadProgress >= 0.75f)
                    {
                        playedSeventyFivePercent = true;
                        Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Titania_Beenoculars.GetRange(21, 3));
                    }
                    if (!playedNinetyPercent && uploadProgress >= 0.90f)
                    {
                        playedNinetyPercent = true;
                        Stage.CutsceneManager.PlayDialogueSection(Stage.CutsceneManager.Titania_Beenoculars.GetRange(24, 2));
                    }

                    int minutesLeft = Mathf.FloorToInt(timeLeft / 60f);
                    int secondsLeft = Mathf.FloorToInt(timeLeft % 60f);
                    clockText.text = $"{minutesLeft}:{secondsLeft:D2}";

                    if (timeLeft <= 0f)
                    {
                        ResolveTitania2(titania, true);
                    }
                }, true);
                AddTitania2Timer(survivalClock);

                // Prefab review: the left edge is largely sealed by a long wall, the upper/right
                // edges are segmented, and the most reliable openings are in the lower corners
                // plus gaps on the north/east sides. Each requested lane is checked against the
                // live obstacle colliders and shifted along its edge to the nearest clear opening.
                AddTitania2BeeWave(new List<SavedSquad>() {
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4, true, true),
                }, 1f, -0.65f);
                AddTitania2BeeWave(new List<SavedSquad>() {
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4, true, true),
                }, -1f, -0.85f);
                AddTitania2BeeWave(new List<SavedSquad>() {
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                }, 0.65f, -1f);
                AddTitania2BeeWave(new List<SavedSquad>() {
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 2, true, true),
                }, 0.65f, 1f);

                Stage.ActivateHiveMind = true;
                SetupHivemind();

                ScaledTimer wave1 = new ScaledTimer(60f, () =>
                {
                    if (_titania2Resolved) return;
                    AddTitania2BeeWave(new List<SavedSquad>() {
                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 6, true, true),
                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4, true, true)
                    }, 1f, -0.7f);
                    AddReinforcementsToHivemindCommandQueue();
                });
                AddTitania2Timer(wave1);

                ScaledTimer wave2 = new ScaledTimer(120f, () =>
                {
                    if (_titania2Resolved) return;
                    AddTitania2BeeWave(new List<SavedSquad>() {
                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true)
                    }, -1f, -0.85f);
                    AddReinforcementsToHivemindCommandQueue();
                });
                AddTitania2Timer(wave2);

                ScaledTimer wave3 = new ScaledTimer(210f, () =>
                {
                    if (_titania2Resolved) return;
                    AddTitania2BeeWave(new List<SavedSquad>() {
                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 4, true, true),
                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 8, true, true)
                    }, 0.65f, 1f);
                    AddReinforcementsToHivemindCommandQueue();
                });
                AddTitania2Timer(wave3);

                ScaledTimer wave4 = new ScaledTimer(300f, () =>
                {
                    if (_titania2Resolved) return;
                    AddTitania2BeeWave(new List<SavedSquad>() {
                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 6, true, true),
                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 6, true, true)
                    }, 0.55f, -1f);
                    AddReinforcementsToHivemindCommandQueue();
                });
                AddTitania2Timer(wave4);

                ScaledTimer wave5 = new ScaledTimer(375f, () =>
                {
                    if (_titania2Resolved) return;
                    AddTitania2BeeWave(new List<SavedSquad>() {
                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 4, true, true),
                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 8, true, true)
                    }, 0.7f, 1f);
                    AddTitania2BeeWave(new List<SavedSquad>() {
                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 3, true, true),
                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 6, true, true)
                    }, -0.55f, -1f);
                    AddReinforcementsToHivemindCommandQueue();
                });
                AddTitania2Timer(wave5);

                NextTriggers.Add(new Trigger(
                    () => titania.IsDead || State.IsSideKilled(ConfigData.Configuration.UserSide),
                    () => ResolveTitania2(titania, false),
                    "Titania 2 Losing condition"));

            }, "Titania 2 Start Level"));
        }

        private void StageTitania2HumanFleetPreservingFormations()
        {
            const float placementStep = 28f;
            const int maxRing = 8;
            const float titaniasReservedRadius = 22f;
            const float shipPadding = 4f;

            List<Vector2> occupiedCenters = new List<Vector2>();
            List<float> occupiedRadii = new List<float>();
            List<Squad> userSquads = State.GetSquadsBySide(ConfigData.Configuration.UserSide);

            Physics2D.SyncTransforms();
            foreach (Squad squad in userSquads)
            {
                List<Ship> ships = squad.GetShips();
                if (ships.Count == 0)
                {
                    continue;
                }

                Vector2 originalCenter = squad.GetPosition();
                bool placed = false;

                for (int ring = 1; ring <= maxRing && !placed; ring++)
                {
                    for (int x = -ring; x <= ring && !placed; x++)
                    {
                        for (int y = -ring; y <= ring; y++)
                        {
                            if (Mathf.Max(Mathf.Abs(x), Mathf.Abs(y)) != ring)
                            {
                                continue;
                            }

                            Vector2 candidateCenter = Titania2Center + new Vector2(x * placementStep, y * placementStep);
                            Vector2 delta = candidateCenter - originalCenter;
                            bool valid = true;

                            for (int shipIndex = 0; shipIndex < ships.Count; shipIndex++)
                            {
                                Ship ship = ships[shipIndex];
                                float clearance = Mathf.Max(ship.GetHalfWidth(), ship.GetHalfHeight()) + shipPadding;
                                Vector2 candidateShipPosition = ship.GetPosition() + delta;

                                if (candidateShipPosition.x - clearance < MinX || candidateShipPosition.x + clearance > MaxX ||
                                    candidateShipPosition.y - clearance < MinY || candidateShipPosition.y + clearance > MaxY ||
                                    Vector2.Distance(candidateShipPosition, Titania2Center) < titaniasReservedRadius + clearance ||
                                    Physics2D.OverlapCircle(candidateShipPosition, clearance, ConfigData.ObstaclesLayerMask) != null)
                                {
                                    valid = false;
                                    break;
                                }

                                for (int occupiedIndex = 0; occupiedIndex < occupiedCenters.Count; occupiedIndex++)
                                {
                                    if (Vector2.Distance(candidateShipPosition, occupiedCenters[occupiedIndex]) <
                                        clearance + occupiedRadii[occupiedIndex])
                                    {
                                        valid = false;
                                        break;
                                    }
                                }

                                if (!valid)
                                {
                                    break;
                                }
                            }

                            if (!valid)
                            {
                                continue;
                            }

                            // Translate the whole squad by one delta. Never call SetOffsets here:
                            // the relative formation chosen in Squad Maker must remain unchanged.
                            foreach (Ship ship in ships)
                            {
                                ship.transform.localPosition += (Vector3)delta;
                                occupiedCenters.Add(ship.GetPosition());
                                occupiedRadii.Add(Mathf.Max(ship.GetHalfWidth(), ship.GetHalfHeight()) + shipPadding);
                            }
                            placed = true;
                        }
                    }
                }

                if (!placed)
                {
                    Debug.LogWarning($"Beenoculars could not find a clear Titania staging position for {squad}; preserving its existing formation and position.");
                }
            }

            Physics2D.SyncTransforms();
            int userIndex = ConfigData.Configuration.UserSide - 1;
            CurrentLevelOptions.UserStartingPosition = Titania2Center;
            StartingPositions[userIndex] = Titania2Center;
            Stage.DefaultCameraPosition = Titania2Center;
        }

        private void AddTitania2BeeWave(List<SavedSquad> squads, float normalizedX, float normalizedY)
        {
            if (squads == null || squads.Count == 0)
            {
                return;
            }

            const float laneSpread = 0.16f;
            bool horizontalEntry = Mathf.Abs(normalizedX) >= Mathf.Abs(normalizedY);

            for (int i = 0; i < squads.Count; i++)
            {
                float centeredIndex = i - ((squads.Count - 1) * 0.5f);
                float laneOffset = centeredIndex * laneSpread;
                float squadX = horizontalEntry ? normalizedX : normalizedX + laneOffset;
                float squadY = horizontalEntry ? normalizedY + laneOffset : normalizedY;

                GetTitania2OffMapEntry(squadX, squadY, out Vector2 spawnPoint, out Vector2 entryPoint);
                AddReinforcementSquads(new List<SavedSquad> { squads[i] }, spawnPoint, entryPoint);
            }
        }

        private void GetTitania2OffMapEntry(
            float normalizedX,
            float normalizedY,
            out Vector2 spawnPoint,
            out Vector2 entryPoint)
        {
            const float outsideDistance = 80f;
            const float insideDistance = 28f;
            const float tangentMargin = 24f;
            const float laneClearance = 18f;
            const float scanStep = 28f;
            const int scanSteps = 12;

            bool horizontalEntry = Mathf.Abs(normalizedX) >= Mathf.Abs(normalizedY);
            bool positiveEdge = horizontalEntry ? normalizedX >= 0f : normalizedY >= 0f;
            float normalizedTangent = horizontalEntry ? normalizedY : normalizedX;
            float tangentMin = horizontalEntry ? MinY + tangentMargin : MinX + tangentMargin;
            float tangentMax = horizontalEntry ? MaxY - tangentMargin : MaxX - tangentMargin;
            float preferredTangent = Mathf.Lerp(
                tangentMin,
                tangentMax,
                (Mathf.Clamp(normalizedTangent, -1f, 1f) + 1f) * 0.5f);

            Physics2D.SyncTransforms();
            for (int step = 0; step <= scanSteps; step++)
            {
                int signedStep = step == 0 ? 0 : ((step + 1) / 2) * (step % 2 == 1 ? 1 : -1);
                float tangent = Mathf.Clamp(preferredTangent + signedStep * scanStep, tangentMin, tangentMax);

                if (horizontalEntry)
                {
                    spawnPoint = new Vector2(positiveEdge ? MaxX + outsideDistance : MinX - outsideDistance, tangent);
                    entryPoint = new Vector2(positiveEdge ? MaxX - insideDistance : MinX + insideDistance, tangent);
                }
                else
                {
                    spawnPoint = new Vector2(tangent, positiveEdge ? MaxY + outsideDistance : MinY - outsideDistance);
                    entryPoint = new Vector2(tangent, positiveEdge ? MaxY - insideDistance : MinY + insideDistance);
                }

                if (Physics2D.OverlapCircle(entryPoint, laneClearance, ConfigData.ObstaclesLayerMask) == null &&
                    Physics2D.Linecast(spawnPoint, entryPoint, ConfigData.ObstaclesLayerMask).collider == null)
                {
                    return;
                }
            }

            if (horizontalEntry)
            {
                spawnPoint = new Vector2(positiveEdge ? MaxX + outsideDistance : MinX - outsideDistance, preferredTangent);
                entryPoint = new Vector2(positiveEdge ? MaxX - insideDistance : MinX + insideDistance, preferredTangent);
            }
            else
            {
                spawnPoint = new Vector2(preferredTangent, positiveEdge ? MaxY + outsideDistance : MinY - outsideDistance);
                entryPoint = new Vector2(preferredTangent, positiveEdge ? MaxY - insideDistance : MinY + insideDistance);
            }
            Debug.LogWarning($"Beenoculars could not find a clear obstacle opening near requested lane {normalizedX},{normalizedY}; using {entryPoint}.");
        }

        private void AddTitania2Timer(ScaledTimer timer)
        {
            _titania2MissionTimers.Add(timer);
            AddTimer(timer);
        }

        private void CancelTitania2Timers()
        {
            foreach (ScaledTimer timer in _titania2MissionTimers)
            {
                CancelTimer(timer);
            }
            _titania2MissionTimers.Clear();
        }

        private void ResolveTitania2(HumanTarget titania, bool survivedEvacuation)
        {
            if (_titania2Resolved)
            {
                return;
            }

            bool userAlive = !State.IsSideKilled(ConfigData.Configuration.UserSide);
            bool success = survivedEvacuation && titania != null && !titania.IsDead && userAlive;

            _titania2Resolved = true;
            CancelTitania2Timers();
            Stage.Menus.Clock.SetActive(false);
            WinningSide = success ? ConfigData.Configuration.UserSide : ConfigData.Configuration.AISide;

            CloseLevel();
            Stage.CutsceneManager.PlayDialogueSection(
                success
                    ? Stage.CutsceneManager.Titania_Beenoculars.GetRange(31, 2)
                    : Stage.CutsceneManager.Titania_Beenoculars.GetRange(26, 5),
                true);
        }

        public void Titania2CampaignEnding()
        {
            ConfigData.HasSeenPreLevelIntro = false;
            ConfigData.HasSeenIntermission = false;
            ConfigData.UserProgressData.CampaignScore += State.PlayerScore;
            ConfigData.UserProgressData.AdvanceToNextLevel();

            ConfigData.UserProgressData.Save();
            ConfigData.CurrentShips.SaveSquadData();
            ConfigData.CurrentShips.SaveFleetData();

            State.GameOver = true;
            Stage.Menus.ShowLevelSummary();
        }
    }
}
