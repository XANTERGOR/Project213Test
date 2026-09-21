using UnityEngine;
using UnityEngine.Formats.Alembic.Importer;

#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways]
[DisallowMultipleComponent]
[RequireComponent(typeof(AlembicStreamPlayer))]
public sealed class AlembicPreviewPlayer : MonoBehaviour
{
    [Header("Playback")]
    [Min(0f)] public float speed = 1f;
    public bool loop = true;
    public bool playOnEnableInGame = true;
    public bool useUnscaledTime = true;

    [SerializeField, HideInInspector] private bool isPlaying;

    private AlembicStreamPlayer streamPlayer;

#if UNITY_EDITOR
    private double lastEditorTime;
#endif

    public bool IsPlaying => isPlaying;

    private void OnEnable()
    {
        GetPlayer();

        if (Application.isPlaying)
            isPlaying = playOnEnableInGame;

#if UNITY_EDITOR
        EditorApplication.update -= EditorTick;
        EditorApplication.update += EditorTick;
        lastEditorTime = EditorApplication.timeSinceStartup;
#endif
    }

    private void OnDisable()
    {
#if UNITY_EDITOR
        EditorApplication.update -= EditorTick;
#endif
    }

    private void Update()
    {
        if (!Application.isPlaying || !isPlaying)
            return;

        float deltaTime = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
        Advance(deltaTime);
    }

    public void Play()
    {
        GetPlayer();
        isPlaying = true;

#if UNITY_EDITOR
        lastEditorTime = EditorApplication.timeSinceStartup;
#endif
    }

    public void Pause()
    {
        isPlaying = false;
    }

    public void Stop()
    {
        isPlaying = false;
        SetTime(0f);
    }

    public void Restart()
    {
        SetTime(0f);
        Play();
    }

    private void Advance(float deltaTime)
    {
        GetPlayer();

        if (streamPlayer == null || streamPlayer.Duration <= 0f)
            return;

        float nextTime = streamPlayer.CurrentTime + deltaTime * speed;

        if (loop)
        {
            nextTime = Mathf.Repeat(nextTime, streamPlayer.Duration);
        }
        else if (nextTime >= streamPlayer.Duration)
        {
            nextTime = streamPlayer.Duration;
            isPlaying = false;
        }

        SetTime(nextTime);
    }

    private void SetTime(float time)
    {
        GetPlayer();

        if (streamPlayer == null)
            return;

        float clampedTime = Mathf.Clamp(time, 0f, streamPlayer.Duration);
        streamPlayer.CurrentTime = clampedTime;
        streamPlayer.UpdateImmediately(clampedTime);

#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            EditorUtility.SetDirty(streamPlayer);
            SceneView.RepaintAll();
        }
#endif
    }

    private void GetPlayer()
    {
        if (streamPlayer == null)
            streamPlayer = GetComponent<AlembicStreamPlayer>();
    }

#if UNITY_EDITOR
    private void EditorTick()
    {
        if (this == null || Application.isPlaying)
            return;

        double now = EditorApplication.timeSinceStartup;
        float deltaTime = (float)System.Math.Min(now - lastEditorTime, 0.1d);
        lastEditorTime = now;

        if (!isPlaying)
            return;

        Advance(deltaTime);
        EditorApplication.QueuePlayerLoopUpdate();
    }
#endif
}

#if UNITY_EDITOR
[CustomEditor(typeof(AlembicPreviewPlayer))]
public sealed class AlembicPreviewPlayerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        AlembicPreviewPlayer preview = (AlembicPreviewPlayer)target;

        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(preview.IsPlaying ? "Pause" : "Play"))
            {
                Undo.RecordObject(preview, "Alembic Preview Playback");

                if (preview.IsPlaying)
                    preview.Pause();
                else
                    preview.Play();

                EditorUtility.SetDirty(preview);
            }

            if (GUILayout.Button("Restart"))
            {
                Undo.RecordObject(preview, "Restart Alembic Preview");
                preview.Restart();
                EditorUtility.SetDirty(preview);
            }

            if (GUILayout.Button("Stop"))
            {
                Undo.RecordObject(preview, "Stop Alembic Preview");
                preview.Stop();
                EditorUtility.SetDirty(preview);
            }
        }

        EditorGUILayout.HelpBox(
            "Кнопки работают в Edit Mode и Play Mode. Stop возвращает Alembic на начало.",
            MessageType.Info);
    }
}
#endif
