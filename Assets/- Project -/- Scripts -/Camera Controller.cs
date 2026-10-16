using UnityEngine;
using UnityEngine.InputSystem;

public class CameraController : MonoBehaviour
{
    [SerializeField] float sensitivity = 0.15f;
    [SerializeField] Vector2 pitchLimits = new(10f, 55f);
    [SerializeField] float smoothTime = 0.05f;

    Vector3 defaultEuler = new(35f, -45f, 0f);

    float targetPitch;
    float targetYaw;
    float currentPitch;
    float currentYaw;

    float pitchVel;
    float yawVel;

    bool isResetting;

    void Awake()
    {
        defaultEuler = transform.eulerAngles;
        currentPitch = targetPitch = defaultEuler.x;
        currentYaw = targetYaw = defaultEuler.y;
    }

    void Update()
    {
        var mouse = Mouse.current;
        var kb = Keyboard.current;
        if (mouse == null) return;

        // Reset target
        if (kb != null && kb.cKey.wasPressedThisFrame)
        {
            targetPitch = defaultEuler.x;
            targetYaw = currentYaw + Mathf.DeltaAngle(currentYaw, defaultEuler.y);
            isResetting = true;
        }

        // Orbit input
        if (mouse.rightButton.isPressed)
        {
            isResetting = false;
            Vector2 delta = mouse.delta.ReadValue() * sensitivity;

            targetYaw += delta.x;
            targetPitch = Mathf.Clamp(targetPitch - delta.y, pitchLimits.x, pitchLimits.y);
        }

        // Critically damped smoothing feels snappy for jam games
        float speed = isResetting ? 0.12f : smoothTime;
        currentPitch = Mathf.SmoothDamp(currentPitch, targetPitch, ref pitchVel, speed);
        currentYaw = Mathf.SmoothDampAngle(currentYaw, targetYaw, ref yawVel, speed);

        transform.rotation = Quaternion.Euler(currentPitch, currentYaw, 0f);
    }
}