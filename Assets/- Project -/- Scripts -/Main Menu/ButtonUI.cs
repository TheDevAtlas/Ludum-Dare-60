using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;

public class ButtonUI : MonoBehaviour,
    IPointerEnterHandler, IPointerDownHandler, IPointerUpHandler, IPointerClickHandler
{
    [SerializeField] private TMP_Text text;
    [SerializeField] private Material selectedMaterial;
    [SerializeField] private Material unselectedMaterial;
    [SerializeField] private float pressedScale = 0.9f;
    [SerializeField] private float scaleSpeed = 20f;
    [SerializeField] private UnityEvent onClick = new UnityEvent();

    private MenuButtonGroup group;
    private RectTransform rectTransform;
    private bool isSelected;
    private bool isPressed;
    private Vector3 baseScale = Vector3.one;
    private Vector2 appliedOffset;
    private float currentScale = 1f;

    public RectTransform TextTransform => ResolveText() != null ? text.rectTransform : null;
    public bool IsSelectable => isActiveAndEnabled && gameObject.activeInHierarchy;

    private void Awake()
    {
        rectTransform = (RectTransform)transform;
        baseScale = transform.localScale;

        if (ResolveText() == null)
        {
            Debug.LogError($"'{name}' requires a TextMeshPro text component.", this);
        }
    }

    private void OnEnable()
    {
        // Re-apply the remembered state instead of forcing unselected; the group may have
        // already selected this button before this component was enabled.
        isPressed = false;
        ApplyScale(1f);
        RefreshMaterial();
    }

    private void OnDisable()
    {
        isPressed = false;
        ApplyScale(1f);
    }

    private void Update()
    {
        float targetScale = isPressed ? pressedScale : 1f;
        if (currentScale == targetScale)
        {
            return;
        }

        float t = 1f - Mathf.Exp(-scaleSpeed * Time.unscaledDeltaTime);
        float nextScale = Mathf.Lerp(currentScale, targetScale, t);
        ApplyScale(Mathf.Abs(nextScale - targetScale) < 0.001f ? targetScale : nextScale);
    }

    // Scales around the centre of the rendered text rather than the RectTransform pivot.
    // Scaling about the pivot by s moves a local point c to s * c, so shifting by (1 - s) * c
    // keeps the text centre in place. The shift is applied as a delta on top of wherever the
    // layout put the button, and is fully removed again at scale 1.
    private void ApplyScale(float scale)
    {
        currentScale = scale;
        transform.localScale = baseScale * scale;

        Vector3 textCenter = ResolveText() != null ? text.textBounds.center : Vector3.zero;
        Vector2 offset = transform.localRotation * Vector3.Scale(baseScale, textCenter) * (1f - scale);
        rectTransform.anchoredPosition += offset - appliedOffset;
        appliedOffset = offset;
    }

    public void Initialize(MenuButtonGroup owningGroup)
    {
        group = owningGroup;
    }

    public void SetSelected(bool selected)
    {
        isSelected = selected;
        RefreshMaterial();
    }

    public void SetPressed(bool pressed)
    {
        isPressed = pressed;
        RefreshMaterial();
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        if (group != null)
        {
            group.SelectButton(this);
        }
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        if (eventData.button != PointerEventData.InputButton.Left)
        {
            return;
        }

        if (group != null)
        {
            group.SelectButton(this);
        }
        SetPressed(true);
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        if (eventData.button != PointerEventData.InputButton.Left)
        {
            return;
        }

        SetPressed(false);
    }

    // Unity only sends this when the press and release both happen over this button.
    public void OnPointerClick(PointerEventData eventData)
    {
        if (eventData.button != PointerEventData.InputButton.Left)
        {
            return;
        }

        TriggerClick();
    }

    public void TriggerClick()
    {
        onClick.Invoke();
    }

    private TMP_Text ResolveText()
    {
        if (text == null)
        {
            text = GetComponent<TMP_Text>();
        }

        return text;
    }

    private void RefreshMaterial()
    {
        if (ResolveText() == null)
        {
            return;
        }

        Material material = isSelected && !isPressed ? selectedMaterial : unselectedMaterial;
        if (material != null)
        {
            text.fontSharedMaterial = material;
        }
    }
}
