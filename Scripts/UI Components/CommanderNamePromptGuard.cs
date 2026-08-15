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
        private static readonly Vector2 InputAnchor = new Vector2(0f, 1f);
        private static readonly Vector2 InputPosition = new Vector2(175f, -107.5f);
        private static readonly Vector2 InputSize = new Vector2(300f, 35f);
        private static readonly Color32 InputBackground = new Color32(30, 207, 136, 255);
        private static readonly Color32 InputForeground = new Color32(34, 62, 53, 255);

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

            // Resolve the field from the modal hierarchy first. This avoids accidentally repairing
            // an unrelated TMP_InputField if an older Main Menu scene carried a stale serialized
            // NameInput reference. The first-run prompt owns exactly one Text Input prefab.
            TMP_InputField input = GetComponentInChildren<TMP_InputField>(true);
            if (input == null && mainMenu != null && mainMenu.NameInput != null &&
                mainMenu.NameInput.transform.IsChildOf(transform))
            {
                input = mainMenu.NameInput;
            }

            if (input != null)
            {
                PrepareNameInput(input);

                // SubmitName reads MainMenu.NameInput.text and then persists it to
                // UserProgressData.PlayerName. Make the repaired visible field the authoritative
                // reference so the text the player sees and edits is exactly what gets saved.
                if (mainMenu != null)
                {
                    mainMenu.NameInput = input;
                }
            }
            else
            {
                Debug.LogError("Commander-name prompt does not contain a TMP_InputField.");
            }

            // Do not activate hidden templates. Repair only buttons that are actually visible in
            // the commander prompt. In addition to interactable, both the Selectable component and
            // its target Graphic must be enabled/raycastable or a button can look normal while
            // silently ignoring pointer input.
            Button[] buttons = GetComponentsInChildren<Button>(true);
            for (int i = 0; i < buttons.Length; i++)
            {
                Button button = buttons[i];
                if (!button.gameObject.activeInHierarchy)
                {
                    continue;
                }

                button.enabled = true;
                button.interactable = true;

                Graphic buttonGraphic = button.targetGraphic;
                if (buttonGraphic != null)
                {
                    buttonGraphic.gameObject.SetActive(true);
                    buttonGraphic.enabled = true;
                    buttonGraphic.raycastTarget = true;
                }

                TMP_Text label = button.GetComponentInChildren<TMP_Text>(true);
                if (label != null)
                {
                    if (string.IsNullOrWhiteSpace(label.text))
                    {
                        label.text = "Confirm";
                    }
                    MakeTextVisible(label);
                    label.raycastTarget = false;
                }

                // Keep the serialized listener for backwards compatibility, but also install a
                // runtime binding. Some legacy prompt instances can retain a visually valid Button
                // while their persistent SubmitName target is stale. RemoveListener prevents this
                // repair pass from accumulating duplicate runtime listeners.
                if (mainMenu != null)
                {
                    button.onClick.RemoveListener(mainMenu.SubmitName);
                    button.onClick.AddListener(mainMenu.SubmitName);
                }
            }
        }

        private void PrepareNameInput(TMP_InputField input)
        {
            ActivateBranch(input.transform);
            input.transform.SetAsLastSibling();

            // Main Panel/Text uses a VerticalLayoutGroup for the title and explanation. The
            // commander-name field is intentionally positioned inside that same text area, so it
            // must opt out of automatic layout or the layout pass can move it beneath the Buttons
            // panel, where the panel's opaque background completely hides it.
            LayoutElement layoutElement = input.GetComponent<LayoutElement>();
            if (layoutElement == null)
            {
                layoutElement = input.gameObject.AddComponent<LayoutElement>();
            }
            layoutElement.ignoreLayout = true;

            RectTransform inputRect = input.GetComponent<RectTransform>();
            if (inputRect != null)
            {
                inputRect.anchorMin = InputAnchor;
                inputRect.anchorMax = InputAnchor;
                inputRect.pivot = new Vector2(0.5f, 0.5f);
                inputRect.anchoredPosition = InputPosition;
                inputRect.sizeDelta = InputSize;
                inputRect.localScale = Vector3.one;
            }

            input.enabled = true;
            input.interactable = true;
            input.readOnly = false;
            input.characterLimit = 20;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.contentType = TMP_InputField.ContentType.Standard;
            input.customCaretColor = true;
            input.caretColor = InputForeground;

            Graphic targetGraphic = input.targetGraphic;
            if (targetGraphic != null)
            {
                targetGraphic.gameObject.SetActive(true);
                targetGraphic.enabled = true;
                targetGraphic.raycastTarget = true;
                targetGraphic.color = InputBackground;
            }

            if (input.textViewport != null)
            {
                input.textViewport.gameObject.SetActive(true);
                input.textViewport.localScale = Vector3.one;
            }

            if (input.textComponent != null)
            {
                input.textComponent.enabled = true;
                input.textComponent.color = InputForeground;
                MakeTextVisible(input.textComponent);
            }

            if (input.placeholder is TMP_Text placeholder)
            {
                placeholder.enabled = true;
                if (string.IsNullOrWhiteSpace(placeholder.text))
                {
                    placeholder.text = "Enter Name...";
                }
                placeholder.color = InputForeground;
                MakeTextVisible(placeholder, 0.6f);
            }

            // The prompt is only shown when a name is required, so put keyboard focus directly in
            // the field. The player can type immediately and Confirm continues to call SubmitName.
            if (input.gameObject.activeInHierarchy)
            {
                input.Select();
                input.ActivateInputField();
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
