using UnityEngine;

[DefaultExecutionOrder(-1000)]
public class QuitGame : MonoBehaviour
{
    [SerializeField] GameObject hideOnWebGL;

    void Awake()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        (hideOnWebGL != null ? hideOnWebGL : gameObject).SetActive(false);
#endif
    }

    public void Quit()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#elif UNITY_WEBGL
#else
        Application.Quit();
#endif
    }
}
