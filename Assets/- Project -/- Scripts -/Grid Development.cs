using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class GridDevelopment : MonoBehaviour
{
    [Header("Grid Dimensions")]
    [Min(1)] [SerializeField] private int width = 15;
    [Min(1)] [SerializeField] private int height = 15;
    [SerializeField] private float spacing = 1f;
    [SerializeField] private Vector2 quadSize = new Vector2(0.9f, 0.9f);

    [Header("Dynamic 3D Subdivision")]
    [SerializeField] private bool enableSubdivision = true;
    [Tooltip("Maximum recursive splits (1 = 4 sub-tiles, 2 = 16, 3 = 64).")]
    [Range(0, 4)] [SerializeField] private int maxSubdivisionDepth = 2;
    [Tooltip("Deformation slope (height delta per unit width) required for the first split into 4 sub-tiles.")]
    [Min(0.05f)] [SerializeField] private float stretchSlopeThreshold = 0.45f;
    [Tooltip("How much steeper a sub-tile's slope must be to split again at the next depth level (>1 prevents over-subdividing on the first hit).")]
    [Range(1.0f, 2.5f)] [SerializeField] private float depthSlopeMultiplier = 1.45f;
    [Tooltip("3D radius multiplier around the blast to smoothly pre-subdivide boundary tiles.")]
    [Min(1f)] [SerializeField] private float subdivisionRadiusMultiplier = 1.25f;
    [Tooltip("Ensures adjacent tiles never jump by more than 1 subdivision level.")]
    [SerializeField] private bool smoothSubdivisionTransitions = true;
    [Tooltip("If enabled, steep initial Perlin noise also triggers subdivision.")]
    [SerializeField] private bool subdivideBaseNoise = false;

    [Header("Noise")]
    [SerializeField] private float noiseScale = 0.15f;
    [SerializeField] private Vector2 noiseOffset = Vector2.zero;
    [SerializeField] private float minHeight = -0.5f;
    [SerializeField] private float maxHeight = 2.5f;

    [Header("Visuals")]
    [SerializeField] private Material gridMaterial;

    private const float CoordinateBaseOffset = 10000f;

    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    private Mesh gridMesh;

    private readonly List<Vector3> vertices = new List<Vector3>();
    private readonly List<Vector2> uvs = new List<Vector2>();
    private readonly List<int> triangles = new List<int>();

    private class GridTile
    {
        public float minX, maxX, minZ, maxZ;
        public int depth;

        // Persistent 3D height offsets at the 4 quad corners (BL, BR, TL, TR)
        public float offsetBL, offsetBR, offsetTL, offsetTR;

        // Cached local-space and world-space corner positions
        public Vector3 vBL, vBR, vTL, vTR;
        public Vector3 wBL, wBR, wTL, wTR;

        public GridTile[] children;
        public bool IsLeaf => children == null;
    }

    private GridTile[,] rootTiles;
    private readonly List<GridTile> leafTiles = new List<GridTile>();

    public float SubdivisionRadiusMultiplier => subdivisionRadiusMultiplier;

    private void Awake()
    {
        InitializeComponents();
    }

    private void Start()
    {
        if (rootTiles == null)
        {
            GenerateGrid();
        }
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

    private void InitializeComponents()
    {
        if (meshFilter == null) meshFilter = GetComponent<MeshFilter>();
        if (meshRenderer == null) meshRenderer = GetComponent<MeshRenderer>();

        if (gridMesh == null)
        {
            gridMesh = new Mesh { name = "ProceduralGridMesh" };
            gridMesh.indexFormat = IndexFormat.UInt32;
            gridMesh.MarkDynamic();
            meshFilter.sharedMesh = gridMesh;
        }

        if (gridMaterial != null && meshRenderer.sharedMaterial != gridMaterial)
        {
            meshRenderer.sharedMaterial = gridMaterial;
        }
    }

    [ContextMenu("Regenerate Grid")]
    public void GenerateGrid()
    {
        InitializeComponents();
        ClearLegacyChildren();

        BuildRootTiles();

        if (enableSubdivision && subdivideBaseNoise && maxSubdivisionDepth > 0)
        {
            for (int d = 0; d < maxSubdivisionDepth; d++)
            {
                CollectLeaves();
                bool splitAny = false;
                for (int i = 0; i < leafTiles.Count; i++)
                {
                    GridTile tile = leafTiles[i];
                    if (tile.depth < maxSubdivisionDepth && ShouldTileSplitCurrent(tile))
                    {
                        SubdivideTile(tile);
                        splitAny = true;
                    }
                }
                if (!splitAny) break;
            }

            if (smoothSubdivisionTransitions)
            {
                BalanceQuadtreeGradation();
            }
        }

        RebuildMeshFromLeaves();
    }

    private void BuildRootTiles()
    {
        rootTiles = new GridTile[width, height];

        float totalWidth = width * spacing;
        float totalHeight = height * spacing;
        float startX = -totalWidth * 0.5f;
        float startZ = -totalHeight * 0.5f;

        for (int x = 0; x < width; x++)
        {
            for (int z = 0; z < height; z++)
            {
                float cellMinX = startX + (x * spacing);
                float cellMinZ = startZ + (z * spacing);

                GridTile tile = new GridTile
                {
                    minX = cellMinX,
                    maxX = cellMinX + spacing,
                    minZ = cellMinZ,
                    maxZ = cellMinZ + spacing,
                    depth = 0
                };

                UpdateTileCornerVertices(tile);
                rootTiles[x, z] = tile;
            }
        }
    }

    public void UpdatePositionsAndHeights()
    {
        if (rootTiles == null || rootTiles.GetLength(0) != width || rootTiles.GetLength(1) != height)
        {
            GenerateGrid();
            return;
        }

        RebuildMeshFromLeaves();
    }

    /// <summary>
    /// Modifies terrain elevations using true 3D spherical distance from worldCenter,
    /// pre-subdividing stretched tiles within a 3D buffer radius.
    /// </summary>
    public void ModifyTerrainAt(Vector3 worldCenter, float radius, float heightDelta)
    {
        if (radius <= 0.001f) return;
        if (rootTiles == null) GenerateGrid();

        float widerRadius3D = radius * Mathf.Max(1f, subdivisionRadiusMultiplier);

        // 1. Pre-subdivide tiles in 3D before displacing vertices so new sub-tile
        // vertices sample the exact 3D spherical falloff curve.
        if (enableSubdivision && maxSubdivisionDepth > 0)
        {
            for (int d = 0; d < maxSubdivisionDepth; d++)
            {
                CollectLeaves();
                bool anySplit = false;

                for (int i = 0; i < leafTiles.Count; i++)
                {
                    GridTile tile = leafTiles[i];
                    if (tile.depth >= maxSubdivisionDepth) continue;

                    if (IsTileWithinSphere3D(tile, worldCenter, widerRadius3D))
                    {
                        if (ShouldTileSplitFor3DBlast(tile, worldCenter, radius, heightDelta))
                        {
                            SubdivideTile(tile);
                            anySplit = true;
                        }
                    }
                }

                if (!anySplit) break;
            }

            if (smoothSubdivisionTransitions)
            {
                BalanceQuadtreeGradation();
            }
        }

        // 2. Apply true 3D spherical displacement to leaf tile corners
        CollectLeaves();
        bool modified = false;

        for (int i = 0; i < leafTiles.Count; i++)
        {
            GridTile tile = leafTiles[i];
            if (!IsTileWithinSphere3D(tile, worldCenter, radius)) continue;

            UpdateTileCornerVertices(tile);

            bool tileChanged = false;
            tileChanged |= Apply3DRadialDelta(tile.wBL, ref tile.offsetBL, worldCenter, radius, heightDelta);
            tileChanged |= Apply3DRadialDelta(tile.wBR, ref tile.offsetBR, worldCenter, radius, heightDelta);
            tileChanged |= Apply3DRadialDelta(tile.wTL, ref tile.offsetTL, worldCenter, radius, heightDelta);
            tileChanged |= Apply3DRadialDelta(tile.wTR, ref tile.offsetTR, worldCenter, radius, heightDelta);

            if (tileChanged)
            {
                UpdateTileCornerVertices(tile);
                modified = true;
            }
        }

        // 3. Post-check for cumulative stretch across multiple hits
        if (modified && enableSubdivision && maxSubdivisionDepth > 0)
        {
            bool postSplit = false;
            for (int d = 0; d < maxSubdivisionDepth; d++)
            {
                CollectLeaves();
                bool passSplit = false;

                for (int i = 0; i < leafTiles.Count; i++)
                {
                    GridTile tile = leafTiles[i];
                    if (tile.depth < maxSubdivisionDepth && IsTileWithinSphere3D(tile, worldCenter, widerRadius3D))
                    {
                        if (ShouldTileSplitCurrent(tile))
                        {
                            SubdivideTile(tile);
                            passSplit = true;
                            postSplit = true;
                        }
                    }
                }

                if (!passSplit) break;
            }

            if (postSplit && smoothSubdivisionTransitions)
            {
                BalanceQuadtreeGradation();
            }
        }

        if (modified)
        {
            RebuildMeshFromLeaves();
        }
    }

    private bool Apply3DRadialDelta(Vector3 worldPt, ref float offset, Vector3 worldCenter, float radius, float heightDelta)
    {
        float dist3D = Vector3.Distance(worldPt, worldCenter);
        if (dist3D <= radius)
        {
            float factor = 0.5f * (1f + Mathf.Cos(Mathf.PI * (dist3D / radius)));
            offset += heightDelta * factor;
            return true;
        }
        return false;
    }

    private float Evaluate3DBlastDelta(Vector3 worldPt, Vector3 worldCenter, float radius, float heightDelta)
    {
        float dist3D = Vector3.Distance(worldPt, worldCenter);
        if (dist3D >= radius) return 0f;
        return heightDelta * 0.5f * (1f + Mathf.Cos(Mathf.PI * (dist3D / radius)));
    }

    private bool ShouldTileSplitFor3DBlast(GridTile tile, Vector3 worldCenter, float radius, float heightDelta)
    {
        UpdateTileCornerVertices(tile);

        float dBL = Evaluate3DBlastDelta(tile.wBL, worldCenter, radius, heightDelta);
        float dBR = Evaluate3DBlastDelta(tile.wBR, worldCenter, radius, heightDelta);
        float dTL = Evaluate3DBlastDelta(tile.wTL, worldCenter, radius, heightDelta);
        float dTR = Evaluate3DBlastDelta(tile.wTR, worldCenter, radius, heightDelta);

        Vector3 worldTileCenter = (tile.wBL + tile.wBR + tile.wTL + tile.wTR) * 0.25f;
        float dCenter = Evaluate3DBlastDelta(worldTileCenter, worldCenter, radius, heightDelta);

        // If this 3D blast barely touches the tile at all, do not pre-split it
        float maxBlastImpact = Mathf.Max(
            Mathf.Max(Mathf.Abs(dBL), Mathf.Abs(dBR)),
            Mathf.Max(Mathf.Max(Mathf.Abs(dTL), Mathf.Abs(dTR)), Mathf.Abs(dCenter))
        );
        if (maxBlastImpact < 0.005f) return false;

        // Measure predicted deformation height (excluding base Perlin noise unless subdivideBaseNoise is enabled)
        float hBL = (subdivideBaseNoise ? tile.vBL.y : tile.offsetBL) + dBL;
        float hBR = (subdivideBaseNoise ? tile.vBR.y : tile.offsetBR) + dBR;
        float hTL = (subdivideBaseNoise ? tile.vTL.y : tile.offsetTL) + dTL;
        float hTR = (subdivideBaseNoise ? tile.vTR.y : tile.offsetTR) + dTR;

        float baseCenterH = subdivideBaseNoise
            ? (tile.vBL.y + tile.vBR.y + tile.vTL.y + tile.vTR.y) * 0.25f
            : (tile.offsetBL + tile.offsetBR + tile.offsetTL + tile.offsetTR) * 0.25f;
        float hCenter = baseCenterH + dCenter;

        return ExceedsStretchThreshold(tile, hBL, hBR, hTL, hTR, hCenter);
    }

    private bool ShouldTileSplitCurrent(GridTile tile)
    {
        UpdateTileCornerVertices(tile);

        float hBL = subdivideBaseNoise ? tile.vBL.y : tile.offsetBL;
        float hBR = subdivideBaseNoise ? tile.vBR.y : tile.offsetBR;
        float hTL = subdivideBaseNoise ? tile.vTL.y : tile.offsetTL;
        float hTR = subdivideBaseNoise ? tile.vTR.y : tile.offsetTR;
        float hCenter = (hBL + hBR + hTL + hTR) * 0.25f;

        return ExceedsStretchThreshold(tile, hBL, hBR, hTL, hTR, hCenter);
    }

    private bool ExceedsStretchThreshold(GridTile tile, float hBL, float hBR, float hTL, float hTR, float hCenter)
    {
        float tileSize = Mathf.Max(0.001f, tile.maxX - tile.minX);

        float minH = Mathf.Min(Mathf.Min(hBL, hBR), Mathf.Min(hTL, hTR));
        float maxH = Mathf.Max(Mathf.Max(hBL, hBR), Mathf.Max(hTL, hTR));
        float cornerSpan = maxH - minH;

        float avgCornerH = (hBL + hBR + hTL + hTR) * 0.25f;
        float centerDeviation = Mathf.Abs(hCenter - avgCornerH) * 2f;
        float diagonalTwist = Mathf.Abs((hBL + hTR) - (hBR + hTL));

        // Normalized slope across this tile (delta height / tile width)
        float effectiveDeltaH = Mathf.Max(cornerSpan, Mathf.Max(centerDeviation, diagonalTwist));
        float slope = effectiveDeltaH / tileSize;

        // Require progressively steeper slopes for deeper subdivisions
        float requiredSlope = stretchSlopeThreshold * Mathf.Pow(depthSlopeMultiplier, tile.depth);
        return slope > requiredSlope;
    }

    /// <summary>
    /// Checks 3D intersection between a sphere at worldCenter and the tile's 3D bounding box.
    /// </summary>
    private bool IsTileWithinSphere3D(GridTile tile, Vector3 worldCenter, float radius)
    {
        UpdateTileCornerVertices(tile);

        Vector3 minB = Vector3.Min(Vector3.Min(tile.wBL, tile.wBR), Vector3.Min(tile.wTL, tile.wTR));
        Vector3 maxB = Vector3.Max(Vector3.Max(tile.wBL, tile.wBR), Vector3.Max(tile.wTL, tile.wTR));

        // Expand slightly in XZ to cover the tile's full cell border
        float padXZ = (tile.maxX - tile.minX) * 0.15f;
        minB.x -= padXZ; minB.z -= padXZ;
        maxB.x += padXZ; maxB.z += padXZ;

        float closestX = Mathf.Clamp(worldCenter.x, minB.x, maxB.x);
        float closestY = Mathf.Clamp(worldCenter.y, minB.y, maxB.y);
        float closestZ = Mathf.Clamp(worldCenter.z, minB.z, maxB.z);

        float distSq = (new Vector3(closestX, closestY, closestZ) - worldCenter).sqrMagnitude;
        return distSq <= (radius * radius);
    }

    private void SubdivideTile(GridTile parent)
    {
        if (!parent.IsLeaf || parent.depth >= maxSubdivisionDepth) return;

        UpdateTileCornerVertices(parent);

        float midX = (parent.minX + parent.maxX) * 0.5f;
        float midZ = (parent.minZ + parent.maxZ) * 0.5f;
        int nextDepth = parent.depth + 1;

        parent.children = new GridTile[4];
        parent.children[0] = CreateChildTile(parent, parent.minX, midX, parent.minZ, midZ, nextDepth);
        parent.children[1] = CreateChildTile(parent, midX, parent.maxX, parent.minZ, midZ, nextDepth);
        parent.children[2] = CreateChildTile(parent, parent.minX, midX, midZ, parent.maxZ, nextDepth);
        parent.children[3] = CreateChildTile(parent, midX, parent.maxX, midZ, parent.maxZ, nextDepth);
    }

    private GridTile CreateChildTile(GridTile parent, float cMinX, float cMaxX, float cMinZ, float cMaxZ, int depth)
    {
        GridTile child = new GridTile
        {
            minX = cMinX,
            maxX = cMaxX,
            minZ = cMinZ,
            maxZ = cMaxZ,
            depth = depth
        };

        GetQuadBounds(parent, out float pLeft, out float pRight, out float pBottom, out float pTop);
        GetQuadBounds(child, out float cLeft, out float cRight, out float cBottom, out float cTop);

        float uLeft = Mathf.Clamp01((cLeft - pLeft) / Mathf.Max(0.0001f, pRight - pLeft));
        float uRight = Mathf.Clamp01((cRight - pLeft) / Mathf.Max(0.0001f, pRight - pLeft));
        float vBottom = Mathf.Clamp01((cBottom - pBottom) / Mathf.Max(0.0001f, pTop - pBottom));
        float vTop = Mathf.Clamp01((cTop - pBottom) / Mathf.Max(0.0001f, pTop - pBottom));

        child.offsetBL = BilinearLerp(parent.offsetBL, parent.offsetBR, parent.offsetTL, parent.offsetTR, uLeft, vBottom);
        child.offsetBR = BilinearLerp(parent.offsetBL, parent.offsetBR, parent.offsetTL, parent.offsetTR, uRight, vBottom);
        child.offsetTL = BilinearLerp(parent.offsetBL, parent.offsetBR, parent.offsetTL, parent.offsetTR, uLeft, vTop);
        child.offsetTR = BilinearLerp(parent.offsetBL, parent.offsetBR, parent.offsetTL, parent.offsetTR, uRight, vTop);

        UpdateTileCornerVertices(child);
        return child;
    }

    private static float BilinearLerp(float bl, float br, float tl, float tr, float u, float v)
    {
        float bot = Mathf.Lerp(bl, br, u);
        float top = Mathf.Lerp(tl, tr, u);
        return Mathf.Lerp(bot, top, v);
    }

    private void BalanceQuadtreeGradation()
    {
        bool changed = true;
        int safety = 0;

        while (changed && safety < 8)
        {
            changed = false;
            safety++;
            CollectLeaves();

            for (int i = 0; i < leafTiles.Count; i++)
            {
                GridTile tile = leafTiles[i];
                if (!tile.IsLeaf || tile.depth >= maxSubdivisionDepth - 1) continue;

                float margin = (tile.maxX - tile.minX) * 0.15f;

                for (int j = 0; j < leafTiles.Count; j++)
                {
                    GridTile other = leafTiles[j];
                    if (other.depth <= tile.depth + 1) continue;

                    if (other.maxX >= tile.minX - margin && other.minX <= tile.maxX + margin &&
                        other.maxZ >= tile.minZ - margin && other.maxZ <= tile.maxZ + margin)
                    {
                        SubdivideTile(tile);
                        changed = true;
                        break;
                    }
                }
            }
        }
    }

    private void GetQuadBounds(GridTile tile, out float left, out float right, out float bottom, out float top)
    {
        float tileWidth = tile.maxX - tile.minX;
        float tileHeight = tile.maxZ - tile.minZ;

        float fillRatioX = Mathf.Clamp01(quadSize.x / Mathf.Max(0.0001f, spacing));
        float fillRatioZ = Mathf.Clamp01(quadSize.y / Mathf.Max(0.0001f, spacing));

        float insetX = tileWidth * (1f - fillRatioX) * 0.5f;
        float insetZ = tileHeight * (1f - fillRatioZ) * 0.5f;

        left = tile.minX + insetX;
        right = tile.maxX - insetX;
        bottom = tile.minZ + insetZ;
        top = tile.maxZ - insetZ;
    }

    private void UpdateTileCornerVertices(GridTile tile)
    {
        GetQuadBounds(tile, out float left, out float right, out float bottom, out float top);

        Vector3 blFlatWorld = transform.TransformPoint(new Vector3(left, 0f, bottom));
        Vector3 brFlatWorld = transform.TransformPoint(new Vector3(right, 0f, bottom));
        Vector3 tlFlatWorld = transform.TransformPoint(new Vector3(left, 0f, top));
        Vector3 trFlatWorld = transform.TransformPoint(new Vector3(right, 0f, top));

        float yBL = SampleHeight(blFlatWorld.x, blFlatWorld.z) + tile.offsetBL;
        float yBR = SampleHeight(brFlatWorld.x, brFlatWorld.z) + tile.offsetBR;
        float yTL = SampleHeight(tlFlatWorld.x, tlFlatWorld.z) + tile.offsetTL;
        float yTR = SampleHeight(trFlatWorld.x, trFlatWorld.z) + tile.offsetTR;

        tile.vBL = new Vector3(left, yBL, bottom);
        tile.vBR = new Vector3(right, yBR, bottom);
        tile.vTL = new Vector3(left, yTL, top);
        tile.vTR = new Vector3(right, yTR, top);

        tile.wBL = transform.TransformPoint(tile.vBL);
        tile.wBR = transform.TransformPoint(tile.vBR);
        tile.wTL = transform.TransformPoint(tile.vTL);
        tile.wTR = transform.TransformPoint(tile.vTR);
    }

    private void CollectLeaves()
    {
        leafTiles.Clear();
        if (rootTiles == null) return;

        for (int x = 0; x < width; x++)
        {
            for (int z = 0; z < height; z++)
            {
                if (rootTiles[x, z] != null)
                {
                    CollectLeavesRecursive(rootTiles[x, z]);
                }
            }
        }
    }

    private void CollectLeavesRecursive(GridTile tile)
    {
        if (tile.IsLeaf)
        {
            leafTiles.Add(tile);
            return;
        }

        for (int i = 0; i < 4; i++)
        {
            CollectLeavesRecursive(tile.children[i]);
        }
    }

    private void RebuildMeshFromLeaves()
    {
        CollectLeaves();

        int leafCount = leafTiles.Count;
        vertices.Clear();
        uvs.Clear();
        triangles.Clear();

        if (vertices.Capacity < leafCount * 4) vertices.Capacity = leafCount * 4;
        if (uvs.Capacity < leafCount * 4) uvs.Capacity = leafCount * 4;
        if (triangles.Capacity < leafCount * 6) triangles.Capacity = leafCount * 6;

        int vertIndex = 0;

        for (int i = 0; i < leafCount; i++)
        {
            GridTile tile = leafTiles[i];
            UpdateTileCornerVertices(tile);

            vertices.Add(tile.vBL);
            vertices.Add(tile.vBR);
            vertices.Add(tile.vTL);
            vertices.Add(tile.vTR);

            uvs.Add(new Vector2(0f, 0f));
            uvs.Add(new Vector2(1f, 0f));
            uvs.Add(new Vector2(0f, 1f));
            uvs.Add(new Vector2(1f, 1f));

            triangles.Add(vertIndex + 0);
            triangles.Add(vertIndex + 2);
            triangles.Add(vertIndex + 1);
            triangles.Add(vertIndex + 2);
            triangles.Add(vertIndex + 3);
            triangles.Add(vertIndex + 1);

            vertIndex += 4;
        }

        gridMesh.Clear();
        gridMesh.SetVertices(vertices);
        gridMesh.SetUVs(0, uvs);
        gridMesh.SetTriangles(triangles, 0);
        gridMesh.RecalculateNormals();
        gridMesh.RecalculateBounds();
    }

    [ContextMenu("Reset Modified Terrain Heights")]
    public void ResetTerrainModifications()
    {
        GenerateGrid();
    }

    private float SampleHeight(float worldX, float worldZ)
    {
        float sampleX = (worldX + CoordinateBaseOffset + noiseOffset.x) * noiseScale;
        float sampleZ = (worldZ + CoordinateBaseOffset + noiseOffset.y) * noiseScale;

        return Mathf.Lerp(minHeight, maxHeight, Mathf.PerlinNoise(sampleX, sampleZ));
    }

    private void ClearLegacyChildren()
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