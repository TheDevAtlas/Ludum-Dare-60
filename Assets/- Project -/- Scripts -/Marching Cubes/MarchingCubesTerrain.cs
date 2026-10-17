using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using static Unity.Mathematics.math;

[ExecuteAlways]
[DisallowMultipleComponent]
[RequireComponent(typeof(Transform))]
public sealed class MarchingCubesTerrain : MonoBehaviour
{
    [Serializable]
    struct TerrainStamp
    {
        public Vector3 localCenter;
        public float radius;
        public float densityDelta;
        public Color tint;
        public float tintBlend;
        public float eventRadius;
        public float eventWidth;
        public float eventStrength;
    }

    struct VoxelSample
    {
        public float density;
        public float4 color;
    }

    struct VertexData
    {
        public float3 position;
        public float3 normal;
        public float4 color;
    }

    struct Triangle
    {
        public VertexData a;
        public VertexData b;
        public VertexData c;
    }

    struct EditStampData
    {
        public float3 localCenter;
        public float radius;
        public float densityDelta;
        public float4 tint;
        public float tintBlend;
        public float eventRadius;
        public float eventWidth;
        public float eventStrength;
    }

    [Header("Chunk")]
    [SerializeField, Min(2)] int cellsX = 30;
    [SerializeField, Min(2)] int cellsY = 10;
    [SerializeField, Min(2)] int cellsZ = 30;
    [SerializeField, Min(0.01f)] float cellSize = 0.5f;

    [Header("Noise")]
    [SerializeField] Vector2 noiseScrollDirection = new(1f, 0.35f);
    [SerializeField, Min(0f)] float noiseScrollSpeed = 0.25f;
    [SerializeField, Min(0.001f)] float noiseScale = 0.14f;
    [SerializeField, Min(0f)] float minSurfaceHeight = 1.0f;
    [SerializeField, Min(0f)] float maxSurfaceHeight = 4.3f;
    [SerializeField, Range(0f, 1f)] float detailContribution = 0.35f;

    [Header("Visuals")]
    [SerializeField] Color lowTint = new(0.12f, 0.87f, 1f, 0.66f);
    [SerializeField] Color highTint = new(1f, 0.55f, 0.25f, 0.92f);
    [SerializeField] Color accentTint = new(0.55f, 1f, 0.95f, 1f);
    [SerializeField] Color eventTint = new(1f, 0.35f, 0.1f, 1f);
    [SerializeField, Min(0f)] float gridScale = 1.2f;
    [SerializeField, Range(0.001f, 0.25f)] float gridLineWidth = 0.055f;
    [SerializeField, Min(0f)] float contourScale = 1.8f;
    [SerializeField, Range(0.001f, 0.25f)] float contourLineWidth = 0.04f;
    [SerializeField, Min(0f)] float emissionStrength = 1.4f;
    [SerializeField, Min(0f)] float edgeGlowStrength = 1.0f;
    [Tooltip("How strongly walls use the vertical low->high height gradient instead of their column's surface color.")]
    [SerializeField, Range(0f, 1f)] float wallGradientStrength = 1.0f;

    [Header("Runtime")]
    [SerializeField] bool rebuildEveryFrame = true;
    [SerializeField] bool useCustomMaterial = true;
    [SerializeField] Material materialOverride;

    const string GeneratedMeshName = "Marching Cubes Terrain Mesh";
    const string GeneratedMaterialName = "Marching Cubes Terrain Material (Generated)";
    const MeshUpdateFlags FastMeshUpdate = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices;

    static readonly VertexAttributeDescriptor[] VertexLayout =
    {
        new(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
        new(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
        new(VertexAttribute.Color, VertexAttributeFormat.Float32, 4),
    };

    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int LowTintId = Shader.PropertyToID("_LowTint");
    static readonly int HighTintId = Shader.PropertyToID("_HighTint");
    static readonly int AccentTintId = Shader.PropertyToID("_AccentTint");
    static readonly int EventTintId = Shader.PropertyToID("_EventTint");
    static readonly int GridScaleId = Shader.PropertyToID("_GridScale");
    static readonly int GridLineWidthId = Shader.PropertyToID("_GridLineWidth");
    static readonly int ContourScaleId = Shader.PropertyToID("_ContourScale");
    static readonly int ContourLineWidthId = Shader.PropertyToID("_ContourLineWidth");
    static readonly int EmissionStrengthId = Shader.PropertyToID("_EmissionStrength");
    static readonly int EdgeGlowStrengthId = Shader.PropertyToID("_EdgeGlowStrength");
    static readonly int ChunkHalfExtentsId = Shader.PropertyToID("_ChunkHalfExtents");
    static readonly int MinSurfaceHeightId = Shader.PropertyToID("_MinSurfaceHeight");
    static readonly int MaxSurfaceHeightId = Shader.PropertyToID("_MaxSurfaceHeight");
    static readonly int WallGradientStrengthId = Shader.PropertyToID("_WallGradientStrength");
    static readonly int EventCenterRadiusId = Shader.PropertyToID("_EventCenterRadius");
    static readonly int EventWidthId = Shader.PropertyToID("_EventWidth");
    static readonly int EventStrengthId = Shader.PropertyToID("_EventStrength");
    static readonly int EventPulseId = Shader.PropertyToID("_EventPulse");

    MeshFilter meshFilter;
    MeshRenderer meshRenderer;
    Mesh mesh;
    Material generatedMaterial;
    MaterialPropertyBlock propertyBlock;

    readonly List<TerrainStamp> stamps = new();
    Vector2 scrollOffset;
    bool dirty = true;
    float lastRebuildTime;

    public Vector3 NoiseSampleWorldPosition
    {
        get
        {
            float scale = Mathf.Max(noiseScale, 0.001f);
            Vector3 localPosition = new(scrollOffset.x / scale, 0f, scrollOffset.y / scale);
            return transform.TransformPoint(localPosition);
        }
    }

    // Persistent job buffers, reused every rebuild so scrolling doesn't allocate.
    NativeArray<VoxelSample> samples;
    NativeList<EditStampData> editStamps;
    NativeList<Triangle> triangles;
    int3 allocatedSampleCounts;
    int meshVertexCapacity;
    JobHandle pendingJob;
    bool jobPending;
    Bounds pendingBounds;

    static readonly int3[] CornerOffsets =
    {
        new(0, 0, 0),
        new(1, 0, 0),
        new(1, 1, 0),
        new(0, 1, 0),
        new(0, 0, 1),
        new(1, 0, 1),
        new(1, 1, 1),
        new(0, 1, 1),
    };

    static readonly int2[] EdgeCorners =
    {
        new(0, 1),
        new(1, 2),
        new(2, 3),
        new(3, 0),
        new(4, 5),
        new(5, 6),
        new(6, 7),
        new(7, 4),
        new(0, 4),
        new(1, 5),
        new(2, 6),
        new(3, 7),
    };

    static readonly int[] EdgeTable = new int[256] {
            0x0  , 0x109, 0x203, 0x30a, 0x406, 0x50f, 0x605, 0x70c,
            0x80c, 0x905, 0xa0f, 0xb06, 0xc0a, 0xd03, 0xe09, 0xf00,
            0x190, 0x99 , 0x393, 0x29a, 0x596, 0x49f, 0x795, 0x69c,
            0x99c, 0x895, 0xb9f, 0xa96, 0xd9a, 0xc93, 0xf99, 0xe90,
            0x230, 0x339, 0x33 , 0x13a, 0x636, 0x73f, 0x435, 0x53c,
            0xa3c, 0xb35, 0x83f, 0x936, 0xe3a, 0xf33, 0xc39, 0xd30,
            0x3a0, 0x2a9, 0x1a3, 0xaa , 0x7a6, 0x6af, 0x5a5, 0x4ac,
            0xbac, 0xaa5, 0x9af, 0x8a6, 0xfaa, 0xea3, 0xda9, 0xca0,
            0x460, 0x569, 0x663, 0x76a, 0x66 , 0x16f, 0x265, 0x36c,
            0xc6c, 0xd65, 0xe6f, 0xf66, 0x86a, 0x963, 0xa69, 0xb60,
            0x5f0, 0x4f9, 0x7f3, 0x6fa, 0x1f6, 0xff , 0x3f5, 0x2fc,
            0xdfc, 0xcf5, 0xfff, 0xef6, 0x9fa, 0x8f3, 0xbf9, 0xaf0,
            0x650, 0x759, 0x453, 0x55a, 0x256, 0x35f, 0x55 , 0x15c,
            0xe5c, 0xf55, 0xc5f, 0xd56, 0xa5a, 0xb53, 0x859, 0x950,
            0x7c0, 0x6c9, 0x5c3, 0x4ca, 0x3c6, 0x2cf, 0x1c5, 0xcc ,
            0xfcc, 0xec5, 0xdcf, 0xcc6, 0xbca, 0xac3, 0x9c9, 0x8c0,
            0x8c0, 0x9c9, 0xac3, 0xbca, 0xcc6, 0xdcf, 0xec5, 0xfcc,
            0xcc , 0x1c5, 0x2cf, 0x3c6, 0x4ca, 0x5c3, 0x6c9, 0x7c0,
            0x950, 0x859, 0xb53, 0xa5a, 0xd56, 0xc5f, 0xf55, 0xe5c,
            0x15c, 0x55 , 0x35f, 0x256, 0x55a, 0x453, 0x759, 0x650,
            0xaf0, 0xbf9, 0x8f3, 0x9fa, 0xef6, 0xfff, 0xcf5, 0xdfc,
            0x2fc, 0x3f5, 0xff , 0x1f6, 0x6fa, 0x7f3, 0x4f9, 0x5f0,
            0xb60, 0xa69, 0x963, 0x86a, 0xf66, 0xe6f, 0xd65, 0xc6c,
            0x36c, 0x265, 0x16f, 0x66 , 0x76a, 0x663, 0x569, 0x460,
            0xca0, 0xda9, 0xea3, 0xfaa, 0x8a6, 0x9af, 0xaa5, 0xbac,
            0x4ac, 0x5a5, 0x6af, 0x7a6, 0xaa , 0x1a3, 0x2a9, 0x3a0,
            0xd30, 0xc39, 0xf33, 0xe3a, 0x936, 0x83f, 0xb35, 0xa3c,
            0x53c, 0x435, 0x73f, 0x636, 0x13a, 0x33 , 0x339, 0x230,
            0xe90, 0xf99, 0xc93, 0xd9a, 0xa96, 0xb9f, 0x895, 0x99c,
            0x69c, 0x795, 0x49f, 0x596, 0x29a, 0x393, 0x99 , 0x190,
            0xf00, 0xe09, 0xd03, 0xc0a, 0xb06, 0xa0f, 0x905, 0x80c,
            0x70c, 0x605, 0x50f, 0x406, 0x30a, 0x203, 0x109, 0x0   };

        static readonly int[] TriTable = new int[]
        {
            -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            0, 8, 3, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            0, 1, 9, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            1, 8, 3, 9, 8, 1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            1, 2, 10, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            0, 8, 3, 1, 2, 10, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            9, 2, 10, 0, 2, 9, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            2, 8, 3, 2, 10, 8, 10, 9, 8, -1, -1, -1, -1, -1, -1, -1,
            3, 11, 2, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            0, 11, 2, 8, 11, 0, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            1, 9, 0, 2, 3, 11, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            1, 11, 2, 1, 9, 11, 9, 8, 11, -1, -1, -1, -1, -1, -1, -1,
            3, 10, 1, 11, 10, 3, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            0, 10, 1, 0, 8, 10, 8, 11, 10, -1, -1, -1, -1, -1, -1, -1,
            3, 9, 0, 3, 11, 9, 11, 10, 9, -1, -1, -1, -1, -1, -1, -1,
            9, 8, 10, 10, 8, 11, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            4, 7, 8, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            4, 3, 0, 7, 3, 4, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            0, 1, 9, 8, 4, 7, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            4, 1, 9, 4, 7, 1, 7, 3, 1, -1, -1, -1, -1, -1, -1, -1,
            1, 2, 10, 8, 4, 7, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            3, 4, 7, 3, 0, 4, 1, 2, 10, -1, -1, -1, -1, -1, -1, -1,
            9, 2, 10, 9, 0, 2, 8, 4, 7, -1, -1, -1, -1, -1, -1, -1,
            2, 10, 9, 2, 9, 7, 2, 7, 3, 7, 9, 4, -1, -1, -1, -1,
            8, 4, 7, 3, 11, 2, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            11, 4, 7, 11, 2, 4, 2, 0, 4, -1, -1, -1, -1, -1, -1, -1,
            9, 0, 1, 8, 4, 7, 2, 3, 11, -1, -1, -1, -1, -1, -1, -1,
            4, 7, 11, 9, 4, 11, 9, 11, 2, 9, 2, 1, -1, -1, -1, -1,
            3, 10, 1, 3, 11, 10, 7, 8, 4, -1, -1, -1, -1, -1, -1, -1,
            1, 11, 10, 1, 4, 11, 1, 0, 4, 7, 11, 4, -1, -1, -1, -1,
            4, 7, 8, 9, 0, 11, 9, 11, 10, 11, 0, 3, -1, -1, -1, -1,
            4, 7, 11, 4, 11, 9, 9, 11, 10, -1, -1, -1, -1, -1, -1, -1,
            9, 5, 4, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            9, 5, 4, 0, 8, 3, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            0, 5, 4, 1, 5, 0, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            8, 5, 4, 8, 3, 5, 3, 1, 5, -1, -1, -1, -1, -1, -1, -1,
            1, 2, 10, 9, 5, 4, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            3, 0, 8, 1, 2, 10, 4, 9, 5, -1, -1, -1, -1, -1, -1, -1,
            5, 2, 10, 5, 4, 2, 4, 0, 2, -1, -1, -1, -1, -1, -1, -1,
            2, 10, 5, 3, 2, 5, 3, 5, 4, 3, 4, 8, -1, -1, -1, -1,
            9, 5, 4, 2, 3, 11, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            0, 11, 2, 0, 8, 11, 4, 9, 5, -1, -1, -1, -1, -1, -1, -1,
            0, 5, 4, 0, 1, 5, 2, 3, 11, -1, -1, -1, -1, -1, -1, -1,
            2, 1, 5, 2, 5, 8, 2, 8, 11, 4, 8, 5, -1, -1, -1, -1,
            10, 3, 11, 10, 1, 3, 9, 5, 4, -1, -1, -1, -1, -1, -1, -1,
            4, 9, 5, 0, 8, 1, 8, 10, 1, 8, 11, 10, -1, -1, -1, -1,
            5, 4, 0, 5, 0, 11, 5, 11, 10, 11, 0, 3, -1, -1, -1, -1,
            5, 4, 8, 5, 8, 10, 10, 8, 11, -1, -1, -1, -1, -1, -1, -1,
            9, 7, 8, 5, 7, 9, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            9, 3, 0, 9, 5, 3, 5, 7, 3, -1, -1, -1, -1, -1, -1, -1,
            0, 7, 8, 0, 1, 7, 1, 5, 7, -1, -1, -1, -1, -1, -1, -1,
            1, 5, 3, 3, 5, 7, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            9, 7, 8, 9, 5, 7, 10, 1, 2, -1, -1, -1, -1, -1, -1, -1,
            10, 1, 2, 9, 5, 0, 5, 3, 0, 5, 7, 3, -1, -1, -1, -1,
            8, 0, 2, 8, 2, 5, 8, 5, 7, 10, 5, 2, -1, -1, -1, -1,
            2, 10, 5, 2, 5, 3, 3, 5, 7, -1, -1, -1, -1, -1, -1, -1,
            7, 9, 5, 7, 8, 9, 3, 11, 2, -1, -1, -1, -1, -1, -1, -1,
            9, 5, 7, 9, 7, 2, 9, 2, 0, 2, 7, 11, -1, -1, -1, -1,
            2, 3, 11, 0, 1, 8, 1, 7, 8, 1, 5, 7, -1, -1, -1, -1,
            11, 2, 1, 11, 1, 7, 7, 1, 5, -1, -1, -1, -1, -1, -1, -1,
            9, 5, 8, 8, 5, 7, 10, 1, 3, 10, 3, 11, -1, -1, -1, -1,
            5, 7, 0, 5, 0, 9, 7, 11, 0, 1, 0, 10, 11, 10, 0, -1,
            11, 10, 0, 11, 0, 3, 10, 5, 0, 8, 0, 7, 5, 7, 0, -1,
            11, 10, 5, 7, 11, 5, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            10, 6, 5, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            0, 8, 3, 5, 10, 6, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            9, 0, 1, 5, 10, 6, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            1, 8, 3, 1, 9, 8, 5, 10, 6, -1, -1, -1, -1, -1, -1, -1,
            1, 6, 5, 2, 6, 1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            1, 6, 5, 1, 2, 6, 3, 0, 8, -1, -1, -1, -1, -1, -1, -1,
            9, 6, 5, 9, 0, 6, 0, 2, 6, -1, -1, -1, -1, -1, -1, -1,
            5, 9, 8, 5, 8, 2, 5, 2, 6, 3, 2, 8, -1, -1, -1, -1,
            2, 3, 11, 10, 6, 5, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            11, 0, 8, 11, 2, 0, 10, 6, 5, -1, -1, -1, -1, -1, -1, -1,
            0, 1, 9, 2, 3, 11, 5, 10, 6, -1, -1, -1, -1, -1, -1, -1,
            5, 10, 6, 1, 9, 2, 9, 11, 2, 9, 8, 11, -1, -1, -1, -1,
            6, 3, 11, 6, 5, 3, 5, 1, 3, -1, -1, -1, -1, -1, -1, -1,
            0, 8, 11, 0, 11, 5, 0, 5, 1, 5, 11, 6, -1, -1, -1, -1,
            3, 11, 6, 0, 3, 6, 0, 6, 5, 0, 5, 9, -1, -1, -1, -1,
            6, 5, 9, 6, 9, 11, 11, 9, 8, -1, -1, -1, -1, -1, -1, -1,
            5, 10, 6, 4, 7, 8, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            4, 3, 0, 4, 7, 3, 6, 5, 10, -1, -1, -1, -1, -1, -1, -1,
            1, 9, 0, 5, 10, 6, 8, 4, 7, -1, -1, -1, -1, -1, -1, -1,
            10, 6, 5, 1, 9, 7, 1, 7, 3, 7, 9, 4, -1, -1, -1, -1,
            6, 1, 2, 6, 5, 1, 4, 7, 8, -1, -1, -1, -1, -1, -1, -1,
            1, 2, 5, 5, 2, 6, 3, 0, 4, 3, 4, 7, -1, -1, -1, -1,
            8, 4, 7, 9, 0, 5, 0, 6, 5, 0, 2, 6, -1, -1, -1, -1,
            7, 3, 9, 7, 9, 4, 3, 2, 9, 5, 9, 6, 2, 6, 9, -1,
            3, 11, 2, 7, 8, 4, 10, 6, 5, -1, -1, -1, -1, -1, -1, -1,
            5, 10, 6, 4, 7, 2, 4, 2, 0, 2, 7, 11, -1, -1, -1, -1,
            0, 1, 9, 4, 7, 8, 2, 3, 11, 5, 10, 6, -1, -1, -1, -1,
            9, 2, 1, 9, 11, 2, 9, 4, 11, 7, 11, 4, 5, 10, 6, -1,
            8, 4, 7, 3, 11, 5, 3, 5, 1, 5, 11, 6, -1, -1, -1, -1,
            5, 1, 11, 5, 11, 6, 1, 0, 11, 7, 11, 4, 0, 4, 11, -1,
            0, 5, 9, 0, 6, 5, 0, 3, 6, 11, 6, 3, 8, 4, 7, -1,
            6, 5, 9, 6, 9, 11, 4, 7, 9, 7, 11, 9, -1, -1, -1, -1,
            10, 4, 9, 6, 4, 10, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            4, 10, 6, 4, 9, 10, 0, 8, 3, -1, -1, -1, -1, -1, -1, -1,
            10, 0, 1, 10, 6, 0, 6, 4, 0, -1, -1, -1, -1, -1, -1, -1,
            8, 3, 1, 8, 1, 6, 8, 6, 4, 6, 1, 10, -1, -1, -1, -1,
            1, 4, 9, 1, 2, 4, 2, 6, 4, -1, -1, -1, -1, -1, -1, -1,
            3, 0, 8, 1, 2, 9, 2, 4, 9, 2, 6, 4, -1, -1, -1, -1,
            0, 2, 4, 4, 2, 6, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            8, 3, 2, 8, 2, 4, 4, 2, 6, -1, -1, -1, -1, -1, -1, -1,
            10, 4, 9, 10, 6, 4, 11, 2, 3, -1, -1, -1, -1, -1, -1, -1,
            0, 8, 2, 2, 8, 11, 4, 9, 10, 4, 10, 6, -1, -1, -1, -1,
            3, 11, 2, 0, 1, 6, 0, 6, 4, 6, 1, 10, -1, -1, -1, -1,
            6, 4, 1, 6, 1, 10, 4, 8, 1, 2, 1, 11, 8, 11, 1, -1,
            9, 6, 4, 9, 3, 6, 9, 1, 3, 11, 6, 3, -1, -1, -1, -1,
            8, 11, 1, 8, 1, 0, 11, 6, 1, 9, 1, 4, 6, 4, 1, -1,
            3, 11, 6, 3, 6, 0, 0, 6, 4, -1, -1, -1, -1, -1, -1, -1,
            6, 4, 8, 11, 6, 8, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            7, 10, 6, 7, 8, 10, 8, 9, 10, -1, -1, -1, -1, -1, -1, -1,
            0, 7, 3, 0, 10, 7, 0, 9, 10, 6, 7, 10, -1, -1, -1, -1,
            10, 6, 7, 1, 10, 7, 1, 7, 8, 1, 8, 0, -1, -1, -1, -1,
            10, 6, 7, 10, 7, 1, 1, 7, 3, -1, -1, -1, -1, -1, -1, -1,
            1, 2, 6, 1, 6, 8, 1, 8, 9, 8, 6, 7, -1, -1, -1, -1,
            2, 6, 9, 2, 9, 1, 6, 7, 9, 0, 9, 3, 7, 3, 9, -1,
            7, 8, 0, 7, 0, 6, 6, 0, 2, -1, -1, -1, -1, -1, -1, -1,
            7, 3, 2, 6, 7, 2, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            2, 3, 11, 10, 6, 8, 10, 8, 9, 8, 6, 7, -1, -1, -1, -1,
            2, 0, 7, 2, 7, 11, 0, 9, 7, 6, 7, 10, 9, 10, 7, -1,
            1, 8, 0, 1, 7, 8, 1, 10, 7, 6, 7, 10, 2, 3, 11, -1,
            11, 2, 1, 11, 1, 7, 10, 6, 1, 6, 7, 1, -1, -1, -1, -1,
            8, 9, 6, 8, 6, 7, 9, 1, 6, 11, 6, 3, 1, 3, 6, -1,
            0, 9, 1, 11, 6, 7, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            7, 8, 0, 7, 0, 6, 3, 11, 0, 11, 6, 0, -1, -1, -1, -1,
            7, 11, 6, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            7, 6, 11, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            3, 0, 8, 11, 7, 6, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            0, 1, 9, 11, 7, 6, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            8, 1, 9, 8, 3, 1, 11, 7, 6, -1, -1, -1, -1, -1, -1, -1,
            10, 1, 2, 6, 11, 7, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            1, 2, 10, 3, 0, 8, 6, 11, 7, -1, -1, -1, -1, -1, -1, -1,
            2, 9, 0, 2, 10, 9, 6, 11, 7, -1, -1, -1, -1, -1, -1, -1,
            6, 11, 7, 2, 10, 3, 10, 8, 3, 10, 9, 8, -1, -1, -1, -1,
            7, 2, 3, 6, 2, 7, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            7, 0, 8, 7, 6, 0, 6, 2, 0, -1, -1, -1, -1, -1, -1, -1,
            2, 7, 6, 2, 3, 7, 0, 1, 9, -1, -1, -1, -1, -1, -1, -1,
            1, 6, 2, 1, 8, 6, 1, 9, 8, 8, 7, 6, -1, -1, -1, -1,
            10, 7, 6, 10, 1, 7, 1, 3, 7, -1, -1, -1, -1, -1, -1, -1,
            10, 7, 6, 1, 7, 10, 1, 8, 7, 1, 0, 8, -1, -1, -1, -1,
            0, 3, 7, 0, 7, 10, 0, 10, 9, 6, 10, 7, -1, -1, -1, -1,
            7, 6, 10, 7, 10, 8, 8, 10, 9, -1, -1, -1, -1, -1, -1, -1,
            6, 8, 4, 11, 8, 6, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            3, 6, 11, 3, 0, 6, 0, 4, 6, -1, -1, -1, -1, -1, -1, -1,
            8, 6, 11, 8, 4, 6, 9, 0, 1, -1, -1, -1, -1, -1, -1, -1,
            9, 4, 6, 9, 6, 3, 9, 3, 1, 11, 3, 6, -1, -1, -1, -1,
            6, 8, 4, 6, 11, 8, 2, 10, 1, -1, -1, -1, -1, -1, -1, -1,
            1, 2, 10, 3, 0, 11, 0, 6, 11, 0, 4, 6, -1, -1, -1, -1,
            4, 11, 8, 4, 6, 11, 0, 2, 9, 2, 10, 9, -1, -1, -1, -1,
            10, 9, 3, 10, 3, 2, 9, 4, 3, 11, 3, 6, 4, 6, 3, -1,
            8, 2, 3, 8, 4, 2, 4, 6, 2, -1, -1, -1, -1, -1, -1, -1,
            0, 4, 2, 4, 6, 2, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            1, 9, 0, 2, 3, 4, 2, 4, 6, 4, 3, 8, -1, -1, -1, -1,
            1, 9, 4, 1, 4, 2, 2, 4, 6, -1, -1, -1, -1, -1, -1, -1,
            8, 1, 3, 8, 6, 1, 8, 4, 6, 6, 10, 1, -1, -1, -1, -1,
            10, 1, 0, 10, 0, 6, 6, 0, 4, -1, -1, -1, -1, -1, -1, -1,
            4, 6, 3, 4, 3, 8, 6, 10, 3, 0, 3, 9, 10, 9, 3, -1,
            10, 9, 4, 6, 10, 4, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            4, 9, 5, 7, 6, 11, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            0, 8, 3, 4, 9, 5, 11, 7, 6, -1, -1, -1, -1, -1, -1, -1,
            5, 0, 1, 5, 4, 0, 7, 6, 11, -1, -1, -1, -1, -1, -1, -1,
            11, 7, 6, 8, 3, 4, 3, 5, 4, 3, 1, 5, -1, -1, -1, -1,
            9, 5, 4, 10, 1, 2, 7, 6, 11, -1, -1, -1, -1, -1, -1, -1,
            6, 11, 7, 1, 2, 10, 0, 8, 3, 4, 9, 5, -1, -1, -1, -1,
            7, 6, 11, 5, 4, 10, 4, 2, 10, 4, 0, 2, -1, -1, -1, -1,
            3, 4, 8, 3, 5, 4, 3, 2, 5, 10, 5, 2, 11, 7, 6, -1,
            7, 2, 3, 7, 6, 2, 5, 4, 9, -1, -1, -1, -1, -1, -1, -1,
            9, 5, 4, 0, 8, 6, 0, 6, 2, 6, 8, 7, -1, -1, -1, -1,
            3, 6, 2, 3, 7, 6, 1, 5, 0, 5, 4, 0, -1, -1, -1, -1,
            6, 2, 8, 6, 8, 7, 2, 1, 8, 4, 8, 5, 1, 5, 8, -1,
            9, 5, 4, 10, 1, 6, 1, 7, 6, 1, 3, 7, -1, -1, -1, -1,
            1, 6, 10, 1, 7, 6, 1, 0, 7, 8, 7, 0, 9, 5, 4, -1,
            4, 0, 10, 4, 10, 5, 0, 3, 10, 6, 10, 7, 3, 7, 10, -1,
            7, 6, 10, 7, 10, 8, 5, 4, 10, 4, 8, 10, -1, -1, -1, -1,
            6, 9, 5, 6, 11, 9, 11, 8, 9, -1, -1, -1, -1, -1, -1, -1,
            3, 6, 11, 0, 6, 3, 0, 5, 6, 0, 9, 5, -1, -1, -1, -1,
            0, 11, 8, 0, 5, 11, 0, 1, 5, 5, 6, 11, -1, -1, -1, -1,
            6, 11, 3, 6, 3, 5, 5, 3, 1, -1, -1, -1, -1, -1, -1, -1,
            1, 2, 10, 9, 5, 11, 9, 11, 8, 11, 5, 6, -1, -1, -1, -1,
            0, 11, 3, 0, 6, 11, 0, 9, 6, 5, 6, 9, 1, 2, 10, -1,
            11, 8, 5, 11, 5, 6, 8, 0, 5, 10, 5, 2, 0, 2, 5, -1,
            6, 11, 3, 6, 3, 5, 2, 10, 3, 10, 5, 3, -1, -1, -1, -1,
            5, 8, 9, 5, 2, 8, 5, 6, 2, 3, 8, 2, -1, -1, -1, -1,
            9, 5, 6, 9, 6, 0, 0, 6, 2, -1, -1, -1, -1, -1, -1, -1,
            1, 5, 8, 1, 8, 0, 5, 6, 8, 3, 8, 2, 6, 2, 8, -1,
            1, 5, 6, 2, 1, 6, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            1, 3, 6, 1, 6, 10, 3, 8, 6, 5, 6, 9, 8, 9, 6, -1,
            10, 1, 0, 10, 0, 6, 9, 5, 0, 5, 6, 0, -1, -1, -1, -1,
            0, 3, 8, 5, 6, 10, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            10, 5, 6, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            11, 5, 10, 7, 5, 11, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            11, 5, 10, 11, 7, 5, 8, 3, 0, -1, -1, -1, -1, -1, -1, -1,
            5, 11, 7, 5, 10, 11, 1, 9, 0, -1, -1, -1, -1, -1, -1, -1,
            10, 7, 5, 10, 11, 7, 9, 8, 1, 8, 3, 1, -1, -1, -1, -1,
            11, 1, 2, 11, 7, 1, 7, 5, 1, -1, -1, -1, -1, -1, -1, -1,
            0, 8, 3, 1, 2, 7, 1, 7, 5, 7, 2, 11, -1, -1, -1, -1,
            9, 7, 5, 9, 2, 7, 9, 0, 2, 2, 11, 7, -1, -1, -1, -1,
            7, 5, 2, 7, 2, 11, 5, 9, 2, 3, 2, 8, 9, 8, 2, -1,
            2, 5, 10, 2, 3, 5, 3, 7, 5, -1, -1, -1, -1, -1, -1, -1,
            8, 2, 0, 8, 5, 2, 8, 7, 5, 10, 2, 5, -1, -1, -1, -1,
            9, 0, 1, 5, 10, 3, 5, 3, 7, 3, 10, 2, -1, -1, -1, -1,
            9, 8, 2, 9, 2, 1, 8, 7, 2, 10, 2, 5, 7, 5, 2, -1,
            1, 3, 5, 3, 7, 5, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            0, 8, 7, 0, 7, 1, 1, 7, 5, -1, -1, -1, -1, -1, -1, -1,
            9, 0, 3, 9, 3, 5, 5, 3, 7, -1, -1, -1, -1, -1, -1, -1,
            9, 8, 7, 5, 9, 7, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            5, 8, 4, 5, 10, 8, 10, 11, 8, -1, -1, -1, -1, -1, -1, -1,
            5, 0, 4, 5, 11, 0, 5, 10, 11, 11, 3, 0, -1, -1, -1, -1,
            0, 1, 9, 8, 4, 10, 8, 10, 11, 10, 4, 5, -1, -1, -1, -1,
            10, 11, 4, 10, 4, 5, 11, 3, 4, 9, 4, 1, 3, 1, 4, -1,
            2, 5, 1, 2, 8, 5, 2, 11, 8, 4, 5, 8, -1, -1, -1, -1,
            0, 4, 11, 0, 11, 3, 4, 5, 11, 2, 11, 1, 5, 1, 11, -1,
            0, 2, 5, 0, 5, 9, 2, 11, 5, 4, 5, 8, 11, 8, 5, -1,
            9, 4, 5, 2, 11, 3, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            2, 5, 10, 3, 5, 2, 3, 4, 5, 3, 8, 4, -1, -1, -1, -1,
            5, 10, 2, 5, 2, 4, 4, 2, 0, -1, -1, -1, -1, -1, -1, -1,
            3, 10, 2, 3, 5, 10, 3, 8, 5, 4, 5, 8, 0, 1, 9, -1,
            5, 10, 2, 5, 2, 4, 1, 9, 2, 9, 4, 2, -1, -1, -1, -1,
            8, 4, 5, 8, 5, 3, 3, 5, 1, -1, -1, -1, -1, -1, -1, -1,
            0, 4, 5, 1, 0, 5, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            8, 4, 5, 8, 5, 3, 9, 0, 5, 0, 3, 5, -1, -1, -1, -1,
            9, 4, 5, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            4, 11, 7, 4, 9, 11, 9, 10, 11, -1, -1, -1, -1, -1, -1, -1,
            0, 8, 3, 4, 9, 7, 9, 11, 7, 9, 10, 11, -1, -1, -1, -1,
            1, 10, 11, 1, 11, 4, 1, 4, 0, 7, 4, 11, -1, -1, -1, -1,
            3, 1, 4, 3, 4, 8, 1, 10, 4, 7, 4, 11, 10, 11, 4, -1,
            4, 11, 7, 9, 11, 4, 9, 2, 11, 9, 1, 2, -1, -1, -1, -1,
            9, 7, 4, 9, 11, 7, 9, 1, 11, 2, 11, 1, 0, 8, 3, -1,
            11, 7, 4, 11, 4, 2, 2, 4, 0, -1, -1, -1, -1, -1, -1, -1,
            11, 7, 4, 11, 4, 2, 8, 3, 4, 3, 2, 4, -1, -1, -1, -1,
            2, 9, 10, 2, 7, 9, 2, 3, 7, 7, 4, 9, -1, -1, -1, -1,
            9, 10, 7, 9, 7, 4, 10, 2, 7, 8, 7, 0, 2, 0, 7, -1,
            3, 7, 10, 3, 10, 2, 7, 4, 10, 1, 10, 0, 4, 0, 10, -1,
            1, 10, 2, 8, 7, 4, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            4, 9, 1, 4, 1, 7, 7, 1, 3, -1, -1, -1, -1, -1, -1, -1,
            4, 9, 1, 4, 1, 7, 0, 8, 1, 8, 7, 1, -1, -1, -1, -1,
            4, 0, 3, 7, 4, 3, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            4, 8, 7, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            9, 10, 8, 10, 11, 8, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            3, 0, 9, 3, 9, 11, 11, 9, 10, -1, -1, -1, -1, -1, -1, -1,
            0, 1, 10, 0, 10, 8, 8, 10, 11, -1, -1, -1, -1, -1, -1, -1,
            3, 1, 10, 11, 3, 10, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            1, 2, 11, 1, 11, 9, 9, 11, 8, -1, -1, -1, -1, -1, -1, -1,
            3, 0, 9, 3, 9, 11, 1, 2, 9, 2, 11, 9, -1, -1, -1, -1,
            0, 2, 11, 8, 0, 11, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            3, 2, 11, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            2, 3, 8, 2, 8, 10, 10, 8, 9, -1, -1, -1, -1, -1, -1, -1,
            9, 10, 2, 0, 9, 2, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            2, 3, 8, 2, 8, 10, 0, 1, 8, 1, 10, 8, -1, -1, -1, -1,
            1, 10, 2, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            1, 3, 8, 9, 1, 8, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            0, 9, 1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            0, 3, 8, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1
        };

    void Awake()
    {
        EnsureComponents();
    }

    void OnEnable()
    {
        EnsureComponents();
        RequestRebuild();
        if (!Application.isPlaying)
        {
            RebuildImmediate();
        }
    }

    void OnDisable()
    {
        CompletePendingRebuild();
        DisposeBuffers();
    }

    void OnDestroy()
    {
        CompletePendingRebuild();
        DisposeBuffers();
        DestroyOwned(mesh);
        mesh = null;
        DestroyOwned(generatedMaterial);
        generatedMaterial = null;
    }

    void OnValidate()
    {
#if UNITY_EDITOR
        // Component/material assignment isn't allowed during OnValidate, so defer to the next editor tick.
        UnityEditor.EditorApplication.delayCall -= DelayedValidate;
        UnityEditor.EditorApplication.delayCall += DelayedValidate;
#endif
    }

#if UNITY_EDITOR
    void DelayedValidate()
    {
        if (this == null || !isActiveAndEnabled)
        {
            return;
        }

        EnsureComponents();
        RequestRebuild();
        if (!Application.isPlaying)
        {
            RebuildImmediate();
        }
        else
        {
            PushVisualState();
        }
    }
#endif

    /// <summary>
    /// Throws away the generated material, job buffers and mesh layout, then rebuilds everything from scratch.
    /// Available from the component's context menu (the "⋮" button in the inspector).
    /// </summary>
    [ContextMenu("Refresh")]
    public void Refresh()
    {
        CompletePendingRebuild();

        if (meshRenderer != null && IsGeneratedMaterial(meshRenderer.sharedMaterial))
        {
            Material stale = meshRenderer.sharedMaterial;
            meshRenderer.sharedMaterial = null;
            if (stale != generatedMaterial)
            {
                DestroyOwned(stale);
            }
        }

        DestroyOwned(generatedMaterial);
        generatedMaterial = null;

        DisposeBuffers();
        if (mesh != null)
        {
            mesh.Clear();
        }
        meshVertexCapacity = 0;

        EnsureComponents();
        RebuildImmediate();
        if (!isActiveAndEnabled)
        {
            DisposeBuffers();
        }

#if UNITY_EDITOR
        UnityEditor.SceneView.RepaintAll();
        UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
#endif
    }

    void Update()
    {
        if (!Application.isPlaying)
        {
            return;
        }

        if (noiseScrollSpeed > 0f)
        {
            Vector2 delta = noiseScrollDirection.sqrMagnitude > 0f ? noiseScrollDirection.normalized : Vector2.right;
            scrollOffset += delta * (noiseScrollSpeed * Time.deltaTime);
            if (rebuildEveryFrame)
            {
                dirty = true;
            }
        }

        PushVisualState();

        if (dirty)
        {
            // Jobs run on worker threads while the rest of the frame's scripts execute; collected in LateUpdate.
            ScheduleRebuild();
        }
    }

    void LateUpdate()
    {
        if (jobPending)
        {
            CompletePendingRebuild();
        }
    }

    public void RequestRebuild()
    {
        dirty = true;
    }

    public void ClearEdits()
    {
        stamps.Clear();
        dirty = true;
    }

    public void ApplyExplosion(Vector3 worldCenter, float radius, float densityDelta, Color tint, float tintBlend = 0.75f)
    {
        stamps.Add(new TerrainStamp
        {
            localCenter = transform.InverseTransformPoint(worldCenter),
            radius = radius,
            densityDelta = densityDelta,
            tint = tint,
            tintBlend = tintBlend,
            eventRadius = radius,
            eventWidth = radius * 0.3f,
            eventStrength = 1f,
        });

        dirty = true;
    }

    void EnsureComponents()
    {
        if (meshFilter == null && !TryGetComponent(out meshFilter))
        {
            meshFilter = gameObject.AddComponent<MeshFilter>();
        }

        if (meshRenderer == null && !TryGetComponent(out meshRenderer))
        {
            meshRenderer = gameObject.AddComponent<MeshRenderer>();
        }

        if (mesh == null)
        {
            // Reuse the mesh from before a domain reload instead of leaking a new one each time.
            Mesh existing = meshFilter.sharedMesh;
            if (existing != null && existing.name == GeneratedMeshName && (existing.hideFlags & HideFlags.DontSave) != 0)
            {
                mesh = existing;
            }
            else
            {
                mesh = new Mesh
                {
                    name = GeneratedMeshName,
                    hideFlags = HideFlags.DontSave,
                };
            }

            mesh.MarkDynamic();
            meshVertexCapacity = 0;
        }

        if (meshFilter.sharedMesh != mesh)
        {
            meshFilter.sharedMesh = mesh;
        }

        Material current = meshRenderer.sharedMaterial;
        if (current == null || materialOverride != null || IsGeneratedMaterial(current))
        {
            Material desired = ResolveMaterial();
            if (desired != null && current != desired)
            {
                meshRenderer.sharedMaterial = desired;
                if (current != null && current != generatedMaterial && IsGeneratedMaterial(current))
                {
                    DestroyOwned(current);
                }
            }
        }

        propertyBlock ??= new MaterialPropertyBlock();
    }

    Material ResolveMaterial()
    {
        if (materialOverride != null)
        {
            return materialOverride;
        }

        Shader hologram = Shader.Find("Hidden/MarchingCubes/Hologram");
        Shader unlit = Shader.Find("Universal Render Pipeline/Unlit");
        Shader shader = useCustomMaterial ? (hologram != null ? hologram : unlit) : (unlit != null ? unlit : hologram);
        if (shader == null)
        {
            return null;
        }

        if (generatedMaterial != null && generatedMaterial.shader == shader)
        {
            return generatedMaterial;
        }

        DestroyOwned(generatedMaterial);
        generatedMaterial = new Material(shader)
        {
            name = GeneratedMaterialName,
            hideFlags = HideFlags.DontSave,
        };
        return generatedMaterial;
    }

    static bool IsGeneratedMaterial(Material material)
    {
        return material != null
            && material.name == GeneratedMaterialName
            && (material.hideFlags & HideFlags.DontSave) != 0;
    }

    static void DestroyOwned(UnityEngine.Object obj)
    {
        if (obj == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            Destroy(obj);
        }
        else
        {
            DestroyImmediate(obj);
        }
    }

    void PushVisualState()
    {
        if (meshRenderer == null)
        {
            return;
        }

        propertyBlock ??= new MaterialPropertyBlock();
        propertyBlock.Clear();

        TerrainStamp stamp = stamps.Count > 0 ? stamps[^1] : default;
        Vector4 eventCenterRadius = stamps.Count > 0
            ? new Vector4(stamp.localCenter.x, stamp.localCenter.y, stamp.localCenter.z, stamp.eventRadius)
            : Vector4.zero;

        float eventPulse = stamps.Count > 0 ? Mathf.Clamp01(1f - Mathf.Clamp01((Time.time - lastRebuildTime) * 0.7f)) : 0f;

        propertyBlock.SetColor(BaseColorId, Color.white);
        propertyBlock.SetColor(LowTintId, lowTint);
        propertyBlock.SetColor(HighTintId, highTint);
        propertyBlock.SetColor(AccentTintId, accentTint);
        propertyBlock.SetColor(EventTintId, eventTint);
        propertyBlock.SetFloat(GridScaleId, gridScale);
        propertyBlock.SetFloat(GridLineWidthId, gridLineWidth);
        propertyBlock.SetFloat(ContourScaleId, contourScale);
        propertyBlock.SetFloat(ContourLineWidthId, contourLineWidth);
        propertyBlock.SetFloat(EmissionStrengthId, emissionStrength);
        propertyBlock.SetFloat(EdgeGlowStrengthId, edgeGlowStrength);
        propertyBlock.SetVector(ChunkHalfExtentsId, new Vector4(cellsX * cellSize * 0.5f, cellsZ * cellSize * 0.5f, 0f, 0f));
        propertyBlock.SetFloat(MinSurfaceHeightId, minSurfaceHeight);
        propertyBlock.SetFloat(MaxSurfaceHeightId, maxSurfaceHeight);
        propertyBlock.SetFloat(WallGradientStrengthId, wallGradientStrength);
        propertyBlock.SetVector(EventCenterRadiusId, eventCenterRadius);
        propertyBlock.SetFloat(EventWidthId, stamps.Count > 0 ? stamp.eventWidth : 0f);
        propertyBlock.SetFloat(EventStrengthId, stamps.Count > 0 ? stamp.eventStrength : 0f);
        propertyBlock.SetFloat(EventPulseId, eventPulse);

        meshRenderer.SetPropertyBlock(propertyBlock);
    }

    void RebuildImmediate()
    {
        ScheduleRebuild();
        CompletePendingRebuild();
        PushVisualState();
    }

    void ScheduleRebuild()
    {
        CompletePendingRebuild();
        if (mesh == null)
        {
            EnsureComponents();
        }

        dirty = false;
        lastRebuildTime = Time.time;

        // One extra ring of forced-air samples on every side so marching cubes closes the volume into walls and a bottom.
        int3 counts = new(cellsX + 3, cellsY + 3, cellsZ + 3);
        EnsureBuffers(counts);

        editStamps.Clear();
        for (int i = 0; i < stamps.Count; i++)
        {
            TerrainStamp stamp = stamps[i];
            editStamps.Add(new EditStampData
            {
                localCenter = stamp.localCenter,
                radius = stamp.radius,
                densityDelta = stamp.densityDelta,
                tint = new float4(stamp.tint.r, stamp.tint.g, stamp.tint.b, stamp.tint.a),
                tintBlend = stamp.tintBlend,
                eventRadius = stamp.eventRadius,
                eventWidth = stamp.eventWidth,
                eventStrength = stamp.eventStrength,
            });
        }

        triangles.Clear();

        float3 boundsMin = new(-(cellsX * cellSize) * 0.5f, 0f, -(cellsZ * cellSize) * 0.5f);
        float3 boundsMax = new((cellsX * cellSize) * 0.5f, cellsY * cellSize, (cellsZ * cellSize) * 0.5f);
        float3 origin = boundsMin - cellSize;
        pendingBounds = new Bounds(
            new Vector3(0f, (cellsY * cellSize) * 0.5f, 0f),
            new Vector3(cellsX * cellSize, cellsY * cellSize, cellsZ * cellSize));

        SampleColumnsJob sampleJob = new()
        {
            Samples = samples,
            EditStamps = editStamps.AsArray(),
            SampleCounts = counts,
            CellSize = cellSize,
            Origin = origin,
            NoiseScale = noiseScale,
            ScrollOffset = scrollOffset,
            MinSurfaceHeight = minSurfaceHeight,
            MaxSurfaceHeight = maxSurfaceHeight,
            DetailContribution = detailContribution,
            LowTint = new float4(lowTint.r, lowTint.g, lowTint.b, lowTint.a),
            HighTint = new float4(highTint.r, highTint.g, highTint.b, highTint.a),
        };

        BuildMeshJob buildJob = new()
        {
            Samples = samples,
            SampleCounts = counts,
            CellSize = cellSize,
            Origin = origin,
            BoundsMin = boundsMin,
            BoundsMax = boundsMax,
            Triangles = triangles.AsParallelWriter(),
        };

        JobHandle sampleHandle = sampleJob.Schedule(counts.x * counts.z, 16);
        pendingJob = buildJob.Schedule((counts.x - 1) * (counts.z - 1), 8, sampleHandle);
        jobPending = true;
        JobHandle.ScheduleBatchedJobs();
    }

    void CompletePendingRebuild()
    {
        if (!jobPending)
        {
            return;
        }

        pendingJob.Complete();
        jobPending = false;

        if (mesh == null || !triangles.IsCreated)
        {
            return;
        }

        int vertexCount = triangles.Length * 3;
        EnsureMeshCapacity(vertexCount);

        if (vertexCount > 0)
        {
            NativeArray<VertexData> vertexData = triangles.AsArray().Reinterpret<VertexData>(UnsafeUtility.SizeOf<Triangle>());
            mesh.SetVertexBufferData(vertexData, 0, 0, vertexCount, 0, FastMeshUpdate);
        }

        // The index buffer is a static 0..capacity sequence; only the submesh range changes per rebuild.
        mesh.SetSubMesh(0, new SubMeshDescriptor(0, vertexCount, MeshTopology.Triangles)
        {
            bounds = pendingBounds,
            firstVertex = 0,
            vertexCount = vertexCount,
        }, FastMeshUpdate);
        mesh.bounds = pendingBounds;
    }

    void EnsureBuffers(int3 counts)
    {
        if (samples.IsCreated && allocatedSampleCounts.Equals(counts))
        {
            return;
        }

        DisposeBuffers();

        int sampleCount = counts.x * counts.y * counts.z;
        int cubeCount = (counts.x - 1) * (counts.y - 1) * (counts.z - 1);

        samples = new NativeArray<VoxelSample>(sampleCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        editStamps = new NativeList<EditStampData>(16, Allocator.Persistent);
        // Marching cubes emits at most 5 triangles per cube.
        triangles = new NativeList<Triangle>(cubeCount * 5, Allocator.Persistent);
        allocatedSampleCounts = counts;
    }

    void EnsureMeshCapacity(int vertexCount)
    {
        if (meshVertexCapacity > 0 && vertexCount <= meshVertexCapacity)
        {
            return;
        }

        int maxVertices = math.max(triangles.Capacity * 3, 3);
        int capacity = math.max(vertexCount, math.max(meshVertexCapacity + meshVertexCapacity / 2, 3 * 1024));
        capacity = math.min(capacity, maxVertices);
        capacity = math.max(capacity, vertexCount);
        capacity += (3 - capacity % 3) % 3;

        mesh.Clear(false);
        mesh.SetVertexBufferParams(capacity, VertexLayout);
        mesh.SetIndexBufferParams(capacity, IndexFormat.UInt32);

        NativeArray<int> sequentialIndices = new(capacity, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
        for (int i = 0; i < capacity; i++)
        {
            sequentialIndices[i] = i;
        }

        mesh.SetIndexBufferData(sequentialIndices, 0, 0, capacity, FastMeshUpdate);
        sequentialIndices.Dispose();

        mesh.subMeshCount = 1;
        meshVertexCapacity = capacity;
    }

    void DisposeBuffers()
    {
        if (jobPending)
        {
            pendingJob.Complete();
            jobPending = false;
        }

        if (samples.IsCreated) samples.Dispose();
        if (editStamps.IsCreated) editStamps.Dispose();
        if (triangles.IsCreated) triangles.Dispose();
        allocatedSampleCounts = default;
    }

    /// <summary>
    /// One work item per (x, z) column: the 2D height noise is evaluated once per column instead of once per voxel.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast, OptimizeFor = OptimizeFor.Performance)]
    struct SampleColumnsJob : IJobParallelFor
    {
        [WriteOnly, NativeDisableParallelForRestriction] public NativeArray<VoxelSample> Samples;
        [ReadOnly] public NativeArray<EditStampData> EditStamps;
        public int3 SampleCounts;
        public float CellSize;
        public float3 Origin;
        public float NoiseScale;
        public float2 ScrollOffset;
        public float MinSurfaceHeight;
        public float MaxSurfaceHeight;
        public float DetailContribution;
        public float4 LowTint;
        public float4 HighTint;

        public void Execute(int column)
        {
            int x = column % SampleCounts.x;
            int z = column / SampleCounts.x;

            float2 columnPos = Origin.xz + new float2(x, z) * CellSize;
            float2 noisePos = columnPos * NoiseScale + ScrollOffset;

            float baseNoise = noise.snoise(noisePos);
            float detailNoise = noise.snoise(noisePos * 2.13f + new float2(19.73f, 6.41f));
            float normalized = saturate(baseNoise * 0.5f + 0.5f + detailNoise * DetailContribution);
            float surfaceHeight = lerp(MinSurfaceHeight, MaxSurfaceHeight, normalized);
            float chunkHeight = (SampleCounts.y - 3) * CellSize;
            surfaceHeight = clamp(surfaceHeight, CellSize * 0.5f, chunkHeight - CellSize * 0.5f);

            float3 columnTint = lerp(LowTint, HighTint, normalized).xyz;
            bool edgeColumn = x == 0 || z == 0 || x == SampleCounts.x - 1 || z == SampleCounts.z - 1;
            int stride = SampleCounts.x * SampleCounts.y;
            int baseIndex = x + stride * z;

            for (int y = 0; y < SampleCounts.y; y++)
            {
                float3 localPos = new(columnPos.x, Origin.y + y * CellSize, columnPos.y);
                float density = localPos.y - surfaceHeight;
                float3 tint = columnTint;
                // Accumulated stamp tint weight, consumed by the shader to keep explosion tint on walls.
                float stampWeight = 0f;

                for (int i = 0; i < EditStamps.Length; i++)
                {
                    EditStampData stamp = EditStamps[i];
                    float distSq = distancesq(localPos, stamp.localCenter);
                    if (distSq >= stamp.radius * stamp.radius)
                    {
                        continue;
                    }

                    float falloff = 1f - sqrt(distSq) / stamp.radius;
                    falloff *= falloff;

                    density += stamp.densityDelta * falloff;
                    float blend = falloff * stamp.tintBlend;
                    tint = lerp(tint, stamp.tint.xyz, blend);
                    stampWeight = lerp(stampWeight, 1f, blend);
                }

                if (edgeColumn || y == 0 || y == SampleCounts.y - 1)
                {
                    density = CellSize;
                }
                else if (y == 1)
                {
                    density = -CellSize;
                }

                Samples[baseIndex + SampleCounts.x * y] = new VoxelSample
                {
                    density = density,
                    color = new float4(tint, stampWeight),
                };
            }
        }
    }

    /// <summary>
    /// One work item per (x, z) column of cubes, marching up through y. Triangles are appended with a parallel writer.
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast, OptimizeFor = OptimizeFor.Performance)]
    struct BuildMeshJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<VoxelSample> Samples;
        public int3 SampleCounts;
        public float CellSize;
        public float3 Origin;
        public float3 BoundsMin;
        public float3 BoundsMax;
        public NativeList<Triangle>.ParallelWriter Triangles;

        public void Execute(int column)
        {
            int cubesX = SampleCounts.x - 1;
            int x = column % cubesX;
            int z = column / cubesX;
            int3 strides = new(1, SampleCounts.x, SampleCounts.x * SampleCounts.y);

            for (int y = 0; y < SampleCounts.y - 1; y++)
            {
                int baseIndex = x + strides.y * y + strides.z * z;

                int cubeIndex = 0;
                for (int corner = 0; corner < 8; corner++)
                {
                    if (Samples[baseIndex + dot(CornerOffsets[corner], strides)].density < 0f)
                    {
                        cubeIndex |= 1 << corner;
                    }
                }

                if (cubeIndex == 0 || cubeIndex == 255)
                {
                    continue;
                }

                int3 cell = new(x, y, z);
                int triBase = cubeIndex * 16;
                for (int i = 0; i < 15; i += 3)
                {
                    int a = TriTable[triBase + i];
                    if (a < 0)
                    {
                        break;
                    }

                    VertexData va = EdgeVertex(a, cell, baseIndex, strides);
                    VertexData vb = EdgeVertex(TriTable[triBase + i + 1], cell, baseIndex, strides);
                    VertexData vc = EdgeVertex(TriTable[triBase + i + 2], cell, baseIndex, strides);

                    float3 faceCross = cross(vb.position - va.position, vc.position - va.position);
                    if (lengthsq(faceCross) < 1e-12f)
                    {
                        continue;
                    }

                    float3 normal = normalize(faceCross);
                    va.normal = normal;
                    vb.normal = normal;
                    vc.normal = normal;

                    Triangles.AddNoResize(new Triangle { a = va, b = vb, c = vc });
                }
            }
        }

        VertexData EdgeVertex(int edge, int3 cell, int baseIndex, int3 strides)
        {
            int2 corners = EdgeCorners[edge];
            int3 offsetA = CornerOffsets[corners.x];
            int3 offsetB = CornerOffsets[corners.y];

            VoxelSample a = Samples[baseIndex + dot(offsetA, strides)];
            VoxelSample b = Samples[baseIndex + dot(offsetB, strides)];

            float3 cornerA = Origin + (float3)(cell + offsetA) * CellSize;
            float3 cornerB = Origin + (float3)(cell + offsetB) * CellSize;

            return new VertexData
            {
                position = clamp((cornerA + cornerB) * 0.5f, BoundsMin, BoundsMax),
                normal = default,
                color = (a.color + b.color) * 0.5f,
            };
        }
    }
}
