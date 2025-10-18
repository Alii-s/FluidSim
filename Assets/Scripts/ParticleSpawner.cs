// ...existing code...
using System.Collections.Generic;
using UnityEngine;

public class ParticleSpawner : MonoBehaviour
{
    public GameObject particlePrefab; // optional prefab; if null a simple one is created at runtime
    public int spawnCount = 50;
    public float particleDiameter = 0.18f;
    public float bounce = 0.05f;   // 0..1
    public float damping = 1.0f;   // linear + angular damping applied to spawned bodies

    [Header("Gravity (dynamic)")]
    public Vector2 gravity = new Vector2(0f, -9.81f);
    [Tooltip("Higher = faster gravity changes")]
    public float gravityTransitionSpeed = 10f;

    // attempts to avoid overlap when placing particles
    public int maxPlacementAttemptsPerParticle = 50;
    public float placementMargin = 0.01f;

    Sprite circleSprite;
    Vector2 currentGravity;

    void Awake()
    {
        // initialize and apply gravity immediately
        currentGravity = gravity;
        Physics2D.gravity = currentGravity;

        CreateCircleSprite();

        if (particlePrefab == null)
            particlePrefab = CreateDefaultParticlePrefab();
    }

    void Start()
    {
        SpawnAllInsideContainer();
    }

    void Update()
    {
        // smoothly interpolate global physics gravity toward the inspector value
        if (!Mathf.Approximately(currentGravity.x, gravity.x) || !Mathf.Approximately(currentGravity.y, gravity.y))
        {
            float t = 1f - Mathf.Exp(-gravityTransitionSpeed * Time.deltaTime);
            currentGravity = Vector2.Lerp(currentGravity, gravity, t);
            Physics2D.gravity = currentGravity;
        }
    }

    // public helper to change gravity from other scripts; immediate if requested
    public void SetGravity(Vector2 g, bool immediate = false)
    {
        gravity = g;
        if (immediate)
        {
            currentGravity = gravity;
            Physics2D.gravity = gravity;
        }
    }

    void SpawnAllInsideContainer()
    {
        ContainerBuilder cb = FindObjectOfType<ContainerBuilder>();

        float halfWidth = cb != null ? cb.width / 2f : 1.5f;
        float halfHeight = cb != null ? cb.height / 2f : 1.0f;

        float radius = particleDiameter * 0.5f;
        float minX = -halfWidth + radius + placementMargin;
        float maxX =  halfWidth - radius - placementMargin;
        float minY = -halfHeight + radius + placementMargin;
        float maxY =  halfHeight - radius - placementMargin;

        List<Vector2> placed = new List<Vector2>(spawnCount);

        for (int i = 0; i < spawnCount; i++)
        {
            Vector2 chosen = Vector2.zero;
            bool found = false;

            for (int attempt = 0; attempt < maxPlacementAttemptsPerParticle; attempt++)
            {
                float x = Random.Range(minX, maxX);
                float y = Random.Range(minY, maxY);
                Vector2 candidate = new Vector2(x, y);

                bool overlaps = false;
                float minDistSqr = (2f * radius) * (2f * radius);
                for (int j = 0; j < placed.Count; j++)
                {
                    if ((placed[j] - candidate).sqrMagnitude < minDistSqr)
                    {
                        overlaps = true;
                        break;
                    }
                }

                if (!overlaps)
                {
                    chosen = candidate;
                    found = true;
                    break;
                }
            }

            // if we failed to find a non-overlapping spot, place anyway at random (may overlap)
            if (!found)
            {
                chosen = new Vector2(Random.Range(minX, maxX), Random.Range(minY, maxY));
            }

            placed.Add(chosen);

            var p = Instantiate(particlePrefab, transform);
            p.transform.position = new Vector3(chosen.x, chosen.y, 0f);

            // ensure scale matches desired diameter (in case prefab was created or changed)
            p.transform.localScale = new Vector3(particleDiameter, particleDiameter, 1f);

            var rb = p.GetComponent<Rigidbody2D>();
            if (rb != null)
            {
                rb.linearDamping = damping;
                rb.angularDamping = damping;
                rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            }
            else
            {
                // if prefab lacked a Rigidbody2D, add one
                rb = p.AddComponent<Rigidbody2D>();
                rb.gravityScale = 1f;
                rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
                rb.linearDamping = damping;
                rb.angularDamping = damping;
            }
        }
    }

    void CreateCircleSprite()
    {
        if (circleSprite != null) return;

        int size = 64;
        Texture2D tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
        Color transparent = new Color(0, 0, 0, 0);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = (x - size / 2f) / (size / 2f);
                float dy = (y - size / 2f) / (size / 2f);
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                tex.SetPixel(x, y, d <= 1f ? Color.white : transparent);
            }
        }
        tex.Apply();
        circleSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size); // 1 unit sprite
    }

    GameObject CreateDefaultParticlePrefab()
    {
        GameObject p = new GameObject("Particle_Default");
        var sr = p.AddComponent<SpriteRenderer>();
        sr.sprite = circleSprite;
        sr.color = Color.cyan;

        // set scale to desired diameter (sprite is 1 unit)
        p.transform.localScale = new Vector3(particleDiameter, particleDiameter, 1f);

        var col = p.AddComponent<CircleCollider2D>();
        col.radius = 0.5f; // sprite is 1 unit; scale makes actual radius = 0.5 * scale

        var mat = new PhysicsMaterial2D("dynMat");
        mat.bounciness = Mathf.Clamp01(bounce);
        mat.friction = 0.4f;
        col.sharedMaterial = mat;

        var rb = p.AddComponent<Rigidbody2D>();
        rb.gravityScale = 1f;
        rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        rb.linearDamping = damping;
        rb.angularDamping = damping;

        // keep as a usable runtime prefab
        p.SetActive(true);
        return p;
    }
}
// ...existing code...