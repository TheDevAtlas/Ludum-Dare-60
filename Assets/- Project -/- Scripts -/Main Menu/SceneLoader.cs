using UnityEngine;

public class SceneLoader : MonoBehaviour
{
    [SerializeField] string sceneName = "Main Menu";

    public void LoadScene()
    {
        SceneFlow.ChangeScene(sceneName);
    }

    public void LoadScene(string name)
    {
        SceneFlow.ChangeScene(name);
    }
}
