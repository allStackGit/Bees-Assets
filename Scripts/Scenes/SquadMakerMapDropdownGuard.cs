using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Assets.Scripts.Scenes
{
    /// <summary>
    /// Keeps the Free Play/Training map selector synchronized with ConfigData.Maps.
    /// The serialized Squad Maker dropdown can become stale when new locations are added,
    /// which also shifts the option-to-map-id mapping used by ChangeMapDropdown.
    /// </summary>
    internal static class SquadMakerMapDropdownGuard
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        private static void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            SquadMaker squadMaker = Object.FindObjectOfType<SquadMaker>();
            if (squadMaker == null || squadMaker.MapDropdown == null)
            {
                return;
            }

            TMP_Dropdown dropdown = squadMaker.MapDropdown.GetComponent<TMP_Dropdown>();
            if (dropdown == null)
            {
                return;
            }

            List<TMP_Dropdown.OptionData> options = new List<TMP_Dropdown.OptionData>
            {
                new TMP_Dropdown.OptionData("Random")
            };

            foreach (Data.Map map in ConfigData.Maps.OrderBy(map => map.Id))
            {
                options.Add(new TMP_Dropdown.OptionData(map.Name));
            }

            dropdown.ClearOptions();
            dropdown.AddOptions(options);
            dropdown.SetValueWithoutNotify(0);
            dropdown.RefreshShownValue();
        }
    }
}
