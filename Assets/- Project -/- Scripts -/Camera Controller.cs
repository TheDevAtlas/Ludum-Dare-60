using UnityEngine;
using UnityEngine.InputSystem;

public class CameraController : MonoBehaviour
{

    public Camera mainCamera;

    public float hSens;
    public float vSens;

    // Update is called once per frame
    void Update()
    {
        // If we hold right mouse button, rotate camera on X and/or Y
        if (Mouse.current.rightButton.isPressed)
        {
            Vector2 mouseDelta = Mouse.current.delta.ReadValue();

            transform.Rotate(0f, mouseDelta.x * hSens, 0f);
        }
    }
}
