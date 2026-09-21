using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

[RequireComponent(typeof(ParticleSystem))]
public class ParticleCollisionDecalSpawner : MonoBehaviour
{
    [Header("Decal Prefabs")]
    [Tooltip("Список префабов. При столкновении случайно выбирается один.")]
    [SerializeField] private List<GameObject> decalPrefabs = new List<GameObject>();


    [Header("Placement")]
    [Tooltip("Небольшой отступ от поверхности.")]
    [SerializeField] private float surfaceOffset = 0.002f;

    [Tooltip("Дополнительный поворот prefab относительно поверхности.")]
    [SerializeField] private Vector3 rotationOffset = Vector3.zero;


    [Header("Random Rotation")]
    [Tooltip("Случайный поворот вокруг нормали поверхности.")]
    [SerializeField] private bool randomRotation = true;


    [Header("Random Scale")]
    [SerializeField] private bool randomScale = true;

    [SerializeField] private Vector2 scaleRange = new Vector2(0.8f, 1.2f);


    [Header("Lifetime")]
    [Tooltip("Общее время жизни декали. 0 = бесконечно.")]
    [SerializeField] private float decalLifetime = 20f;


    [Header("Fade In")]
    [Tooltip("Плавное появление декали через Fade Factor.")]
    [SerializeField] private bool useFadeIn = true;

    [Tooltip("Время появления Fade Factor 0 -> 1.")]
    [SerializeField] private float fadeInDuration = 0.5f;


    [Header("Fade Out")]
    [Tooltip("Плавное исчезновение декали перед окончанием Lifetime.")]
    [SerializeField] private bool useFadeOut = true;

    [Tooltip("За сколько секунд до конца Lifetime начинать Fade Factor 1 -> 0.")]
    [SerializeField] private float fadeOutDuration = 5f;


    private ParticleSystem ps;

    private readonly List<ParticleCollisionEvent> collisionEvents =
        new List<ParticleCollisionEvent>();


    private void Awake()
    {
        ps = GetComponent<ParticleSystem>();
    }


    private void OnParticleCollision(GameObject other)
    {
        if (decalPrefabs == null || decalPrefabs.Count == 0)
            return;

        int collisionCount =
            ParticlePhysicsExtensions.GetCollisionEvents(
                ps,
                other,
                collisionEvents
            );

        for (int i = 0; i < collisionCount; i++)
        {
            ParticleCollisionEvent collision = collisionEvents[i];

            SpawnRandomDecal(
                collision.intersection,
                collision.normal
            );
        }
    }


    private void SpawnRandomDecal(Vector3 position, Vector3 normal)
    {
        if (normal.sqrMagnitude < 0.0001f)
            return;

        normal.Normalize();


        // =====================================================
        // RANDOM PREFAB
        // =====================================================

        GameObject selectedPrefab =
            decalPrefabs[Random.Range(0, decalPrefabs.Count)];

        if (selectedPrefab == null)
            return;


        // =====================================================
        // POSITION
        // =====================================================

        Vector3 spawnPosition =
            position + normal * surfaceOffset;


        // =====================================================
        // ROTATION
        // =====================================================

        Quaternion surfaceRotation =
            Quaternion.LookRotation(normal);

        Quaternion offsetRotation =
            Quaternion.Euler(rotationOffset);

        Quaternion finalRotation =
            surfaceRotation * offsetRotation;


        // Случайный поворот вокруг нормали поверхности
        if (randomRotation)
        {
            Quaternion randomSurfaceRotation =
                Quaternion.AngleAxis(
                    Random.Range(0f, 360f),
                    normal
                );

            finalRotation =
                randomSurfaceRotation * finalRotation;
        }


        // =====================================================
        // SPAWN
        // =====================================================

        GameObject decal = Instantiate(
            selectedPrefab,
            spawnPosition,
            finalRotation
        );


        // =====================================================
        // RANDOM SCALE
        // =====================================================

        if (randomScale)
        {
            float scale = Random.Range(
                scaleRange.x,
                scaleRange.y
            );

            decal.transform.localScale *= scale;
        }


        // =====================================================
        // FADE / LIFETIME CONTROLLER
        // =====================================================

        // Контроллер нужен, если используется Lifetime
        // или Fade In.
        if (decalLifetime > 0f || useFadeIn)
        {
            DecalLifetimeController controller =
                decal.AddComponent<DecalLifetimeController>();

            controller.Initialize(
                decalLifetime,
                useFadeIn,
                fadeInDuration,
                useFadeOut,
                fadeOutDuration
            );
        }
    }
}


// ============================================================================
// Управление Fade In / Lifetime / Fade Out
// ============================================================================

public class DecalLifetimeController : MonoBehaviour
{
    private DecalProjector[] decalProjectors;

    private float lifetime;

    private bool useFadeIn;
    private float fadeInDuration;

    private bool useFadeOut;
    private float fadeOutDuration;

    private float timer;


    public void Initialize(
        float newLifetime,
        bool newUseFadeIn,
        float newFadeInDuration,
        bool newUseFadeOut,
        float newFadeOutDuration)
    {
        lifetime = newLifetime;

        useFadeIn = newUseFadeIn;
        fadeInDuration = Mathf.Max(0f, newFadeInDuration);

        useFadeOut = newUseFadeOut;
        fadeOutDuration = Mathf.Max(0f, newFadeOutDuration);

        timer = 0f;


        // Ищем все HDRP Decal Projector,
        // включая вложенные объекты.
        decalProjectors =
            GetComponentsInChildren<DecalProjector>(true);


        // Если включено появление,
        // начинаем с полностью невидимой декали.
        if (useFadeIn && fadeInDuration > 0f)
        {
            SetFadeFactor(0f);
        }
        else
        {
            SetFadeFactor(1f);
        }
    }


    private void Update()
    {
        timer += Time.deltaTime;


        // =====================================================
        // FADE IN
        // 0 -> 1
        // =====================================================

        if (useFadeIn &&
            fadeInDuration > 0f &&
            timer < fadeInDuration)
        {
            float fadeInProgress =
                timer / fadeInDuration;

            float fadeFactor =
                Mathf.Clamp01(fadeInProgress);

            SetFadeFactor(fadeFactor);

            return;
        }


        // =====================================================
        // FADE OUT
        // 1 -> 0
        // =====================================================

        if (lifetime > 0f &&
            useFadeOut &&
            fadeOutDuration > 0f)
        {
            float actualFadeOutDuration =
                Mathf.Min(fadeOutDuration, lifetime);

            float fadeOutStartTime =
                lifetime - actualFadeOutDuration;


            if (timer >= fadeOutStartTime)
            {
                float fadeOutTime =
                    timer - fadeOutStartTime;

                float fadeOutProgress =
                    fadeOutTime / actualFadeOutDuration;

                float fadeFactor =
                    1f - Mathf.Clamp01(fadeOutProgress);

                SetFadeFactor(fadeFactor);
            }
            else
            {
                SetFadeFactor(1f);
            }
        }
        else
        {
            // После Fade In держим декаль полностью видимой
            SetFadeFactor(1f);
        }


        // =====================================================
        // DESTROY
        // =====================================================

        if (lifetime > 0f && timer >= lifetime)
        {
            if (useFadeOut)
            {
                SetFadeFactor(0f);
            }

            Destroy(gameObject);
        }
    }


    private void SetFadeFactor(float value)
    {
        if (decalProjectors == null)
            return;

        for (int i = 0; i < decalProjectors.Length; i++)
        {
            if (decalProjectors[i] != null)
            {
                decalProjectors[i].fadeFactor = value;
            }
        }
    }
}