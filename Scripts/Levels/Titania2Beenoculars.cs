using Assets.Scripts.Data;
using Assets.Scripts.Entities.Ships;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Assets.Scripts.Levels
{
    public partial class Level
    {
        /// <summary>
        /// Active Titania mission 2 setup. Gameplay remains the existing in-development
        /// implementation; this partial exists so its dialogue slices can match the authored
        /// mission script without editing the campaign trigger monolith.
        /// </summary>
        public void Titania2BeenocularsCampaign()
        {
            Vector2 centerOfTitania = new Vector2(-32, 55);
            HasContinuousTriggers = true;
            Stage.Menus.SetMissionStatus("Survive and defend Titania!");
            Stage.CutsceneManager.Setup(Titania2Ending);

            // Index 0 is the pre-mission briefing shown by LevelIntro. The in-level opening is
            // indices 1-10 inclusive.
            Stage.Menus.TogglePausePanel();
            Stage.CutsceneManager.PlayDialogueSection(
                Stage.CutsceneManager.Titania_Beenoculars.GetRange(1, 10));

            NextTriggers.Add(new Trigger(() => Stage.CutsceneManager.HitDialogueBreak, () =>
            {
                Stage.Menus.TogglePausePanel();
                Stage.EnablePlayerControl();

                HumanTarget titania = CreateHumanTarget(centerOfTitania);

                TMP_Text clockText = Stage.Menus.Clock.transform.GetChild(0).GetComponent<TMP_Text>();
                Stage.Menus.Clock.SetActive(true);
                float endTime = Time.time + 450f;

                ScaledTimer survivalClock = new ScaledTimer();
                survivalClock.Reuse(1f, () =>
                {
                    float timeLeft = endTime - Time.time;
                    if (timeLeft <= 0 || State.IsSideKilled(ConfigData.Configuration.AISide))
                    {
                        CancelTimer(survivalClock);
                        if (!titania.IsDead && !State.IsSideKilled(ConfigData.Configuration.UserSide))
                        {
                            WinningSide = ConfigData.Configuration.UserSide;
                            // Final two entries are the scripted success dialogue.
                            Stage.CutsceneManager.PlayDialogueSection(
                                Stage.CutsceneManager.Titania_Beenoculars.GetRange(31, 2), true);
                        }
                        else
                        {
                            WinningSide = ConfigData.Configuration.AISide;
                            // Entries 26-30 are the scripted abort/failure dialogue.
                            Stage.CutsceneManager.PlayDialogueSection(
                                Stage.CutsceneManager.Titania_Beenoculars.GetRange(26, 5), true);
                        }
                        CloseLevel();
                    }
                    else
                    {
                        int minutesLeft = Mathf.FloorToInt(timeLeft / 60f);
                        int secondsLeft = Mathf.FloorToInt(timeLeft % 60f);
                        clockText.text = $"{minutesLeft}:{secondsLeft:D2}";
                    }
                }, true);
                AddTimer(survivalClock);

                AddReinforcementSquads(new List<SavedSquad>() {
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 4, true, true),
                }, new Vector2(340, 200), new Vector2(300, 160));

                AddReinforcementSquads(new List<SavedSquad>() {
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4, true, true),
                }, new Vector2(-340, 220), new Vector2(-300, 180));

                AddReinforcementSquads(new List<SavedSquad>() {
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                }, new Vector2(120, -340), new Vector2(80, -300));

                AddReinforcementSquads(new List<SavedSquad>() {
                    ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 2, true, true),
                }, new Vector2(-120, -360), new Vector2(-80, -320));

                Stage.ActivateHiveMind = true;
                SetupHivemind();

                ScaledTimer wave1 = new ScaledTimer(60f, () =>
                {
                    AddReinforcementSquads(new List<SavedSquad>() {
                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 6, true, true),
                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Wasp, 4, true, true)
                    }, new Vector2(400, 0), new Vector2(340, 20));
                    AddReinforcementsToHivemindCommandQueue();
                });
                AddTimer(wave1);

                ScaledTimer wave2 = new ScaledTimer(120f, () =>
                {
                    AddReinforcementSquads(new List<SavedSquad>() {
                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Honeybee, 2, true, true),
                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.YellowJacket, 4, true, true)
                    }, new Vector2(-400, 0), new Vector2(-340, 20));
                    AddReinforcementsToHivemindCommandQueue();
                });
                AddTimer(wave2);

                ScaledTimer wave3 = new ScaledTimer(210f, () =>
                {
                    AddReinforcementSquads(new List<SavedSquad>() {
                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Leafcutter, 4, true, true),
                        ConfigData.CurrentShips.GetSquadByComposition(this, ConfigData.ShipTypes.Hornet, 8, true, true)
                    }, new Vector2(0, 420), new Vector2(0, 360));
                    AddReinforcementsToHivemindCommandQueue();
                });
                AddTimer(wave3);

                NextTriggers.Add(new Trigger(
                    () => titania.IsDead || State.IsSideKilled(ConfigData.Configuration.UserSide),
                    () =>
                    {
                        CancelTimer(survivalClock);
                        CancelTimer(wave1);
                        CancelTimer(wave2);
                        CancelTimer(wave3);
                        WinningSide = ConfigData.Configuration.AISide;
                        CloseLevel();
                        Stage.CutsceneManager.PlayDialogueSection(
                            Stage.CutsceneManager.Titania_Beenoculars.GetRange(26, 5), true);
                    },
                    "Titania 2 Losing condition"));

            }, "Titania 2 Start Level"));
        }
    }
}
