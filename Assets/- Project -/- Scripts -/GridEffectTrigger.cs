using UnityEngine;
using UnityEngine.InputSystem; // Required for the new Input System

public class GridEffectTrigger : MonoBehaviour
{
    [Header("Target Controllers")]
    [SerializeField] private GridMaterialController materialController;
    [SerializeField] private GridDevelopment gridDevelopment;

    [Header("Effect Shape & Color")]
    [ColorUsage(true, true)]
    [SerializeField] private Color effectColor = Color.red;
    [SerializeField] private float radius = 2.0f;
    [SerializeField] private float falloffSoftness = 0.6f;
    [SerializeField] private float emissionIntensity = 4.0f;

    [Header("Terrain Modification")]
    [SerializeField] private bool changesTerrain = false;
    [Tooltip("Positive values raise the terrain; negative values lower/dig into the terrain.")]
    [SerializeField] private float heightDelta = 1.0f;

    [Header("Timing (Hold then Fade)")]
    [Tooltip("Time the effect stays at 100% opacity. Use negative value for infinite.")]
    [SerializeField] private float holdDuration = 5.0f;
    [Tooltip("Time it takes to fade from 100% to 0% after the hold period.")]
    [SerializeField] private float fadeDuration = 3.0f;

    private void Update()
    {
        // Check for null keyboard first in case no keyboard is connected/detected
        if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
        {
            TriggerEffect();
        }
    }

    [ContextMenu("Trigger Effect At Position")]
    public void TriggerEffect()
    {
        // 1. Trigger Terrain Height Modification in 3D
        if (changesTerrain)
        {
            if (gridDevelopment == null)
            {
                gridDevelopment = FindFirstObjectByType<GridDevelopment>();
            }

            if (gridDevelopment != null)
            {
                gridDevelopment.ModifyTerrainAt(transform.position, radius, heightDelta);
            }
            else
            {
                Debug.LogWarning("[GridEffectTrigger] changesTerrain is enabled, but no GridDevelopment instance was found.", this);
            }
        }

        // 2. Trigger Shader Material Effect (passing heightDelta so 3D color tracks displaced surface)
        if (materialController == null)
        {
            materialController = FindFirstObjectByType<GridMaterialController>();
        }

        if (materialController != null)
        {
            materialController.AddRadialEffect(
                transform.position,
                effectColor,
                radius,
                falloffSoftness,
                emissionIntensity,
                holdDuration,
                fadeDuration,
                changesTerrain ? heightDelta : 0f
            );
        }
        else
        {
            Debug.LogWarning("[GridEffectTrigger] No GridMaterialController found in scene.", this);
        }
    }

    [ContextMenu("Clear All Active Effects")]
    public void ClearAllEffects()
    {
        if (materialController != null)
        {
            materialController.ClearAllEffects();
        }
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(effectColor.r, effectColor.g, effectColor.b, 0.45f);
        Gizmos.DrawWireSphere(transform.position, Mathf.Max(0f, radius - falloffSoftness));

        Gizmos.color = new Color(effectColor.r, effectColor.g, effectColor.b, 0.15f);
        Gizmos.DrawWireSphere(transform.position, radius);

        if (changesTerrain)
        {
            Gizmos.color = heightDelta >= 0f ? Color.green : Color.red;
            Gizmos.DrawWireCube(transform.position + Vector3.up * (heightDelta * 0.5f), new Vector3(radius * 0.5f, Mathf.Abs(heightDelta), radius * 0.5f));

            // Visualize the wider subdivision buffer radius
            float mult = gridDevelopment != null ? gridDevelopment.SubdivisionRadiusMultiplier : 1.35f;
            Gizmos.color = new Color(1f, 0.85f, 0f, 0.35f);
            Gizmos.DrawWireSphere(transform.position, radius * mult);
        }
    }
}