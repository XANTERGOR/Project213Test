#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>Free flight controls, automatically installed in Play Mode and player builds.</summary>
[DisallowMultipleComponent]
public sealed class PlayModeFreeCamera : MonoBehaviour
{
    public float moveSpeed = 20f;
    public float fastMultiplier = 4f;
    public float mouseSensitivity = 0.12f;

    private Vector3 originalPosition;
    private Quaternion originalRotation;
    private float yaw;
    private float pitch;
    private bool looking;
    private CursorLockMode previousLock;
    private bool previousVisible;

#if !UNITY_EDITOR
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void RegisterPlayerCamera()
    {
        UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnPlayerSceneLoaded;
        UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnPlayerSceneLoaded;
    }

    private static void OnPlayerSceneLoaded(UnityEngine.SceneManagement.Scene scene,
        UnityEngine.SceneManagement.LoadSceneMode mode)
    {
        Camera camera = Camera.main;
        if (camera != null && camera.GetComponent<PlayModeFreeCamera>() == null)
            camera.gameObject.AddComponent<PlayModeFreeCamera>();
    }
#endif

    private void Awake()
    {
        originalPosition = transform.position;
        originalRotation = transform.rotation;
        yaw = transform.eulerAngles.y;
        pitch = Mathf.DeltaAngle(0f, transform.eulerAngles.x);
    }

    private void Update()
    {
        var mouse = Mouse.current;
        var keyboard = Keyboard.current;
        if (!Application.isFocused || mouse == null || keyboard == null)
        {
            ReleaseMouse();
            return;
        }

        if (keyboard.escapeKey.wasPressedThisFrame || !mouse.rightButton.isPressed)
            ReleaseMouse();
        else if (mouse.rightButton.wasPressedThisFrame)
        {
            previousLock = Cursor.lockState;
            previousVisible = Cursor.visible;
            looking = true;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        // Match the Scene view: hold RMB to fly, leaving editor shortcuts usable otherwise.
        if (!looking)
            return;

        Vector2 delta = mouse.delta.ReadValue();
        yaw += delta.x * mouseSensitivity;
        pitch = Mathf.Clamp(pitch - delta.y * mouseSensitivity, -89f, 89f);
        transform.rotation = Quaternion.Euler(pitch, yaw, 0f);

        float horizontal = (keyboard.dKey.isPressed ? 1f : 0f) - (keyboard.aKey.isPressed ? 1f : 0f);
        float forward = (keyboard.wKey.isPressed ? 1f : 0f) - (keyboard.sKey.isPressed ? 1f : 0f);
        float vertical = (keyboard.eKey.isPressed ? 1f : 0f) - (keyboard.qKey.isPressed ? 1f : 0f);
        Vector3 direction = transform.right * horizontal + transform.forward * forward + Vector3.up * vertical;
        float speed = moveSpeed * (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed ? fastMultiplier : 1f);
        transform.position += Vector3.ClampMagnitude(direction, 1f) * speed * Time.unscaledDeltaTime;
    }

    private void OnApplicationFocus(bool focused)
    {
        if (!focused) ReleaseMouse();
    }

    private void OnDisable() => ReleaseMouse();

    private void ReleaseMouse()
    {
        if (!looking) return;
        looking = false;
        Cursor.lockState = previousLock;
        Cursor.visible = previousVisible;
    }

    internal void Restore()
    {
        ReleaseMouse();
        transform.SetPositionAndRotation(originalPosition, originalRotation);
    }
}

#if UNITY_EDITOR
[InitializeOnLoad]
internal static class PlayModeFreeCameraBootstrap
{
    private static PlayModeFreeCamera installed;

    static PlayModeFreeCameraBootstrap()
    {
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
    }

    private static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.EnteredPlayMode)
        {
            Camera camera = Camera.main;
            if (camera != null && camera.GetComponent<PlayModeFreeCamera>() == null)
            {
                installed = camera.gameObject.AddComponent<PlayModeFreeCamera>();
                installed.hideFlags = HideFlags.DontSave;
            }
        }
        else if (state == PlayModeStateChange.ExitingPlayMode && installed != null)
        {
            // Also restore when Enter Play Mode Options disables scene reload.
            installed.Restore();
            Object.DestroyImmediate(installed);
            installed = null;
        }
    }
}
#endif
