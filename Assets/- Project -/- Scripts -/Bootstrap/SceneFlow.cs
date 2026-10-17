using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

public class SceneFlow : MonoBehaviour
{
    public enum TransitionState { Idle, FadingOut, Loading, FadingIn }

    [Header("UI")]
    [SerializeField] CanvasGroup fade;

    [Header("Timing")]
    [SerializeField] float fadeDuration = 0.8f;

    Coroutine transition;

    public static SceneFlow Instance { get; private set; }
    public static TransitionState Phase { get; private set; }
    public static event Action SceneChanged;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        Phase = TransitionState.Idle;

        transform.SetParent(null);
        DontDestroyOnLoad(gameObject);

        if (fade != null && fade.TryGetComponent(out Canvas canvas))
        {
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = short.MaxValue;
        }

        SetAlpha(0f);
        SetBlocking(false);
    }

    void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
            Phase = TransitionState.Idle;
        }
    }

    public static void ChangeScene(string sceneName)
    {
        if (Instance == null)
        {
            Debug.LogError("[SceneFlow] No SceneFlow in the scene.");
            return;
        }

        switch (Phase)
        {
            case TransitionState.FadingOut:
            case TransitionState.Loading:
                return;

            case TransitionState.FadingIn:
                Instance.Interrupt(sceneName);
                return;

            default:
                Instance.transition = Instance.StartCoroutine(Instance.ChangeSceneRoutine(sceneName));
                return;
        }
    }

    void Interrupt(string sceneName)
    {
        if (transition != null)
        {
            StopCoroutine(transition);
            transition = null;
        }

        transition = StartCoroutine(ChangeSceneRoutine(sceneName));
    }

    IEnumerator ChangeSceneRoutine(string sceneName)
    {
        Phase = TransitionState.FadingOut;
        SetBlocking(true);

        var oldScene = SceneManager.GetActiveScene();

        var previousPriority = Application.backgroundLoadingPriority;
        Application.backgroundLoadingPriority = ThreadPriority.High;

        var load = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
        load.allowSceneActivation = false;

        yield return Fade(1f, fadeDuration * (1f - Alpha()));

        Phase = TransitionState.Loading;

        while (load.progress < 0.9f)
            yield return null;

        load.allowSceneActivation = true;
        while (!load.isDone)
            yield return null;

        var newScene = SceneManager.GetSceneAt(SceneManager.sceneCount - 1);
        SceneManager.SetActiveScene(newScene);

        if (oldScene.IsValid())
            yield return SceneManager.UnloadSceneAsync(oldScene);

        yield return Resources.UnloadUnusedAssets();
        GC.Collect();
        yield return PrewarmText(newScene);

        Application.backgroundLoadingPriority = previousPriority;

        Phase = TransitionState.FadingIn;
        SetBlocking(false);
        SceneChanged?.Invoke();

        yield return Fade(0f, fadeDuration);

        Phase = TransitionState.Idle;
        transition = null;
    }

    IEnumerator Fade(float target, float duration)
    {
        float from = Alpha();
        float t = 0f;

        while (t < duration)
        {
            t += Mathf.Min(Time.unscaledDeltaTime, 1f / 30f);
            SetAlpha(Mathf.Lerp(from, target, Cubic(Mathf.Clamp01(t / duration))));
            yield return null;
        }

        SetAlpha(target);
    }

    static IEnumerator PrewarmText(Scene scene)
    {
        int sinceYield = 0;

        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var text in root.GetComponentsInChildren<TMP_Text>(false))
            {
                text.ForceMeshUpdate();

                if (++sinceYield >= 20)
                {
                    sinceYield = 0;
                    yield return null;
                }
            }
        }
    }

    static float Cubic(float t) =>
        t < 0.5f ? 4f * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 3f) / 2f;

    float Alpha() => fade == null ? 0f : fade.alpha;

    void SetAlpha(float alpha)
    {
        if (fade != null)
            fade.alpha = alpha;
    }

    void SetBlocking(bool blocking)
    {
        if (fade != null)
            fade.blocksRaycasts = blocking;
    }
}
