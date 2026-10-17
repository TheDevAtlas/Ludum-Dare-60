using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Owns the buttons of a single menu: keeps exactly one selected, handles looping keyboard
// navigation (W/S/A/D + arrows, with hold-to-repeat), Enter to press, and an optional selector
// icon. Only reacts to input while its menu is the active menu in the MenuSwitcher.
// Put one on each menu (Root, Options, Credits, ...). A menu with a single button simply
// keeps that button selected.
public class MenuButtonGroup : MonoBehaviour
{
    [Tooltip("Defaults to the MenuSwitcher on this object or a parent.")]
    [SerializeField] private MenuSwitcher menuSwitcher;
    [Tooltip("The menu these buttons belong to. Defaults to the CanvasGroup on this object or a parent.")]
    [SerializeField] private CanvasGroup ownMenu;
    [Tooltip("Buttons in navigation order. If empty, uses child buttons not owned by a nested group.")]
    [SerializeField] private ButtonUI[] buttons;
    [Tooltip("Optional icon that follows the selected button vertically.")]
    [SerializeField] private RectTransform selector;
    [SerializeField] private float selectorVerticalOffset = 9f;
    [SerializeField] private float initialRepeatDelay = 0.45f;
    [SerializeField] private float repeatInterval = 0.1f;

    private int selectedIndex;
    private float nextNavigationTime;
    private ButtonUI keyboardPressedButton;

    protected MenuSwitcher MenuSwitcher => menuSwitcher;

    // Resolved lazily so other scripts can ask before this group's Awake has run.
    public CanvasGroup Menu
    {
        get
        {
            if (ownMenu == null)
            {
                ownMenu = GetComponentInParent<CanvasGroup>();
            }

            return ownMenu;
        }
    }

    public ButtonUI SelectedButton =>
        buttons != null && buttons.Length > 0 ? buttons[selectedIndex] : null;

    private bool IsInputActive => menuSwitcher == null || menuSwitcher.IsActiveMenu(Menu);

    protected virtual void Awake()
    {
        if (menuSwitcher == null)
        {
            menuSwitcher = GetComponentInParent<MenuSwitcher>();
        }

        if (buttons == null || buttons.Length == 0)
        {
            buttons = FindOwnedButtons();
        }

        RemoveUnavailableButtons();
    }

    protected virtual void OnEnable()
    {
        for (int i = 0; i < buttons.Length; i++)
        {
            buttons[i].Initialize(this);
        }

        SelectButtonAtIndex(selectedIndex);
    }

    // Start runs after every child's Awake/OnEnable, so the first button is guaranteed
    // to end up selected regardless of initialization order.
    protected virtual void Start()
    {
        selectedIndex = 0;
        SelectButtonAtIndex(selectedIndex);
    }

    protected virtual void Update()
    {
        if (!IsInputActive)
        {
            CancelKeyboardPress();

            // Blocks keys still held from another menu until they're released.
            nextNavigationTime = float.MaxValue;
            return;
        }

        HandleSubmitInput();

        if (keyboardPressedButton == null)
        {
            HandleNavigationInput();
        }
    }

    protected virtual void OnDisable()
    {
        CancelKeyboardPress();
    }

    public void SelectButton(ButtonUI button)
    {
        if (button == null || !button.IsSelectable)
        {
            return;
        }

        int index = System.Array.IndexOf(buttons, button);
        if (index >= 0)
        {
            SelectButtonAtIndex(index);
        }
    }

    private void CancelKeyboardPress()
    {
        if (keyboardPressedButton != null)
        {
            keyboardPressedButton.SetPressed(false);
            keyboardPressedButton = null;
        }
    }

    private void HandleSubmitInput()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null)
        {
            return;
        }

        if (keyboardPressedButton == null)
        {
            bool submitPressed = keyboard.enterKey.wasPressedThisFrame ||
                                 keyboard.numpadEnterKey.wasPressedThisFrame;
            if (submitPressed && SelectedButton != null)
            {
                keyboardPressedButton = SelectedButton;
                keyboardPressedButton.SetPressed(true);
            }

            return;
        }

        bool submitHeld = keyboard.enterKey.isPressed || keyboard.numpadEnterKey.isPressed;
        if (!submitHeld)
        {
            ButtonUI pressedButton = keyboardPressedButton;
            keyboardPressedButton = null;
            pressedButton.SetPressed(false);

            // Only fire if the mouse didn't move the selection elsewhere mid-press.
            if (pressedButton == SelectedButton)
            {
                pressedButton.TriggerClick();
            }
        }
    }

    private void SelectButtonAtIndex(int index)
    {
        if (buttons == null || buttons.Length == 0)
        {
            return;
        }

        selectedIndex = Mathf.Clamp(index, 0, buttons.Length - 1);
        for (int i = 0; i < buttons.Length; i++)
        {
            buttons[i].SetSelected(i == selectedIndex);
        }

        UpdateSelectorPosition();
    }

    private void HandleNavigationInput()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null || buttons.Length == 0)
        {
            return;
        }

        int direction = GetNavigationDirection(keyboard);
        if (direction == 0)
        {
            nextNavigationTime = 0f;
            return;
        }

        float now = Time.unscaledTime;
        if (nextNavigationTime > now)
        {
            return;
        }

        bool newPress = HasNewNavigationPress(keyboard);
        SelectButtonAtIndex(WrapIndex(selectedIndex + direction));
        nextNavigationTime = now + (newPress ? initialRepeatDelay : repeatInterval);
    }

    private static int GetNavigationDirection(Keyboard keyboard)
    {
        if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed ||
            keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed)
        {
            return -1;
        }

        if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed ||
            keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed)
        {
            return 1;
        }

        return 0;
    }

    private static bool HasNewNavigationPress(Keyboard keyboard)
    {
        return keyboard.wKey.wasPressedThisFrame || keyboard.upArrowKey.wasPressedThisFrame ||
               keyboard.aKey.wasPressedThisFrame || keyboard.leftArrowKey.wasPressedThisFrame ||
               keyboard.sKey.wasPressedThisFrame || keyboard.downArrowKey.wasPressedThisFrame ||
               keyboard.dKey.wasPressedThisFrame || keyboard.rightArrowKey.wasPressedThisFrame;
    }

    private int WrapIndex(int index)
    {
        if (index < 0)
        {
            return buttons.Length - 1;
        }

        return index >= buttons.Length ? 0 : index;
    }

    private void UpdateSelectorPosition()
    {
        if (selector == null || SelectedButton == null || SelectedButton.TextTransform == null)
        {
            return;
        }

        RectTransform textTransform = SelectedButton.TextTransform;
        RectTransform selectorParent = selector.parent as RectTransform;
        if (selectorParent == null)
        {
            return;
        }

        Vector3 textCenter = textTransform.TransformPoint(textTransform.rect.center);
        Vector3 targetPosition = textCenter +
                                selectorParent.TransformVector(Vector3.up * selectorVerticalOffset);
        Vector3 targetLocalPosition = selectorParent.InverseTransformPoint(targetPosition);
        Vector3 selectorLocalPosition = selector.localPosition;
        selectorLocalPosition.y = targetLocalPosition.y;
        selector.localPosition = selectorLocalPosition;
    }

    // Child buttons whose closest group is this one, so a group on the canvas never
    // claims buttons that belong to a group on one of its menus.
    private ButtonUI[] FindOwnedButtons()
    {
        List<ButtonUI> owned = new List<ButtonUI>();
        foreach (ButtonUI button in GetComponentsInChildren<ButtonUI>(true))
        {
            if (button.GetComponentInParent<MenuButtonGroup>(true) == this)
            {
                owned.Add(button);
            }
        }

        return owned.ToArray();
    }

    private void RemoveUnavailableButtons()
    {
        List<ButtonUI> available = new List<ButtonUI>();
        if (buttons != null)
        {
            foreach (ButtonUI button in buttons)
            {
                if (button != null && button.gameObject.activeSelf)
                {
                    available.Add(button);
                }
            }
        }

        buttons = available.ToArray();
    }
}
