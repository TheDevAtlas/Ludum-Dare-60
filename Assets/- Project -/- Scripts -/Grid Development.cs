using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways]
public class GridDevelopment : MonoBehaviour
{
    [Header("Grid")]
    [Min(1)] [SerializeField] private int width = 15;
    [Min(1)] [SerializeField] private int height = 15;
    [SerializeField] private float spacing = 1f;
    [SerializeField] private Vector2 quadSize = new Vector2(0.9f, 0.9f);

    [Header("Noise")]
    [SerializeField] private float noiseScale = 0.15f;
    [SerializeField] private Vector2 noiseOffset = Vector2.zero;
    [SerializeField] private float minHeight = -0.5f;
    [SerializeField] private float maxHeight = 2.5f;

    [Header("Visuals")]
    [SerializeField] private Material quadMaterial;
    [SerializeField] private Material outlineMaterial;
    [Min(0.001f)] [SerializeField] private float outlineWidth = 0.04f;
    [SerializeField] private float outlineNormalOffset = 0.002f;

    private const float CoordinateBaseOffset = 10000f;

    private void Start()
    {
        GenerateGrid();
    }

    private void OnValidate()
    {
#if UNITY_EDITOR
        EditorApplication.delayCall += () =>
        {
            if (this != null) GenerateGrid();
        };
#endif
    }

    [ContextMenu("Regenerate Grid")]
    public void GenerateGrid()
    {
        ClearGrid();

        float offsetX = (width - 1) * spacing * 0.5f;
        float offsetZ = (height - 1) * spacing * 0.5f;

        float insetX = (spacing - quadSize.x) * 0.5f;
        float insetZ = (spacing - quadSize.y) * 0.5f;

        float safeOutlineWidth = Mathf.Clamp(outlineWidth, 0.001f, Mathf.Min(quadSize.x, quadSize.y) * 0.49f);

        for (int x = 0; x < width; x++)
        {
            for (int z = 0; z < height; z++)
            {
                var quadObj = new GameObject($"Quad_{x}_{z}");
                quadObj.transform.SetParent(transform, false);

                var quadFilter = quadObj.AddComponent<MeshFilter>();
                var quadRenderer = quadObj.AddComponent<MeshRenderer>();

                if (quadMaterial != null)
                    quadRenderer.sharedMaterial = quadMaterial;

                // Bounds
                float left = (x * spacing) - offsetX + insetX;
                float right = (x * spacing) - offsetX + spacing - insetX;
                float bottom = (z * spacing) - offsetZ + insetZ;
                float top = (z * spacing) - offsetZ + spacing - insetZ;

                // Height sampling in world space so noise flows across tiles cleanly
                Vector3 blWorld = transform.TransformPoint(new Vector3(left, 0f, bottom));
                Vector3 brWorld = transform.TransformPoint(new Vector3(right, 0f, bottom));
                Vector3 tlWorld = transform.TransformPoint(new Vector3(left, 0f, top));
                Vector3 trWorld = transform.TransformPoint(new Vector3(right, 0f, top));

                float yBL = SampleHeight(blWorld.x, blWorld.z);
                float yBR = SampleHeight(brWorld.x, brWorld.z);
                float yTL = SampleHeight(tlWorld.x, tlWorld.z);
                float yTR = SampleHeight(trWorld.x, trWorld.z);

                Vector3 vBL = new Vector3(left, yBL, bottom);
                Vector3 vBR = new Vector3(right, yBR, bottom);
                Vector3 vTL = new Vector3(left, yTL, top);
                Vector3 vTR = new Vector3(right, yTR, top);

                var quadMesh = new Mesh
                {
                    name = "DisplacedQuad",
                    vertices = new[] { vBL, vBR, vTL, vTR },
                    uv = new[]
                    {
                        new Vector2(0f, 0f),
                        new Vector2(1f, 0f),
                        new Vector2(0f, 1f),
                        new Vector2(1f, 1f)
                    },
                    triangles = new[]
                    {
                        0, 2, 1,
                        2, 3, 1
                    }
                };

                quadMesh.RecalculateNormals();
                quadMesh.RecalculateBounds();
                quadFilter.sharedMesh = quadMesh;

                if (outlineWidth > 0f)
                {
                    BuildQuadOutline(quadObj.transform, left, right, bottom, top,
                                     vBL, vBR, vTL, vTR, safeOutlineWidth);
                }
            }
        }
    }

    private void BuildQuadOutline(Transform parent, float left, float right, float bottom, float top,
                                  Vector3 vBL, Vector3 vBR, Vector3 vTL, Vector3 vTR, float widthInset)
    {
        var outlineObj = new GameObject("Outline");
        outlineObj.transform.SetParent(parent, false);

        var outlineFilter = outlineObj.AddComponent<MeshFilter>();
        var outlineRenderer = outlineObj.AddComponent<MeshRenderer>();

        if (outlineMaterial != null)
            outlineRenderer.sharedMaterial = outlineMaterial;

        // Face normal averaging to nudge outline off the quad surface (avoids z-fighting)
        Vector3 n1 = Vector3.Cross(vTL - vBL, vBR - vBL).normalized;
        Vector3 n2 = Vector3.Cross(vTR - vTL, vBR - vTL).normalized;
        Vector3 surfaceNormal = ((n1 + n2) * 0.5f).normalized;
        if (surfaceNormal.y < 0f) surfaceNormal = -surfaceNormal;

        Vector3 normalOffsetVector = surfaceNormal * outlineNormalOffset;

        float iLeft = left + widthInset;
        float iRight = right - widthInset;
        float iBottom = bottom + widthInset;
        float iTop = top - widthInset;

        float yiBL = GetSurfaceHeight(left, right, bottom, top, vBL.y, vBR.y, vTL.y, vTR.y, iLeft, iBottom);
        float yiBR = GetSurfaceHeight(left, right, bottom, top, vBL.y, vBR.y, vTL.y, vTR.y, iRight, iBottom);
        float yiTR = GetSurfaceHeight(left, right, bottom, top, vBL.y, vBR.y, vTL.y, vTR.y, iRight, iTop);
        float yiTL = GetSurfaceHeight(left, right, bottom, top, vBL.y, vBR.y, vTL.y, vTR.y, iLeft, iTop);

        // Ribbon frame: outer ring (0-3), inner ring (4-7)
        Vector3[] vertices =
        {
            vBL + normalOffsetVector,
            vBR + normalOffsetVector,
            vTR + normalOffsetVector,
            vTL + normalOffsetVector,

            new Vector3(iLeft, yiBL, iBottom) + normalOffsetVector,
            new Vector3(iRight, yiBR, iBottom) + normalOffsetVector,
            new Vector3(iRight, yiTR, iTop) + normalOffsetVector,
            new Vector3(iLeft, yiTL, iTop) + normalOffsetVector
        };

        float uInset = widthInset / quadSize.x;
        float vInset = widthInset / quadSize.y;

        Vector2[] uvs =
        {
            new Vector2(0f, 0f),
            new Vector2(1f, 0f),
            new Vector2(1f, 1f),
            new Vector2(0f, 1f),

            new Vector2(uInset, vInset),
            new Vector2(1f - uInset, vInset),
            new Vector2(1f - uInset, 1f - vInset),
            new Vector2(uInset, 1f - vInset)
        };

        int[] triangles =
        {
            // Bottom
            0, 4, 1,
            4, 5, 1,
            // Right
            1, 5, 2,
            5, 6, 2,
            // Top
            2, 6, 3,
            6, 7, 3,
            // Left
            3, 7, 0,
            7, 4, 0
        };

        var outlineMesh = new Mesh
        {
            name = "QuadOutline",
            vertices = vertices,
            uv = uvs,
            triangles = triangles
        };

        outlineMesh.RecalculateNormals();
        outlineMesh.RecalculateBounds();
        outlineFilter.sharedMesh = outlineMesh;
    }

    private float GetSurfaceHeight(float left, float right, float bottom, float top,
                                   float yBL, float yBR, float yTL, float yTR,
                                   float x, float z)
    {
        float tx = Mathf.Clamp01((x - left) / (right - left));
        float tz = Mathf.Clamp01((z - bottom) / (top - bottom));

        // Diagonal split (TL -> BR)
        if (tx + tz <= 1.0f)
            return yBL + tx * (yBR - yBL) + tz * (yTL - yBL);

        return yTR + (1f - tx) * (yTL - yTR) + (1f - tz) * (yBR - yTR);
    }

    private float SampleHeight(float worldX, float worldZ)
    {
        float sampleX = (worldX + CoordinateBaseOffset + noiseOffset.x) * noiseScale;
        float sampleZ = (worldZ + CoordinateBaseOffset + noiseOffset.y) * noiseScale;

        return Mathf.Lerp(minHeight, maxHeight, Mathf.PerlinNoise(sampleX, sampleZ));
    }

    public void ClearGrid()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var child = transform.GetChild(i).gameObject;
            if (Application.isPlaying)
                Destroy(child);
            else
                DestroyImmediate(child);
        }
    }
}