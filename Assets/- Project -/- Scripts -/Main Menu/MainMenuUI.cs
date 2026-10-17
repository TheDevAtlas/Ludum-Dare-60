using UnityEngine;
using UnityEngine.InputSystem;

// The main menu's controller. It owns the root menu's buttons (via MenuButtonGroup) and is
// the single place that handles going back with Escape and switching between menus.
// Other menus own their buttons with their own MenuButtonGroup.
public class MainMenuUI : MenuButtonGroup
{
    protected override void Update()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
        {
            Back();
        }

        base.Update();
    }

    public void OpenMenu(CanvasGroup menu)
    {
        if (MenuSwitcher != null)
        {
            MenuSwitcher.OpenMenu(menu);
        }
    }

    public void Back()
    {
        if (MenuSwitcher != null)
        {
            MenuSwitcher.Back();
        }
    }
}
