using System.Collections.Generic;
using UnityEngine;

public class GridMaterialController : MonoBehaviour
{
    private const int MaxPoints = 16;

    private static readonly int ActivePointCountId = Shader.PropertyToID("_ActivePointCount");
    private static readonly int PointPositionsId = Shader.PropertyToID("_PointPositions");
    private static readonly int PointColorsId = Shader.PropertyToID("_PointColors");
    private static readonly int PointParamsId = Shader.PropertyToID("_PointParams");

    private class ActiveEffect
    {
        public Vector3 position;
        public Color color;
        public float emission;
        public float innerRadius;
        public float outerRadius;
        public float holdDuration;
        public float fadeDuration;
        public float heightDelta;
        public float elapsed;
        public bool isPermanent;
    }

    [SerializeField] private MeshRenderer gridRenderer;
    private MaterialPropertyBlock propertyBlock;

    private readonly List<ActiveEffect> activeEffects = new List<ActiveEffect>();
    private readonly Vector4[] pointPositionsBuffer = new Vector4[MaxPoints];
    private readonly Vector4[] pointColorsBuffer = new Vector4[MaxPoints];
    private readonly Vector4[] pointParamsBuffer = new Vector4[MaxPoints];

    private void Awake()
    {
        propertyBlock = new MaterialPropertyBlock();
        if (gridRenderer == null) gridRenderer = GetComponent<MeshRenderer>();
    }

    private void Update()
    {
        if (activeEffects.Count == 0) return;

        bool isDirty = false;

        for (int i = activeEffects.Count - 1; i >= 0; i--)
        {
            var effect = activeEffects[i];
            if (effect.isPermanent) continue;

            effect.elapsed += Time.deltaTime;
            isDirty = true;

            if (effect.elapsed >= (effect.holdDuration + effect.fadeDuration))
            {
                activeEffects.RemoveAt(i);
            }
        }

        if (isDirty)
        {
            UploadBufferData();
        }
    }

    public void AddRadialEffect(
        Vector3 worldPosition,
        Color color,
        float radius = 2.0f,
        float falloffSoftness = 0.6f,
        float emission = 3.5f,
        float holdDuration = 5.0f,
        float fadeDuration = 3.0f,
        float heightDelta = 0.0f)
    {
        float outer = Mathf.Max(0.01f, radius);
        float inner = Mathf.Clamp(radius - falloffSoftness, 0f, outer);

        if (activeEffects.Count >= MaxPoints)
        {
            int removeIdx = activeEffects.FindIndex(e => !e.isPermanent);
            if (removeIdx != -1) activeEffects.RemoveAt(removeIdx);
            else return;
        }

        activeEffects.Add(new ActiveEffect
        {
            position = worldPosition,
            color = color,
            emission = emission,
            innerRadius = inner,
            outerRadius = outer,
            holdDuration = holdDuration,
            fadeDuration = fadeDuration,
            heightDelta = heightDelta,
            elapsed = 0f,
            isPermanent = (holdDuration < 0f)
        });

        UploadBufferData();
    }

    private void UploadBufferData()
    {
        if (gridRenderer == null) return;
        if (propertyBlock == null) propertyBlock = new MaterialPropertyBlock();

        int count = Mathf.Min(activeEffects.Count, MaxPoints);

        for (int i = 0; i < count; i++)
        {
            var effect = activeEffects[i];

            float alpha = 1.0f;
            if (!effect.isPermanent && effect.elapsed > effect.holdDuration)
            {
                float fadeProgress = (effect.elapsed - effect.holdDuration) / Mathf.Max(0.001f, effect.fadeDuration);
                alpha = Mathf.Clamp01(1.0f - fadeProgress);
            }

            pointPositionsBuffer[i] = effect.position;
            pointColorsBuffer[i] = new Vector4(effect.color.r, effect.color.g, effect.color.b, effect.emission * alpha);
            // X = InnerRadius, Y = OuterRadius, Z = Alpha, W = HeightDelta (for 3D displaced surface lock)
            pointParamsBuffer[i] = new Vector4(effect.innerRadius, effect.outerRadius, alpha, effect.heightDelta);
        }

        gridRenderer.GetPropertyBlock(propertyBlock);
        propertyBlock.SetInt(ActivePointCountId, count);
        propertyBlock.SetVectorArray(PointPositionsId, pointPositionsBuffer);
        propertyBlock.SetVectorArray(PointColorsId, pointColorsBuffer);
        propertyBlock.SetVectorArray(PointParamsId, pointParamsBuffer);
        gridRenderer.SetPropertyBlock(propertyBlock);
    }

    public void ClearAllEffects()
    {
        activeEffects.Clear();
        if (gridRenderer == null) return;
        if (propertyBlock == null) propertyBlock = new MaterialPropertyBlock();

        gridRenderer.GetPropertyBlock(propertyBlock);
        propertyBlock.SetInt(ActivePointCountId, 0);
        gridRenderer.SetPropertyBlock(propertyBlock);
    }
}