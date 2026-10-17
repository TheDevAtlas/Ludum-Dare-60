using System.Globalization;
using TMPro;
using UnityEngine;

public sealed class PositionCoordinatesDisplay : MonoBehaviour
{
    [SerializeField] MarchingCubesTerrain terrain;
    [SerializeField] string unit = "k";
    [SerializeField] float distanceMultiplier = 100f;
    [SerializeField, Min(0)] int decimalPrecision = 2;

    TextMeshProUGUI coordinatesText;

    void Awake()
    {
        coordinatesText = GetComponent<TextMeshProUGUI>();
        if (terrain == null)
        {
            Debug.LogError($"{nameof(PositionCoordinatesDisplay)} requires a terrain reference.", this);
            enabled = false;
            return;
        }

        if (coordinatesText == null)
        {
            Debug.LogError($"{nameof(PositionCoordinatesDisplay)} requires a TextMeshProUGUI component.", this);
            enabled = false;
        }
    }

    void LateUpdate()
    {
        Vector3 position = terrain.NoiseSampleWorldPosition;
        int precision = Mathf.Max(0, decimalPrecision);
        float x = position.x / 1000f * distanceMultiplier;
        float z = position.z / 1000f * distanceMultiplier;

        coordinatesText.text = string.Format(
            CultureInfo.InvariantCulture,
            "X: {0}{2}\nZ: {1}{2}",
            x.ToString($"F{precision}", CultureInfo.InvariantCulture),
            z.ToString($"F{precision}", CultureInfo.InvariantCulture),
            unit);
    }
}
