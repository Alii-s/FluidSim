using System.Collections.Generic;
using System.Threading.Tasks;
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

    [Header("SPH (Parallel Only)")]
    public float smoothingRadius = 0.4f;     // h
    public float restDensity = 1f;           // ρ0
    public float particleMass = 1f;          // m
    public float stiffness = 50f;            // k
    public float viscosity = 0.1f;           // μ
    public float maxForceClamp = 200f;
    public bool autoSetRestDensity = true;   // <-- add this

    [Header("Velocity Color")]
    public bool enableVelocityColor = true;
    public int velocityColorSkipFrames = 0;
    public float maxColorSpeed = 1f;
    public Color slowColor = Color.cyan;
    public Color fastColor = Color.red;

    [Header("Collision Control")]
    public bool disableParticleSelfCollision = true;
    public string particleLayerName = "Particles";

    [Header("Spatial Hashing")]
    public bool useSpatialHash = true;

    [Header("Bounds")]
    public float boundsBounceFactor = 1f; // velocity inversion scale
    public bool useAABBBounds = true;     // enable manual box constraint when physical walls disabled

    [Header("Boundary Control")]
    public bool spawnOnFrameEdges = false;
    public bool useWallRepulsion = true;
    public float wallRepulsionStrength = 40f;
    public float wallRepulsionDistance = 0.3f;
    public bool useGhostBoundary = true;
    public float ghostSpacing = 0.25f;

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

    // Change tracking
    float prevParticleDiameter;
    float prevBounce;
    float prevDamping;
    float prevGravityTransitionSpeed;
    float prevPlacementMargin;

    // Ghost boundary storage
    Vector2[] ghostPositions;
    int ghostCount;

    void Awake()
    {
        currentGravity = gravity;
        Physics2D.gravity = currentGravity;
        CreateCircleSprite();
        if (particlePrefab == null)
            particlePrefab = CreateDefaultParticlePrefab();
        _particleLayerIndex = LayerMask.NameToLayer(particleLayerName);
        prevParticleDiameter = particleDiameter;
        prevBounce = bounce;
        prevDamping = damping;
        prevGravityTransitionSpeed = gravityTransitionSpeed;
        prevPlacementMargin = placementMargin;
    }

    void Start()
    {
        SpawnAllInsideContainer();
        if (useGhostBoundary) GenerateGhostBoundaryParticles();

        if (autoSetRestDensity)
        {
            PrecomputeKernelConstants();
            restDensity = ComputeAverageInitialDensity();
            if (densities != null)
                for (int i = 0; i < densities.Length; i++) densities[i] = restDensity;
        }

        ApplyAllToExistingParticles();
        ApplyParticleLayerSettings();
    }

    void Update()
    {
        // Smooth gravity
        if (!Mathf.Approximately(currentGravity.x, gravity.x) || !Mathf.Approximately(currentGravity.y, gravity.y))
        {
            float t = 1f - Mathf.Exp(-gravityTransitionSpeed * Time.deltaTime);
            currentGravity = Vector2.Lerp(currentGravity, gravity, t);
            Physics2D.gravity = currentGravity;
        }

        // Live adjustments
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
        SPHStep(); // always parallel SPH
        if (useAABBBounds) ConstrainParticlesToBox();
    }

    void SPHStep()
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

        float dt = Time.fixedDeltaTime;
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

    // ----- Spatial Hash -----
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
        if (_hash == null) _hash = new Dictionary<Vector2Int, List<int>>();
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

    // ----- Density (parallel, predicted) -----
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

            // Fluid-fluid
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

            // Ghost contribution (acts like solid boundary density support)
            if (useGhostBoundary && ghostPositions != null)
            {
                for (int g = 0; g < ghostCount; g++)
                {
                    float r2 = (pi - ghostPositions[g]).sqrMagnitude;
                    if (r2 >= h2) continue;
                    float term = h2 - r2;
                    rho += particleMass * _poly6Const * term * term * term;
                }
            }

            densities[i] = Mathf.Max(rho, restDensity * 0.5f);
        });
    }

    // ----- Pressure -----
    void ComputePressures(int n)
    {
        for (int i = 0; i < n; i++)
        {
            float rho = densities[i];
            pressures[i] = stiffness * Mathf.Max(rho - restDensity, 0f);
        }
    }

    // ----- Forces (parallel, predicted) -----
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

        for (int i = 0; i < n; i++)
        {
            var rb = particles[i]?.GetComponent<Rigidbody2D>();
            if (rb == null) continue;
            rb.AddForce(_forceAccum[i], ForceMode2D.Force);
        }

        // After computing _forceAccum for SPH:
        if (useWallRepulsion)
        {
            ApplyWallRepulsion(n);
        }
    }

    // ----- Kernel constants -----
    void PrecomputeKernelConstants()
    {
        float h = smoothingRadius;
        _poly6Const = 4f / (Mathf.PI * Mathf.Pow(h, 8));
        _spikyGradConst = -30f / (Mathf.PI * Mathf.Pow(h, 5));
        _viscLapConst = 20f / (3f * Mathf.PI * Mathf.Pow(h, 5));
    }

    // ----- Utility / visuals / collision -----
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
            sr.color = Color.Lerp(slowColor, fastColor, t);
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
                sr.color = slowColor;
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

    void SpawnAllInsideContainer()
    {
        ContainerBuilder cb = FindFirstObjectByType<ContainerBuilder>();

        float minX, maxX, minY, maxY;
        if (cb != null && !spawnOnFrameEdges)
            cb.GetCentralSpawnBounds(out minX, out maxX, out minY, out maxY);
        else if (cb != null && spawnOnFrameEdges)
        {
            // Use full frame extents
            float hw = cb.GetClampHalfExtents().x;
            float hh = cb.GetClampHalfExtents().y;
            minX = -hw; maxX = hw;
            minY = -hh; maxY = hh;
        }
        else
        {
            float halfW = 3f;
            float halfH = 2f;
            minX = -halfW * 0.5f; maxX = halfW * 0.5f;
            minY = -halfH * 0.5f; maxY = halfH * 0.5f;
        }

        float radius = particleDiameter * 0.5f;

        // When spawning on edges: tighten so centers lie on frame line
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
            // Distribute particles along perimeter
            float widthSpan = maxX - minX;
            float heightSpan = maxY - minY;
            float perimeter = 2f * (widthSpan + heightSpan);
            float spacing = Mathf.Max(radius * 2f, perimeter / spawnCount);
            int created = 0;
            float halfW = widthSpan * 0.5f;
            float halfH = heightSpan * 0.5f;
            // Loop edges until fill
            while (created < spawnCount)
            {
                // Param along perimeter
                float s = (created * spacing) % perimeter;
                Vector2 pos;
                if (s < widthSpan) // bottom edge left->right
                    pos = new Vector2(minX + s, minY);
                else if (s < widthSpan + heightSpan) // right edge bottom->top
                    pos = new Vector2(maxX, minY + (s - widthSpan));
                else if (s < widthSpan + heightSpan + widthSpan) // top edge right->left
                    pos = new Vector2(maxX - (s - widthSpan - heightSpan), maxY);
                else // left edge top->bottom
                    pos = new Vector2(minX, maxY - (s - widthSpan - heightSpan - widthSpan));

                CreateFluidParticle(created, pos);
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
                    float x = Random.Range(minX, maxX);
                    float y = Random.Range(minY, maxY);
                    Vector2 cand = new Vector2(x, y);
                    bool overlaps = false;
                    float minDistSqr = (2f * radius) * (2f * radius);
                    for (int j = 0; j < placed.Count; j++)
                        if ((placed[j] - cand).sqrMagnitude < minDistSqr) { overlaps = true; break; }
                    if (!overlaps) { chosen = cand; found = true; break; }
                }
                if (!found) chosen = new Vector2(Random.Range(minX, maxX), Random.Range(minY, maxY));
                placed.Add(chosen);
                CreateFluidParticle(i, chosen);
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

    // Ghost boundary particles (not Rigidbody2D). They only appear in SPH neighbor queries.
    void GenerateGhostBoundaryParticles()
    {
        var cb = FindFirstObjectByType<ContainerBuilder>();
        if (cb == null) { ghostPositions = null; ghostCount = 0; return; }

        float hw = cb.GetClampHalfExtents().x;
        float hh = cb.GetClampHalfExtents().y;
        float spacing = Mathf.Max(ghostSpacing, smoothingRadius * 0.5f);

        List<Vector2> list = new List<Vector2>();

        // Bottom & Top
        for (float x = -hw; x <= hw + 0.0001f; x += spacing)
        {
            list.Add(new Vector2(x, -hh));
            list.Add(new Vector2(x, hh));
        }
        // Left & Right
        for (float y = -hh + spacing; y <= hh - spacing + 0.0001f; y += spacing)
        {
            list.Add(new Vector2(-hw, y));
            list.Add(new Vector2(hw, y));
        }

        ghostCount = list.Count;
        ghostPositions = list.ToArray();
    }

    // Wall repulsion (soft) – call before AddForce loop
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
            float pad = particleDiameter * 0.5f;

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

    void SetColliderBounce(CircleCollider2D col, float b)
    {
        PhysicsMaterial2D mat = col.sharedMaterial;
        bool needNew = mat == null || !mat.name.Contains("runtime");
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
            ResetVelocityColorsIfNeeded();

        if (disableParticleSelfCollision && _particleLayerIndex != -1)
            Physics2D.IgnoreLayerCollision(_particleLayerIndex, _particleLayerIndex, true);
        else if (!disableParticleSelfCollision && _particleLayerIndex != -1)
            Physics2D.IgnoreLayerCollision(_particleLayerIndex, _particleLayerIndex, false);
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

    // Use frame inner bounds for manual AABB constraint.
    void ConstrainParticlesToBox()
    {
        var cb = FindFirstObjectByType<ContainerBuilder>();
        if (cb == null || particles == null) return;

        Vector2 containerCenter = cb.transform.position; // Add this
        Vector2 half = cb.GetClampHalfExtents() - Vector2.one * (particleDiameter * 0.5f);

        for (int i = 0; i < particles.Length; i++)
        {
            var go = particles[i];
            if (go == null) continue;
            var rb = go.GetComponent<Rigidbody2D>();
            if (rb == null) continue;

            Vector2 pos = rb.position;
            Vector2 localPos = pos - containerCenter; // Make relative to container
            Vector2 vel = rb.linearVelocity;
            bool changed = false;

            if (Mathf.Abs(localPos.x) > half.x)
            {
                localPos.x = half.x * Mathf.Sign(localPos.x);
                vel.x = -vel.x * boundsBounceFactor;
                changed = true;
            }
            if (Mathf.Abs(localPos.y) > half.y)
            {
                localPos.y = half.y * Mathf.Sign(localPos.y);
                vel.y = -vel.y * boundsBounceFactor;
                changed = true;
            }

            if (changed)
            {
                rb.position = containerCenter + localPos; // Convert back to world space
                rb.linearVelocity = vel;
                particlePositions[i] = rb.position;
                particleVelocities[i] = vel;
            }
        }
    }
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
            var col = p.GetComponent<CircleCollider2D>();
            if (col == null) col = p.AddComponent<CircleCollider2D>();
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
}
