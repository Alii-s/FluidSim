using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
#endif

public class ParticleSpawner : MonoBehaviour
{
    [Header("Particles")]
    public GameObject particlePrefab;
    public int spawnCount = 50;
    public float particleDiameter = 0.18f;
    public float bounce = 0.05f;
    public float damping = 1.0f;

    [Header("Gravity")]
    public Vector2 gravity = new Vector2(0f, -9.81f);
    public float gravityTransitionSpeed = 10f;

    [Header("Placement")]
    public int maxPlacementAttemptsPerParticle = 50;
    public float placementMargin = 0.01f;

    [Header("SPH")]
    public float smoothingRadius = 0.4f;
    public float restDensity = 10f;
    public float particleMass = 1f;
    public float stiffness = 50f;
    public float viscosity = 0.1f;
    public float maxForceClamp = 200f;
    public bool autoSetRestDensity = true;

    [Header("Velocity Color")]
    public bool enableVelocityColor = true;
    public int velocityColorSkipFrames = 0;
    public float maxColorSpeed = 1f;
    public Gradient velocityGradient; // replaces slow/fast colors

    [Header("Collision Control")]
    public bool disableParticleSelfCollision = true;
    public string particleLayerName = "Particles";

    [Header("Spatial Hashing")]
    public bool useSpatialHash = true;

    [Header("Bounds")]
    public float boundsBounceFactor = 1f;
    public bool useAABBBounds = true;

    [Header("Boundary Control")]
    public bool spawnOnFrameEdges = false;
    public bool useWallRepulsion = true;
    public float wallRepulsionStrength = 40f;
    public float wallRepulsionDistance = 0.3f;

    [Header("Simulation Control")]
    public bool paused = false;
    public KeyCode togglePauseKey = KeyCode.Space;
    public KeyCode stepKey = KeyCode.F;          // single frame step while paused

    [Header("Mouse Forces")]
    public bool enableMouseForces = true;
    public float mouseForceRadius = 1.2f;
    public float attractionStrength = 60f;
    public float repulsionStrength = 60f;
    public AnimationCurve forceFalloff = AnimationCurve.EaseInOut(0, 1, 1, 0); // distance (0..1) -> scale

    [Header("Mouse Lift Only")]
    public bool mouseLiftOnly = true;
    public float mouseLiftStrength = 80f;          // upward force
    public float mouseLiftRadius = 1.2f;           // area of effect
    public AnimationCurve mouseLiftFalloff = AnimationCurve.EaseInOut(0,1,1,0);

    [Header("Timestep / Substeps")]
    public bool overrideFixedDelta = false;
    public float customFixedDelta = 0.005f;   // smaller than default 0.02 for more updates
    public int sphSubsteps = 1;               // >1 = internal SPH substeps

    [Header("Cohesion / Surface Tension")]
    public bool enableCohesion = true;
    public float cohesionStrength = 2f;      // tune (start 1–5)
    [Range(0f,1f)] public float cohesionMinQ = 0.35f; // start of attraction band (just outside strong repulsion)
    [Range(0f,1f)] public float cohesionMaxQ = 0.85f; // end of attraction band

    // Hash
    Dictionary<Vector2Int, List<int>> _hash;
    float _cellSize;
    Vector2 _hashOrigin;

    // Data
    public GameObject[] particles;
    public Vector2[] particlePositions;
    public Vector2[] particleVelocities;
    float[] densities;
    float[] pressures;
    Vector2[] predictedPositions;

    // Forces
    Vector2[] _forceAccum;
    object[] _locks;

    // Kernels
    float _poly6Const;
    float _spikyGradConst;
    float _viscLapConst;

    // State
    Sprite circleSprite;
    Vector2 currentGravity;
    int _particleLayerIndex = -1;
    int _colorFrameCounter = 0;

    // Live-change tracking
    float prevParticleDiameter;
    float prevBounce;
    float prevDamping;

    // private state fields
    bool _stepRequested;
    Vector2 _mouseWorld;
    bool _mouseAttract;
    bool _mouseRepel;

    // track pause state transition
    bool _wasPaused; 

    void Awake()
    {
        currentGravity = gravity;
        Physics2D.gravity = currentGravity;
        if (overrideFixedDelta) Time.fixedDeltaTime = customFixedDelta;
        CreateCircleSprite();
        if (particlePrefab == null)
            particlePrefab = CreateDefaultParticlePrefab();
        _particleLayerIndex = LayerMask.NameToLayer(particleLayerName);

        prevParticleDiameter = particleDiameter;
        prevBounce = bounce;
        prevDamping = damping;
    }

    void Start()
    {
        SpawnAllInsideContainer();
        ApplyAllToExistingParticles();
        ApplyParticleLayerSettings();
        RemoveLegacyWallColliders(); // NEW
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
        else ResetVelocityColorsIfNeeded();

        HandlePauseInput();
        CaptureMouseInput();
        UpdatePauseState();          // NEW
        TryStepWhilePaused();        // NEW
    }

    void FixedUpdate()
    {
        if (overrideFixedDelta && !Mathf.Approximately(Time.fixedDeltaTime, customFixedDelta))
            Time.fixedDeltaTime = customFixedDelta;

        if (paused) return;

        int steps = Mathf.Max(1, sphSubsteps);
        float dt = Time.fixedDeltaTime / steps;

        // If doing substeps, temporarily control physics manually
        bool scripted = (steps > 1);
        if (scripted) Physics2D.simulationMode = SimulationMode2D.Script;

        for (int s = 0; s < steps; s++)
        {
            SPHStep(dt);
            ApplyMouseForces();
            if (useAABBBounds) ConstrainParticlesToBox();
            if (scripted) Physics2D.Simulate(dt);
        }

        if (scripted) Physics2D.simulationMode = SimulationMode2D.FixedUpdate;
    }

    // ---- SPH core ----
    void SPHStep(float dt)
    {
        if (particles == null) return;
        UpdateParticleArrays();
        int n = particles.Length;
        EnsureSPHArrays(n);
        PrecomputeKernelConstants();

        if (_forceAccum == null || _forceAccum.Length != n)
            _forceAccum = new Vector2[n];
        if (_locks == null || _locks.Length != n)
        {
            _locks = new object[n];
            for (int i = 0; i < n; i++) _locks[i] = new object();
        }

        PredictPositions(dt, n);
        if (useSpatialHash) BuildSpatialHash(n, predictedPositions);
        ComputeDensitiesParallelPredicted(n);
        ComputePressures(n);
        ApplySPHForcesParallelPredicted(n);
    }

    void PredictPositions(float dt, int n)
    {
        for (int i = 0; i < n; i++)
            predictedPositions[i] = particlePositions[i] + particleVelocities[i] * dt;
    }

    void EnsureSPHArrays(int n)
    {
        if (densities == null || densities.Length != n) densities = new float[n];
        if (pressures == null || pressures.Length != n) pressures = new float[n];
        if (predictedPositions == null || predictedPositions.Length != n) predictedPositions = new Vector2[n];
    }

    // ---- Spatial hashing ----
    void BuildSpatialHash(int n, Vector2[] sourcePositions)
    {
        _cellSize = smoothingRadius;
        float minX = float.MaxValue, minY = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            if (particles[i] == null) continue;
            var p = sourcePositions[i];
            if (p.x < minX) minX = p.x;
            if (p.y < minY) minY = p.y;
        }
        _hashOrigin = new Vector2(minX, minY) - Vector2.one * _cellSize;
        _hash ??= new Dictionary<Vector2Int, List<int>>();
        _hash.Clear();

        for (int i = 0; i < n; i++)
        {
            if (particles[i] == null) continue;
            var p = sourcePositions[i];
            Vector2Int cell = WorldToCell(p);
            if (!_hash.TryGetValue(cell, out var list))
            {
                list = new List<int>(8);
                _hash[cell] = list;
            }
            list.Add(i);
        }
    }

    Vector2Int WorldToCell(Vector2 p)
    {
        int cx = Mathf.FloorToInt((p.x - _hashOrigin.x) / _cellSize);
        int cy = Mathf.FloorToInt((p.y - _hashOrigin.y) / _cellSize);
        return new Vector2Int(cx, cy);
    }

    IEnumerable<int> EnumerateNeighborIndices(Vector2Int cell)
    {
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                Vector2Int c = new Vector2Int(cell.x + dx, cell.y + dy);
                if (_hash != null && _hash.TryGetValue(c, out var list))
                    for (int k = 0; k < list.Count; k++)
                        yield return list[k];
            }
    }

    IEnumerable<int> FullIndexEnum(int n)
    {
        for (int i = 0; i < n; i++) yield return i;
    }

    // ---- Density (predicted) ----
    void ComputeDensitiesParallelPredicted(int n)
    {
        float h = smoothingRadius;
        float h2 = h * h;
        if (useSpatialHash && (_hash == null || _hash.Count == 0))
            BuildSpatialHash(n, predictedPositions);

        Parallel.For(0, n, i =>
        {
            if (particles[i] == null) { densities[i] = restDensity; return; }
            float rho = 0f;
            Vector2 pi = predictedPositions[i];

            if (useSpatialHash)
            {
                Vector2Int cell = WorldToCell(pi);
                foreach (int j in EnumerateNeighborIndices(cell))
                {
                    float r2 = (pi - predictedPositions[j]).sqrMagnitude;
                    if (r2 >= h2) continue;
                    float term = h2 - r2;
                    rho += particleMass * _poly6Const * term * term * term;
                }
            }
            else
            {
                for (int j = 0; j < n; j++)
                {
                    if (particles[j] == null) continue;
                    float r2 = (pi - predictedPositions[j]).sqrMagnitude;
                    if (r2 >= h2) continue;
                    float term = h2 - r2;
                    rho += particleMass * _poly6Const * term * term * term;
                }
            }

            densities[i] = Mathf.Max(rho, restDensity * 0.5f);
        });
    }

    // ---- Pressure ----
    void ComputePressures(int n)
    {
        for (int i = 0; i < n; i++)
        {
            float rho = densities[i];
            pressures[i] = stiffness * Mathf.Max(rho - restDensity, 0f);
        }
    }

    // ---- Forces (predicted) ----
    void ApplySPHForcesParallelPredicted(int n)
    {
        System.Array.Fill(_forceAccum, Vector2.zero);

        float h = smoothingRadius;
        if (useSpatialHash && (_hash == null || _hash.Count == 0))
            BuildSpatialHash(n, predictedPositions);

        Parallel.For(0, n, i =>
        {
            var goI = particles[i];
            if (goI == null) return;

            Vector2 pi = predictedPositions[i];
            float rho_i = densities[i];
            float P_i = pressures[i];

            Vector2Int cell = useSpatialHash ? WorldToCell(pi) : default;
            IEnumerable<int> neighborEnum = useSpatialHash ? EnumerateNeighborIndices(cell) : FullIndexEnum(n);

            foreach (int j in neighborEnum)
            {
                if (j <= i) continue;
                var goJ = particles[j];
                if (goJ == null) continue;

                Vector2 pj = predictedPositions[j];
                Vector2 d = pi - pj;
                float dist = d.magnitude;
                if (dist <= 0f || dist >= h) continue;

                float rho_j = densities[j];
                float P_j = pressures[j];

                Vector2 dir = d / dist;
                float gradMag = _spikyGradConst * (h - dist) * (h - dist);
                Vector2 gradW = gradMag * dir;

                float pressureScalar = (P_i + P_j) / (2f * Mathf.Max(rho_i * rho_j, 1e-6f));
                Vector2 fPressure = -particleMass * pressureScalar * gradW;

                float lapW = _viscLapConst * (h - dist);
                Vector2 vij = particleVelocities[j] - particleVelocities[i];
                Vector2 fVisc = viscosity * particleMass * vij * lapW / Mathf.Max(rho_j, 1e-6f);

                Vector2 fTotal = fPressure + fVisc;

                // Cohesion (mid-range attraction)
                if (enableCohesion)
                {
                    float q = dist / h;
                    if (q >= cohesionMinQ && q <= cohesionMaxQ)
                    {
                        float bandT = (q - cohesionMinQ) / Mathf.Max(cohesionMaxQ - cohesionMinQ, 1e-6f);
                        float falloff = 1f - bandT; // strongest near cohesionMinQ
                        // Soften near outer edge (optional quadratic)
                        falloff *= falloff;
                        Vector2 fCohesion = -dir * (cohesionStrength * falloff);
                        fTotal += fCohesion;
                    }
                }

                float mag = fTotal.magnitude;
                if (mag > maxForceClamp) fTotal *= (maxForceClamp / mag);

                _forceAccum[i] += fTotal;
                lock (_locks[j]) { _forceAccum[j] -= fTotal; }
            }
        });

        for (int i = 0; i < n; i++)
        {
            var rb = particles[i]?.GetComponent<Rigidbody2D>();
            if (rb == null) continue;
            rb.AddForce(_forceAccum[i], ForceMode2D.Force);
        }

        if (useWallRepulsion) ApplyWallRepulsion(n);
    }

    // ---- Kernels ----
    void PrecomputeKernelConstants()
    {
        float h = smoothingRadius;
        _poly6Const = 4f / (Mathf.PI * Mathf.Pow(h, 8));
        _spikyGradConst = -30f / (Mathf.PI * Mathf.Pow(h, 5));
        _viscLapConst = 20f / (3f * Mathf.PI * Mathf.Pow(h, 5));
    }

    // ---- Spawning ----
    void SpawnAllInsideContainer()
    {
        ContainerBuilder cb = FindFirstObjectByType<ContainerBuilder>();
        Vector2 center = cb ? (Vector2)cb.transform.position : Vector2.zero;

        float minX, maxX, minY, maxY;
        if (cb != null && !spawnOnFrameEdges)
            cb.GetCentralSpawnBounds(out minX, out maxX, out minY, out maxY);
        else if (cb != null && spawnOnFrameEdges)
        {
            Vector2 half = cb.GetClampHalfExtents();
            minX = -half.x; maxX = half.x;
            minY = -half.y; maxY = half.y;
        }
        else
        {
            float halfW = 3f, halfH = 2f;
            minX = -halfW * 0.5f; maxX = halfW * 0.5f;
            minY = -halfH * 0.5f; maxY = halfH * 0.5f;
        }

        float radius = particleDiameter * 0.5f;

        if (!spawnOnFrameEdges)
        {
            minX += radius + placementMargin;
            maxX -= radius + placementMargin;
            minY += radius + placementMargin;
            maxY -= radius + placementMargin;
        }

        List<Vector2> placed = new List<Vector2>(spawnCount);
        particles = new GameObject[spawnCount];
        particlePositions = new Vector2[spawnCount];
        particleVelocities = new Vector2[spawnCount];
        densities = new float[spawnCount];
        pressures = new float[spawnCount];

        if (spawnOnFrameEdges)
        {
            float widthSpan = maxX - minX;
            float heightSpan = maxY - minY;
            float perimeter = 2f * (widthSpan + heightSpan);
            float spacing = Mathf.Max(radius * 2f, perimeter / spawnCount);
            int created = 0;

            while (created < spawnCount)
            {
                float s = (created * spacing) % perimeter;
                Vector2 local;
                if (s < widthSpan) local = new Vector2(minX + s, minY);
                else if (s < widthSpan + heightSpan) local = new Vector2(maxX, minY + (s - widthSpan));
                else if (s < widthSpan + heightSpan + widthSpan) local = new Vector2(maxX - (s - widthSpan - heightSpan), maxY);
                else local = new Vector2(minX, maxY - (s - widthSpan - heightSpan - widthSpan));

                CreateFluidParticle(created, center + local);
                created++;
            }
        }
        else
        {
            for (int i = 0; i < spawnCount; i++)
            {
                Vector2 chosen = Vector2.zero;
                bool found = false;
                for (int attempt = 0; attempt < maxPlacementAttemptsPerParticle; attempt++)
                {
                    Vector2 cand = new Vector2(Random.Range(minX, maxX), Random.Range(minY, maxY));
                    bool overlaps = false;
                    float minDistSqr = (2f * radius) * (2f * radius);
                    for (int j = 0; j < placed.Count; j++)
                        if ((placed[j] - cand).sqrMagnitude < minDistSqr) { overlaps = true; break; }
                    if (!overlaps) { chosen = cand; found = true; break; }
                }
                if (!found) chosen = new Vector2(Random.Range(minX, maxX), Random.Range(minY, maxY));
                placed.Add(chosen);
                CreateFluidParticle(i, center + chosen);
            }
        }

        particlePrefab.SetActive(false);
    }

    void CreateFluidParticle(int index, Vector2 worldPos)
    {
        var p = Instantiate(particlePrefab, transform);
        if (_particleLayerIndex != -1) p.layer = _particleLayerIndex;
        p.transform.position = worldPos;
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

        particles[index] = p;
        particlePositions[index] = worldPos;
        particleVelocities[index] = Vector2.zero;
        densities[index] = restDensity;
        pressures[index] = 0f;
    }

    // ---- Boundary helpers ----
    void ApplyWallRepulsion(int n)
    {
        if (!useWallRepulsion) return;
        var cb = FindFirstObjectByType<ContainerBuilder>();
        if (cb == null) return;

        Vector2 center = cb.transform.position;
        Vector2 half = cb.GetClampHalfExtents();
        float r = wallRepulsionDistance;

        for (int i = 0; i < n; i++)
        {
            var rb = particles[i]?.GetComponent<Rigidbody2D>();
            if (rb == null) continue;

            Vector2 local = rb.position - center;
            Vector2 force = Vector2.zero;

            float dx = half.x - Mathf.Abs(local.x);
            if (dx < r)
            {
                float sign = Mathf.Sign(local.x);
                force.x += wallRepulsionStrength * (1f - dx / r) * -sign;
            }

            float dy = half.y - Mathf.Abs(local.y);
            if (dy < r)
            {
                float signY = Mathf.Sign(local.y);
                force.y += wallRepulsionStrength * (1f - dy / r) * -signY;
            }

            if (force != Vector2.zero)
                rb.AddForce(force, ForceMode2D.Force);
        }
    }

    // ---- Utility / visuals / collision ----
    void UpdateParticleArrays()
    {
        if (particles == null) return;
        for (int i = 0; i < particles.Length; i++)
        {
            var go = particles[i];
            if (go == null) continue;
            particlePositions[i] = go.transform.position;
            var rb = go.GetComponent<Rigidbody2D>();
            if (rb != null) particleVelocities[i] = rb.linearVelocity;
        }
    }

    void ApplyVelocityColorVisualization()
    {
        _colorFrameCounter = 0;
        if (!enableVelocityColor || particles == null) return;
        float maxSpeed = Mathf.Max(0.0001f, maxColorSpeed);
        for (int i = 0; i < particles.Length; i++)
        {
            var go = particles[i];
            if (go == null) continue;
            var sr = go.GetComponent<SpriteRenderer>();
            if (sr == null) continue;
            float speed = particleVelocities[i].magnitude;
            float t = Mathf.Clamp01(speed / maxSpeed);
            sr.color = velocityGradient.Evaluate(t);
        }
    }

    void ResetVelocityColorsIfNeeded()
    {
        if (_colorFrameCounter != -1 && particles != null)
        {
            foreach (var go in particles)
            {
                if (go == null) continue;
                var sr = go.GetComponent<SpriteRenderer>();
                if (sr == null) continue;
                sr.color = velocityGradient.Evaluate(0f); // blue (idle)
            }
            _colorFrameCounter = -1;
        }
    }

    void ApplyParticleLayerSettings()
    {
        if (!disableParticleSelfCollision || _particleLayerIndex == -1) return;
        Physics2D.IgnoreLayerCollision(_particleLayerIndex, _particleLayerIndex, true);
        if (particles == null) return;
        for (int i = 0; i < particles.Length; i++)
            if (particles[i] != null) particles[i].layer = _particleLayerIndex;
    }

    void OnValidate()
    {
        if (!Application.isPlaying)
        {
            prevParticleDiameter = particleDiameter;
            prevBounce = bounce;
            prevDamping = damping;
            ApplyAllToExistingParticles();
        }
        else if (!enableVelocityColor)
        {
            ResetVelocityColorsIfNeeded();
        }

        if (disableParticleSelfCollision && _particleLayerIndex != -1)
            Physics2D.IgnoreLayerCollision(_particleLayerIndex, _particleLayerIndex, true);
        else if (!disableParticleSelfCollision && _particleLayerIndex != -1)
            Physics2D.IgnoreLayerCollision(_particleLayerIndex, _particleLayerIndex, false);
    }

    void SetColliderBounce(CircleCollider2D col, float b)
    {
        PhysicsMaterial2D mat = col.sharedMaterial;
        if (mat == null || !mat.name.Contains("runtime"))
        {
            mat = new PhysicsMaterial2D("runtime_dynMat") { friction = 0.4f };
        }
        mat.bounciness = Mathf.Clamp01(b);
        col.sharedMaterial = mat;
    }

    void CreateCircleSprite()
    {
        if (circleSprite != null) return;
        int size = 64;
        Texture2D tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
        Color transparent = new Color(0, 0, 0, 0);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x - size / 2f) / (size / 2f);
                float dy = (y - size / 2f) / (size / 2f);
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                tex.SetPixel(x, y, d <= 1f ? Color.white : transparent);
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
        var mat = new PhysicsMaterial2D("dynMat") { bounciness = Mathf.Clamp01(bounce), friction = 0.4f };
        col.sharedMaterial = mat;
        var rb = p.AddComponent<Rigidbody2D>();
        rb.gravityScale = 1f;
        rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
        rb.linearDamping = damping;
        rb.angularDamping = damping;
        p.SetActive(true);
        return p;
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
        return n > 0 ? sum / n : restDensity;
    }

    // ---- Box constraint (matches frame) ----
    void ConstrainParticlesToBox()
    {
        var cb = FindFirstObjectByType<ContainerBuilder>();
        if (cb == null || particles == null) return;

        Vector2 center = cb.transform.position;
        Vector2 half = cb.GetClampHalfExtents() - Vector2.one * (particleDiameter * 0.5f);

        for (int i = 0; i < particles.Length; i++)
        {
            var go = particles[i];
            if (go == null) continue;
            var rb = go.GetComponent<Rigidbody2D>();
            if (rb == null) continue;

            Vector2 local = rb.position - center;
            Vector2 vel = rb.linearVelocity;
            bool hit = false;

            if (Mathf.Abs(local.x) > half.x)
            {
                local.x = half.x * Mathf.Sign(local.x);
                vel.x = -vel.x * boundsBounceFactor;
                hit = true;
            }
            if (Mathf.Abs(local.y) > half.y)
            {
                local.y = half.y * Mathf.Sign(local.y);
                vel.y = -vel.y * boundsBounceFactor;
                hit = true;
            }

            if (hit)
            {
                rb.position = center + local;
                rb.linearVelocity = vel;
                particlePositions[i] = rb.position;
                particleVelocities[i] = vel;
            }
        }
    }

    // ---- Batch apply helpers ----
    void ApplyAllToExistingParticles()
    {
        ApplyScaleToExistingParticles();
        ApplyBounceToExistingParticles();
        ApplyDampingToExistingParticles();
    }

void ApplyScaleToExistingParticles()
    {
        if (particles == null) return;
        for (int i = 0; i < particles.Length; i++)
        {
            var p = particles[i];
            if (p == null) continue;
            p.transform.localScale = new Vector3(particleDiameter, particleDiameter, 1f);
        }
    }

    void ApplyBounceToExistingParticles()
    {
        if (particles == null) return;
        for (int i = 0; i < particles.Length; i++)
        {
            var p = particles[i];
            if (p == null) continue;
            var col = p.GetComponent<CircleCollider2D>() ?? p.AddComponent<CircleCollider2D>();
            SetColliderBounce(col, bounce);
        }
    }

    void ApplyDampingToExistingParticles()
    {
        if (particles == null) return;
        for (int i = 0; i < particles.Length; i++)
        {
            var p = particles[i];
            if (p == null) continue;
            var rb = p.GetComponent<Rigidbody2D>();
            if (rb == null) continue;
#if UNITY_2022_1_OR_NEWER
            rb.linearDamping = damping;
            rb.angularDamping = damping;
#else
            rb.drag = damping;
            rb.angularDrag = damping;
#endif
        }
    }

    void UpdatePauseState()
    {
        if (paused)
        {
            if (!_wasPaused)
            {
                Physics2D.simulationMode = SimulationMode2D.Script; // stop gravity & collisions advancing
                _wasPaused = true;
            }
        }
        else
        {
            if (_wasPaused)
            {
                Physics2D.simulationMode = SimulationMode2D.FixedUpdate;
                _wasPaused = false;
            }
        }
    }

    void TryStepWhilePaused()
    {
        if (!paused || !_stepRequested) return;
        float dt = (overrideFixedDelta ? customFixedDelta : Time.fixedDeltaTime);
        SPHStep(dt);
        ApplyMouseForces();
        if (useAABBBounds) ConstrainParticlesToBox();
        Physics2D.Simulate(dt);
        _stepRequested = false;
    }

    void HandlePauseInput()
    {
#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb == null) return;
        var pauseKey = GetKeyControl(togglePauseKey);
        if (pauseKey != null && pauseKey.wasPressedThisFrame) paused = !paused;
        if (paused)
        {
            var stepKeyCtrl = GetKeyControl(stepKey);
            if (stepKeyCtrl != null && stepKeyCtrl.wasPressedThisFrame) _stepRequested = true;
        }
#else
        if (Input.GetKeyDown(togglePauseKey)) paused = !paused;
        if (paused && Input.GetKeyDown(stepKey)) _stepRequested = true;
#endif
    }

    void CaptureMouseInput()
    {
        if (!enableMouseForces) { _mouseAttract = _mouseRepel = false; return; }
        var cam = Camera.main;
        if (cam == null) return;
#if ENABLE_INPUT_SYSTEM
        if (Mouse.current == null) { _mouseAttract = _mouseRepel = false; return; }
        Vector2 sp = Mouse.current.position.ReadValue();
        var mp = new Vector3(sp.x, sp.y, -cam.transform.position.z);
        _mouseWorld = cam.ScreenToWorldPoint(mp);
        _mouseAttract = Mouse.current.leftButton.isPressed;
        _mouseRepel   = Mouse.current.rightButton.isPressed;
#else
        Vector3 mp = Input.mousePosition;
        mp.z = -cam.transform.position.z;
        _mouseWorld = cam.ScreenToWorldPoint(mp);
        _mouseAttract = Input.GetMouseButton(0);
        _mouseRepel   = Input.GetMouseButton(1);
#endif
    }

#if ENABLE_INPUT_SYSTEM
    KeyControl GetKeyControl(KeyCode code)
    {
        var kb = Keyboard.current;
        if (kb == null) return null;
        switch (code)
        {
            case KeyCode.Space: return kb.spaceKey;
            case KeyCode.F:     return kb.fKey;
            case KeyCode.LeftArrow:  return kb.leftArrowKey;
            case KeyCode.RightArrow: return kb.rightArrowKey;
            case KeyCode.UpArrow:    return kb.upArrowKey;
            case KeyCode.DownArrow:  return kb.downArrowKey;
            case KeyCode.Escape:     return kb.escapeKey;
            case KeyCode.Return:     return kb.enterKey;
            case KeyCode.Tab:        return kb.tabKey;
            // Extend as needed
            default: return null;
        }
    }
#endif

    void ApplyMouseForces()
    {
        if (!enableMouseForces || (!(_mouseAttract || _mouseRepel)) || particles == null) return;

        if (mouseLiftOnly)
        {
            float rMax = mouseLiftRadius;
            float rMax2 = rMax * rMax;
            for (int i = 0; i < particles.Length; i++)
            {
                var rb = particles[i]?.GetComponent<Rigidbody2D>();
                if (rb == null) continue;

                Vector2 to = _mouseWorld - rb.position;
                float d2 = to.sqrMagnitude;
                if (d2 > rMax2) continue;

                float d = Mathf.Sqrt(d2);
                float t = d / Mathf.Max(rMax, 0.0001f);
                float fall = mouseLiftFalloff != null ? mouseLiftFalloff.Evaluate(t) : (1f - t);

                // Pure upward lift
                Vector2 force = Vector2.up * (mouseLiftStrength * fall);
                rb.AddForce(force, ForceMode2D.Force);
            }
            return;
        }

        // Original radial behavior (kept if mouseLiftOnly = false)
        float rMaxRadial = mouseForceRadius;
        float rMax2Radial = rMaxRadial * rMaxRadial;
        float attract = _mouseAttract ? attractionStrength : 0f;
        float repel = _mouseRepel ? repulsionStrength : 0f;

        for (int i = 0; i < particles.Length; i++)
        {
            var rb = particles[i]?.GetComponent<Rigidbody2D>();
            if (rb == null) continue;

            Vector2 to = _mouseWorld - rb.position;
            float d2 = to.sqrMagnitude;
            if (d2 > rMax2Radial || d2 <= 1e-8f) continue;

            float d = Mathf.Sqrt(d2);
            float t = d / rMaxRadial;
            float fall = forceFalloff != null ? forceFalloff.Evaluate(t) : (1f - t);
            Vector2 dir = to / d;

            Vector2 force = Vector2.zero;
            if (attract > 0f) force += dir * (attract * fall);
            if (repel > 0f) force -= dir * (repel * fall);
            rb.AddForce(force, ForceMode2D.Force);
        }
    }

    void RemoveLegacyWallColliders()
    {
        var cb = FindFirstObjectByType<ContainerBuilder>();
        if (cb == null) return;
        var children = cb.GetComponentsInChildren<Transform>(true);
        foreach (var t in children)
        {
            if (t == cb.transform) continue;
            if (t.name == "WallFrame") continue;
            var bc2d = t.GetComponent<Collider2D>();
            if (bc2d != null)
            {
                Destroy(bc2d);
            }
        }
    }

    #if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        var cb = FindFirstObjectByType<ContainerBuilder>();
        if (cb == null) return;
        Vector2 half = cb.GetClampHalfExtents() - Vector2.one * (particleDiameter * 0.5f);
        Gizmos.color = Color.green;
        Vector3 c = cb.transform.position;
        Gizmos.DrawWireCube(c, new Vector3(half.x * 2f, half.y * 2f, 0.01f));
    }
    #endif
}