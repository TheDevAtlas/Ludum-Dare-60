using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Fades between menus (any CanvasGroup) and remembers where you came from so Back() works.
// A transition fades the old menu out, then fades the new one in. The new menu becomes the
// active, interactive menu the moment it starts fading in, so players never see a menu they
// can't use. Nothing is interactive while the old menu is fading out.
public class MenuSwitcher : MonoBehaviour
{
    [Tooltip("Every menu this switcher manages. All but the starting menu are hidden on load.")]
    [SerializeField] private CanvasGroup[] menus;
    [Tooltip("Menu shown on load. Defaults to the first entry in Menus.")]
    [SerializeField] private CanvasGroup startingMenu;
    [SerializeField] private float fadeDuration = 0.4f;

    private readonly List<CanvasGroup> history = new List<CanvasGroup>();
    private Coroutine transition;
    private bool isFadingOut;
    private CanvasGroup pendingMenu;
    private Action pendingOnComplete;

    public CanvasGroup CurrentMenu { get; private set; }
    public bool IsTransitioning => transition != null;
    public bool CanGoBack => history.Count > 0;

    // Fired when a menu becomes the active menu (as it starts fading in): (previous, new).
    public event Action<CanvasGroup, CanvasGroup> MenuChanged;

    private void Awake()
    {
        if (startingMenu == null && menus != null && menus.Length > 0)
        {
            startingMenu = menus[0];
        }

        if (menus != null)
        {
            foreach (CanvasGroup menu in menus)
            {
                SetMenuState(menu, menu == startingMenu ? 1f : 0f, menu == startingMenu);
            }
        }

        CurrentMenu = startingMenu;
        SetMenuState(CurrentMenu, 1f, true);

        EnsureButtonGroups();
    }

    // Every menu needs a MenuButtonGroup so its buttons can be selected (and glow).
    // Menus that don't already have one controlling them get a default group, so adding a
    // new menu only requires adding it to the Menus list. Add a MenuButtonGroup yourself
    // to a menu if you want to customise it (button order, selector icon, repeat timings).
    private void EnsureButtonGroups()
    {
        if (menus == null)
        {
            return;
        }

        MenuButtonGroup[] existingGroups = GetComponentsInChildren<MenuButtonGroup>(true);
        foreach (CanvasGroup menu in menus)
        {
            if (menu != null && !HasButtonGroup(existingGroups, menu))
            {
                menu.gameObject.AddComponent<MenuButtonGroup>();
            }
        }
    }

    private static bool HasButtonGroup(MenuButtonGroup[] groups, CanvasGroup menu)
    {
        foreach (MenuButtonGroup group in groups)
        {
            if (group.Menu == menu)
            {
                return true;
            }
        }

        return false;
    }

    private void OnDisable()
    {
        // A stopped coroutine would leave menus half-faded; snap to the intended result instead.
        if (transition == null)
        {
            return;
        }

        StopCoroutine(transition);
        transition = null;
        isFadingOut = false;

        if (CurrentMenu != pendingMenu)
        {
            SetMenuState(CurrentMenu, 0f, false);
        }

        CurrentMenu = pendingMenu;
        SetMenuState(CurrentMenu, 1f, true);
        CompleteTransition();
    }

    public bool IsActiveMenu(CanvasGroup menu)
    {
        return menu != null && menu == CurrentMenu && !isFadingOut;
    }

    public void OpenMenu(CanvasGroup menu)
    {
        if (menu == null || menu == CurrentMenu || isFadingOut)
        {
            return;
        }

        // Opening a menu that's already in the history rewinds to it instead of stacking a loop.
        int existingIndex = history.LastIndexOf(menu);
        if (existingIndex >= 0)
        {
            history.RemoveRange(existingIndex, history.Count - existingIndex);
        }
        else if (CurrentMenu != null)
        {
            history.Add(CurrentMenu);
        }

        StartTransition(menu, null);
    }

    public void Back()
    {
        if (!CanGoBack || isFadingOut)
        {
            return;
        }

        CanvasGroup previous = history[history.Count - 1];
        history.RemoveAt(history.Count - 1);
        StartTransition(previous, null);
    }

    // Fades the current menu out and leaves everything hidden, e.g. before loading a scene.
    public void HideCurrent(Action onComplete = null)
    {
        if (isFadingOut)
        {
            return;
        }

        history.Clear();
        StartTransition(null, onComplete);
    }

    // Requests are only accepted while nothing is fading out, so the only transition this can
    // interrupt is one whose new menu is already fading in. That menu simply fades back out
    // from its current alpha.
    private void StartTransition(CanvasGroup target, Action onComplete)
    {
        if (transition != null)
        {
            StopCoroutine(transition);
        }

        pendingMenu = target;
        pendingOnComplete = onComplete;
        transition = StartCoroutine(TransitionRoutine(target));
    }

    private IEnumerator TransitionRoutine(CanvasGroup target)
    {
        CanvasGroup previous = CurrentMenu;

        isFadingOut = true;
        if (previous != null)
        {
            SetInteractive(previous, false);
            yield return Fade(previous, 0f);
        }

        isFadingOut = false;
        CurrentMenu = target;
        if (target != null)
        {
            SetInteractive(target, true);
        }

        MenuChanged?.Invoke(previous, target);

        if (target != null)
        {
            yield return Fade(target, 1f);
        }

        transition = null;
        CompleteTransition();
    }

    private void CompleteTransition()
    {
        Action onComplete = pendingOnComplete;
        pendingMenu = null;
        pendingOnComplete = null;
        onComplete?.Invoke();
    }

    // Duration scales with the remaining distance so interrupted fades keep a consistent speed.
    private IEnumerator Fade(CanvasGroup menu, float to)
    {
        float from = menu.alpha;
        float duration = fadeDuration * Mathf.Abs(to - from);

        for (float elapsed = 0f; elapsed < duration; elapsed += Time.unscaledDeltaTime)
        {
            menu.alpha = Mathf.Lerp(from, to, EaseInOutCubic(elapsed / duration));
            yield return null;
        }

        menu.alpha = to;
    }

    private static float EaseInOutCubic(float t)
    {
        return t < 0.5f ? 4f * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 3f) * 0.5f;
    }

    private static void SetMenuState(CanvasGroup menu, float alpha, bool interactive)
    {
        if (menu == null)
        {
            return;
        }

        menu.alpha = alpha;
        SetInteractive(menu, interactive);
    }

    private static void SetInteractive(CanvasGroup menu, bool interactive)
    {
        menu.interactable = interactive;
        menu.blocksRaycasts = interactive;
    }
}
