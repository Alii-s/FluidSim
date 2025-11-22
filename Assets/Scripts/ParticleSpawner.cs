using System.Collections.Generic;
using System.Threading.Tasks; // <-- add this
using UnityEngine;

public class ParticleSpawner : MonoBehaviour
{
    public GameObject particlePrefab;
    public int spawnCount = 50;
    public float particleDiameter = 0.18f;
    public float bounce = 0.05f;
    public float damping = 1.0f;

    [Header("Gravity (dynamic)")]
    public Vector2 gravity = new Vector2(0f, -9.81f);
    [Tooltip("Higher = faster gravity changes")]
    public float gravityTransitionSpeed = 10f;

    public int maxPlacementAttemptsPerParticle = 50;
    public float placementMargin = 0.01f;

    [Header("Repulsion (fallback)")]
    public float interactionRadius = 0.4f;
    public float repulsionStrength = 10f;

    [Header("SPH")]
    public bool useSPH = true;
    public float smoothingRadius = 0.4f;     // h
    public float restDensity = 1f;           // ρ0
    public float particleMass = 1f;          // m
    public float stiffness = 50f;            // k (pressure stiffness)
    public float viscosity = 0.1f;           // μ
    public float maxForceClamp = 200f;       // safety clamp

    [Header("Velocity Color")]
    public bool enableVelocityColor = true;
    public int velocityColorSkipFrames = 0;
    public float maxColorSpeed = 1f;
    public Color slowColor = Color.cyan;
    public Color fastColor = Color.red;

    [Header("Collision Control")]
    public bool disableParticleSelfCollision = true;
    public string particleLayerName = "Particles";

    public bool useParallel = true;

    Sprite circleSprite;
    Vector2 currentGravity;

    float prevParticleDiameter;
    float prevBounce;
    float prevDamping;
    Vector2 prevGravity;
    float prevGravityTransitionSpeed;
    float prevPlacementMargin;

    public GameObject[] particles;
    public Vector2[] particlePositions;
    public Vector2[] particleVelocities;

    // SPH arrays
    float[] densities;
    float[] pressures;

    Vector2[] _forceAccum;
    object[] _locks;

    int _colorFrameCounter = 0;
    int _particleLayerIndex = -1;

    void Awake()
    {
        currentGravity = gravity;
        Physics2D.gravity = currentGravity;
        CreateCircleSprite();
        if (particlePrefab == null)
            particlePrefab = CreateDefaultParticlePrefab();
        _particleLayerIndex = LayerMask.NameToLayer(particleLayerName); // must exist in Project Settings > Tags and Layers
        prevParticleDiameter = particleDiameter;
        prevBounce = bounce;
        prevDamping = damping;
        prevGravity = gravity;
        prevGravityTransitionSpeed = gravityTransitionSpeed;
        prevPlacementMargin = placementMargin;
    }

    void Start()
    {
        SpawnAllInsideContainer();
        ApplyAllToExistingParticles();
        ApplyParticleLayerSettings();
    }

    void Update()
    {
        if (!Mathf.Approximately(currentGravity.x, gravity.x) || !Mathf.Approximately(currentGravity.y, gravity.y))
        {
            float t = 1f - Mathf.Exp(-gravityTransitionSpeed * Time.deltaTime);
            currentGravity = Vector2.Lerp(currentGravity, gravity, t);
            Physics2D.gravity = currentGravity;
        }

        if (!Mathf.Approximately(particleDiameter, prevParticleDiameter))
        {
            ApplyScaleToExistingParticles();
            prevParticleDiameter = particleDiameter;
        }
        if (!Mathf.Approximately(bounce, prevBounce))
        {
            ApplyBounceToExistingParticles();
            prevBounce = bounce;
        }
        if (!Mathf.Approximately(damping, prevDamping))
        {
            ApplyDampingToExistingParticles();
            prevDamping = damping;
        }
        if (!Mathf.Approximately(gravityTransitionSpeed, prevGravityTransitionSpeed))
            prevGravityTransitionSpeed = gravityTransitionSpeed;
        if (!Mathf.Approximately(placementMargin, prevPlacementMargin))
            prevPlacementMargin = placementMargin;

        UpdateParticleArrays();

        if (enableVelocityColor)
        {
            if (_colorFrameCounter >= velocityColorSkipFrames)
            {
                ApplyVelocityColorVisualization();
                _colorFrameCounter = 0;
            }
            else _colorFrameCounter++;
        }
        else
        {
            ResetVelocityColorsIfNeeded();
        }
    }

    void FixedUpdate()
    {
        if (useSPH) SPHStep();
        else ApplyRepulsionForces();
    }

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
        ContainerBuilder cb = FindFirstObjectByType<ContainerBuilder>();
        float halfWidth = cb != null ? cb.width / 2f : 1.5f;
        float halfHeight = cb != null ? cb.height / 2f : 1.0f;

        float radius = particleDiameter * 0.5f;
        float minX = -halfWidth + radius + placementMargin;
        float maxX = halfWidth - radius - placementMargin;
        float minY = -halfHeight + radius + placementMargin;
        float maxY = halfHeight - radius - placementMargin;

        List<Vector2> placed = new List<Vector2>(spawnCount);

        particles = new GameObject[spawnCount];
        particlePositions = new Vector2[spawnCount];
        particleVelocities = new Vector2[spawnCount];
        densities = new float[spawnCount];
        pressures = new float[spawnCount];

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
                    if ((placed[j] - candidate).sqrMagnitude < minDistSqr) { overlaps = true; break; }
                }
                if (!overlaps) { chosen = candidate; found = true; break; }
            }
            if (!found)
                chosen = new Vector2(Random.Range(minX, maxX), Random.Range(minY, maxY));

            placed.Add(chosen);

            var p = Instantiate(particlePrefab, transform);
            // Assign layer immediately if valid
            if (_particleLayerIndex != -1) p.layer = _particleLayerIndex;
            p.transform.position = new Vector3(chosen.x, chosen.y, 0f);
            p.transform.localScale = new Vector3(particleDiameter, particleDiameter, 1f);

            var rb = p.GetComponent<Rigidbody2D>();
            if (rb == null)
            {
                rb = p.AddComponent<Rigidbody2D>();
                rb.gravityScale = 1f;
                rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            }
            rb.linearDamping = damping;
            rb.angularDamping = damping;

            var col = p.GetComponent<CircleCollider2D>();
            if (col == null) col = p.AddComponent<CircleCollider2D>();
            SetColliderBounce(col, bounce);

            particles[i] = p;
            particlePositions[i] = chosen;
            particleVelocities[i] = Vector2.zero;
            densities[i] = restDensity;
            pressures[i] = 0f;
        }
        particlePrefab.SetActive(false);

        // After spawning
        restDensity = ComputeAverageInitialDensity();
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
        circleSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
    }

    GameObject CreateDefaultParticlePrefab()
    {
        GameObject p = new GameObject("Particle_Default");
        var sr = p.AddComponent<SpriteRenderer>();
        sr.sprite = circleSprite;
        sr.color = Color.cyan;
        p.transform.localScale = new Vector3(particleDiameter, particleDiameter, 1f);
        var col = p.AddComponent<CircleCollider2D>();
        col.radius = 0.5f;
        var mat = new PhysicsMaterial2D("dynMat");
        mat.bounciness = Mathf.Clamp01(bounce);
        mat.friction = 0.4f;
        col.sharedMaterial = mat;
        var rb = p.AddComponent<Rigidbody2D>();
        rb.gravityScale = 1f;
        rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        rb.linearDamping = damping;
        rb.angularDamping = damping;
        p.SetActive(true);
        return p;
    }

    void ApplyAllToExistingParticles()
    {
        ApplyScaleToExistingParticles();
        ApplyBounceToExistingParticles();
        ApplyDampingToExistingParticles();
    }

    void ApplyScaleToExistingParticles()
    {
        foreach (Transform child in transform)
        {
            var sr = child.GetComponent<SpriteRenderer>();
            if (sr == null) continue;
            child.localScale = new Vector3(particleDiameter, particleDiameter, 1f);
        }
    }

    void ApplyBounceToExistingParticles()
    {
        foreach (var col in GetComponentsInChildren<CircleCollider2D>())
            SetColliderBounce(col, bounce);
    }

    void ApplyDampingToExistingParticles()
    {
        foreach (var rb in GetComponentsInChildren<Rigidbody2D>())
        {
            if (rb.bodyType != RigidbodyType2D.Dynamic) continue;
            rb.linearDamping = damping;
            rb.angularDamping = damping;
        }
    }

    void SetColliderBounce(CircleCollider2D col, float b)
    {
        PhysicsMaterial2D mat = col.sharedMaterial;
        bool needNew = mat == null;
        if (!needNew && !mat.name.Contains("runtime"))
            needNew = true;
        if (needNew)
        {
            mat = new PhysicsMaterial2D("runtime_dynMat");
            mat.friction = 0.4f;
        }
        mat.bounciness = Mathf.Clamp01(b);
        col.sharedMaterial = mat;
    }

    void OnValidate()
    {
        if (!Application.isPlaying)
        {
            prevParticleDiameter = particleDiameter;
            prevBounce = bounce;
            prevDamping = damping;
            prevPlacementMargin = placementMargin;
            ApplyAllToExistingParticles();
        }
        else if (!enableVelocityColor)
        {
            ResetVelocityColorsIfNeeded();
        }

        // Re-apply ignore on validate if toggled
        if (disableParticleSelfCollision && _particleLayerIndex != -1)
            Physics2D.IgnoreLayerCollision(_particleLayerIndex, _particleLayerIndex, true);
        else if (!disableParticleSelfCollision && _particleLayerIndex != -1)
            Physics2D.IgnoreLayerCollision(_particleLayerIndex, _particleLayerIndex, false);
    }

    void UpdateParticleArrays()
    {
        if (particles == null) return;
        for (int i = 0; i < particles.Length; i++)
        {
            var go = particles[i];
            if (go == null) continue;
            particlePositions[i] = go.transform.position;
            var rb = go.GetComponent<Rigidbody2D>();
            if (rb != null)
                particleVelocities[i] = rb.linearVelocity;
        }
    }

    // ---------- SPH IMPLEMENTATION ----------
    // Kernel constants cached per step
    float _poly6Const;
    float _spikyGradConst;
    float _viscLapConst;

    void SPHStep()
    {
        if (particles == null) return;
        int n = particles.Length;
        EnsureSPHArrays(n);
        PrecomputeKernelConstants();
        if (_forceAccum == null || _forceAccum.Length != n) _forceAccum = new Vector2[n];
        if (_locks == null || _locks.Length != n)
        {
            _locks = new object[n];
            for (int i = 0; i < n; i++) _locks[i] = new object();
        }
        if (useParallel) ComputeDensitiesParallel(n); else ComputeDensities(n);
        ComputePressures(n);
        if (useParallel) ApplySPHForcesParallel(n); else ApplySPHForces(n);
    }

    void ComputeDensitiesParallel(int n)
    {
        float h = smoothingRadius;
        float h2 = h * h;
        Parallel.For(0, n, i =>
        {
            if (particles[i] == null) { densities[i] = restDensity; return; }
            float rho = 0f;
            Vector2 pi = particlePositions[i];
            for (int j = 0; j < n; j++)
            {
                if (particles[j] == null) continue;
                float r2 = (pi - particlePositions[j]).sqrMagnitude;
                if (r2 >= h2) continue;
                float term = h2 - r2;
                rho += particleMass * _poly6Const * term * term * term;
            }
            densities[i] = Mathf.Max(rho, restDensity * 0.5f);
        });
    }

    void ApplySPHForcesParallel(int n)
    {
        System.Array.Fill(_forceAccum, Vector2.zero);
        float h = smoothingRadius;

        Parallel.For(0, n, i =>
        {
            var goI = particles[i];
            if (goI == null) return;
            Vector2 pi = particlePositions[i];
            float rho_i = densities[i];
            float P_i = pressures[i];

            for (int j = i + 1; j < n; j++)
            {
                var goJ = particles[j];
                if (goJ == null) continue;

                Vector2 pj = particlePositions[j];
                Vector2 delta = pi - pj;
                float dist = delta.magnitude;
                if (dist <= 0f || dist >= h) continue;

                float rho_j = densities[j];
                float P_j = pressures[j];

                Vector2 dir = delta / dist;
                float gradMag = _spikyGradConst * (h - dist) * (h - dist);
                Vector2 gradW = gradMag * dir;

                float pressureScalar = (P_i + P_j) / (2f * Mathf.Max(rho_i * rho_j, 1e-6f));
                Vector2 fPressure = -particleMass * pressureScalar * gradW;

                float lapW = _viscLapConst * (h - dist);
                Vector2 vij = particleVelocities[j] - particleVelocities[i];
                Vector2 fVisc = viscosity * particleMass * vij * lapW / Mathf.Max(rho_j, 1e-6f);

                Vector2 fTotal = fPressure + fVisc;
                float mag = fTotal.magnitude;
                if (mag > maxForceClamp) fTotal *= (maxForceClamp / mag);

                _forceAccum[i] += fTotal;
                lock (_locks[j]) { _forceAccum[j] -= fTotal; }
            }
        });

        // Apply on main thread
        for (int i = 0; i < n; i++)
        {
            var rb = particles[i]?.GetComponent<Rigidbody2D>();
            if (rb == null) continue;
            rb.AddForce(_forceAccum[i], ForceMode2D.Force);
        }
    }

    void EnsureSPHArrays(int n)
    {
        if (densities == null || densities.Length != n) densities = new float[n];
        if (pressures == null || pressures.Length != n) pressures = new float[n];
    }

    void PrecomputeKernelConstants()
    {
        float h = smoothingRadius;
        _poly6Const = 4f / (Mathf.PI * Mathf.Pow(h, 8));          // Poly6
        _spikyGradConst = -30f / (Mathf.PI * Mathf.Pow(h, 5));          // Spiky gradient
        _viscLapConst = 20f / (3f * Mathf.PI * Mathf.Pow(h, 5));     // Viscosity Laplacian
    }

    void ComputeDensities(int n)
    {
        float h = smoothingRadius;
        float h2 = h * h;
        for (int i = 0; i < n; i++)
        {
            if (particles[i] == null) { densities[i] = restDensity; continue; }
            float rho = 0f;
            Vector2 pi = particlePositions[i];
            for (int j = 0; j < n; j++)
            {
                if (particles[j] == null) continue;
                Vector2 r = pi - particlePositions[j];
                float r2 = r.sqrMagnitude;
                if (r2 >= h2) continue;
                float term = h2 - r2;
                rho += particleMass * _poly6Const * term * term * term;
            }
            densities[i] = Mathf.Max(rho, restDensity * 0.5f);
        }
    }

    void ComputePressures(int n)
    {
        for (int i = 0; i < n; i++)
        {
            float rho = densities[i];
            pressures[i] = stiffness * Mathf.Max(rho - restDensity, 0f);
        }
    }

    void ApplySPHForces(int n)
    {
        float h = smoothingRadius;
        for (int i = 0; i < n; i++)
        {
            var goI = particles[i];
            if (goI == null) continue;
            var rbI = goI.GetComponent<Rigidbody2D>();
            if (rbI == null) continue;

            Vector2 pi = particlePositions[i];
            float rho_i = densities[i];
            float P_i = pressures[i];

            for (int j = i + 1; j < n; j++)
            {
                var goJ = particles[j];
                if (goJ == null) continue;
                var rbJ = goJ.GetComponent<Rigidbody2D>();
                if (rbJ == null) continue;

                Vector2 pj = particlePositions[j];
                Vector2 delta = pi - pj;
                float dist = delta.magnitude;
                if (dist <= 0f || dist >= h) continue;

                float rho_j = densities[j];
                float P_j = pressures[j];

                Vector2 dir = delta / dist;

                // Spiky gradient
                float gradMag = _spikyGradConst * (h - dist) * (h - dist);
                Vector2 gradW = gradMag * dir;

                // Pressure (symmetrized)
                float pressureScalar = (P_i + P_j) / (2f * Mathf.Max(rho_i * rho_j, 1e-6f));
                Vector2 fPressure = -particleMass * pressureScalar * gradW;

                // Viscosity
                float lapW = _viscLapConst * (h - dist);
                Vector2 vij = particleVelocities[j] - particleVelocities[i];
                Vector2 fVisc = viscosity * particleMass * vij * lapW / Mathf.Max(rho_j, 1e-6f);

                Vector2 fTotal = fPressure + fVisc;
                float mag = fTotal.magnitude;
                if (mag > maxForceClamp)
                    fTotal *= (maxForceClamp / mag);

                rbI.AddForce(fTotal, ForceMode2D.Force);
                rbJ.AddForce(-fTotal, ForceMode2D.Force);
            }
        }
    }

    // ---------- SIMPLE REPULSION (LEGACY) ----------
    void ApplyRepulsionForces()
    {
        if (particles == null) return;
        int n = particles.Length;
        UpdateParticleArrays();
        float r = interactionRadius;
        float rSqr = r * r;

        for (int i = 0; i < n; i++)
        {
            var goI = particles[i];
            if (goI == null) continue;
            var rbI = goI.GetComponent<Rigidbody2D>();
            if (rbI == null) continue;
            Vector2 pi = particlePositions[i];

            for (int j = i + 1; j < n; j++)
            {
                var goJ = particles[j];
                if (goJ == null) continue;
                var rbJ = goJ.GetComponent<Rigidbody2D>();
                if (rbJ == null) continue;

                Vector2 pj = particlePositions[j];
                Vector2 delta = pi - pj;
                float distSqr = delta.sqrMagnitude;
                if (distSqr > rSqr || distSqr < 1e-8f) continue;
                float dist = Mathf.Sqrt(distSqr);
                float falloff = 1f - dist / r;
                float fMag = repulsionStrength * falloff;
                Vector2 forceDir = delta / dist;
                Vector2 force = forceDir * fMag;
                rbI.AddForce(force, ForceMode2D.Force);
                rbJ.AddForce(-force, ForceMode2D.Force);
            }
        }
    }

    void ResetVelocityColorsIfNeeded()
    {
        if (_colorFrameCounter != -1)
        {
            if (particles != null)
            {
                foreach (var go in particles)
                {
                    if (go == null) continue;
                    var sr = go.GetComponent<SpriteRenderer>();
                    if (sr == null) continue;
                    sr.color = slowColor;
                }
            }
            _colorFrameCounter = -1;
        }
    }

    void ApplyVelocityColorVisualization()
    {
        _colorFrameCounter = 0;
        if (!enableVelocityColor) return;
        if (particles == null) return;
        int n = particles.Length;
        float maxSpeed = Mathf.Max(0.0001f, maxColorSpeed);
        for (int i = 0; i < n; i++)
        {
            var go = particles[i];
            if (go == null) continue;
            var sr = go.GetComponent<SpriteRenderer>();
            if (sr == null) continue;
            float speed = particleVelocities[i].magnitude;
            float t = Mathf.Clamp01(speed / maxSpeed);
            sr.color = Color.Lerp(slowColor, fastColor, t);
        }
    }

    void ApplyParticleLayerSettings()
    {
        if (!disableParticleSelfCollision) return;
        if (_particleLayerIndex == -1) return; // layer not found
        // Ignore collisions among particles on this layer
        Physics2D.IgnoreLayerCollision(_particleLayerIndex, _particleLayerIndex, true);

        if (particles == null) return;
        for (int i = 0; i < particles.Length; i++)
        {
            if (particles[i] == null) continue;
            particles[i].layer = _particleLayerIndex;
        }
    }

    public int ParticleCount => particles?.Length ?? 0;
    float ComputeAverageInitialDensity()
    {
        PrecomputeKernelConstants();
        int n = particles.Length;
        float h = smoothingRadius;
        float h2 = h * h;
        float sum = 0f;
        for (int i = 0; i < n; i++)
        {
            Vector2 pi = particlePositions[i];
            float rho = 0f;
            for (int j = 0; j < n; j++)
            {
                Vector2 d = pi - particlePositions[j];
                float r2 = d.sqrMagnitude;
                if (r2 >= h2) continue;
                float term = h2 - r2;
                rho += particleMass * _poly6Const * term * term * term;
            }
            sum += rho;
        }
        return (n > 0) ? sum / n : restDensity;
    }
}
