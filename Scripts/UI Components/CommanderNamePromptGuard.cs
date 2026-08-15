using Assets.Scripts.Scenes;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Assets.Scripts.UI_Components
{
    /// <summary>
    /// Keeps the serialized first-run commander-name prompt usable after asynchronous profile
    /// bootstrap. The prompt is authored inactive in Main Menu and is enabled only after the
    /// server confirms that the user_progress row was newly created; legacy prefab child state
    /// must therefore be repaired when it becomes visible rather than assumed to have run Start.
    /// </summary>
    public sealed class CommanderNamePromptGuard : MonoBehaviour
    {
        private const string PromptName = "Choose Commander Name";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        private static void HandleSceneLoaded(UnityEngine.SceneManagement.Scene scene, LoadSceneMode mode)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < transforms.Length; i++)
                {
                    GameObject candidate = transforms[i].gameObject;
                    if (candidate.name != PromptName || candidate.GetComponent<CommanderNamePromptGuard>() != null)
                    {
                        continue;
                    }

                    candidate.AddComponent<CommanderNamePromptGuard>();
                }
            }
        }

        private void Awake()
        {
            PreparePrompt();
        }

        private void OnEnable()
        {
            PreparePrompt();
        }

        private void PreparePrompt()
        {
            ModalInputBlocker.Ensure(gameObject);

            TMP_Text title = GetText("Main Panel/Text/Title");
            if (title != null)
            {
                if (string.IsNullOrWhiteSpace(title.text))
                {
                    title.text = "Welcome Commander!";
                }
                MakeTextVisible(title);
            }

            TMP_Text explanation = GetText("Main Panel/Text/Explanation");
            if (explanation != null)
            {
                if (string.IsNullOrWhiteSpace(explanation.text))
                {
                    explanation.text = "Choose a commander name.";
                }
                MakeTextVisible(explanation);
            }

            MainMenu mainMenu = Object.FindObjectOfType<MainMenu>();
            TMP_InputField input = mainMenu != null && mainMenu.NameInput != null
                ? mainMenu.NameInput
                : GetComponentInChildren<TMP_InputField>(true);
            if (input != null)
            {
                ActivateBranch(input.transform);
                input.interactable = true;
                MakeTextVisible(input.textComponent);
                if (input.placeholder is TMP_Text placeholder)
                {
                    if (string.IsNullOrWhiteSpace(placeholder.text))
                    {
                        placeholder.text = "Commander Name";
                    }
                    MakeTextVisible(placeholder, 0.65f);
                }
            }

            // Do not activate hidden templates. Repair only buttons already authored as active in
            // the commander prompt and provide a fallback label if an old prefab lost its text.
            Button[] buttons = GetComponentsInChildren<Button>(true);
            for (int i = 0; i < buttons.Length; i++)
            {
                Button button = buttons[i];
                if (!button.gameObject.activeSelf || button.gameObject.name == "Button Prefab")
                {
                    continue;
                }

                button.interactable = true;
                TMP_Text label = button.GetComponentInChildren<TMP_Text>(true);
                if (label != null)
                {
                    if (string.IsNullOrWhiteSpace(label.text))
                    {
                        label.text = "Confirm";
                    }
                    MakeTextVisible(label);
                }
            }
        }

        private TMP_Text GetText(string path)
        {
            Transform child = transform.Find(path);
            return child != null ? child.GetComponent<TMP_Text>() : null;
        }

        private void ActivateBranch(Transform child)
        {
            if (child == null)
            {
                return;
            }

            // NameInput is expected to live inside this modal. If a legacy scene has the serialized
            // reference elsewhere, make the input itself visible without walking up and activating
            // unrelated menu containers.
            if (!child.IsChildOf(transform))
            {
                child.gameObject.SetActive(true);
                return;
            }

            Transform current = child;
            while (current != null && current != transform)
            {
                current.gameObject.SetActive(true);
                current = current.parent;
            }
        }

        private static void MakeTextVisible(TMP_Text text, float alpha = 1f)
        {
            if (text == null)
            {
                return;
            }

            text.gameObject.SetActive(true);
            Color color = text.color;
            color.a = alpha;
            text.color = color;
        }
    }
}
