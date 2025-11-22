// Frame-only container without thickness offset. Frame line width is purely visual; clamp uses raw width/height.

using UnityEngine;

[ExecuteAlways]
public class ContainerBuilder : MonoBehaviour
{
    [Header("Dimensions")]
    public float width = 6f;
    public float height = 3f;
    public float transitionSpeed = 4f;

    [Header("Camera Fit")]
    public float cameraMargin = 1f;

    [Header("Frame (visual only)")]
    public bool showFrame = true;
    public Color frameColor = Color.black;
    public float frameLineWidth = 0.06f;      // no effect on bounds
    public int frameSortingOrder = 10;

    [Header("Spawn Region")]
    [Range(0.05f, 0.95f)]
    public float spawnAreaFraction = 0.5f;

    [Header("Boundary Ghost Particles")]
    public bool generateGhostParticles = true;
    public float ghostSpacing = 0.25f;           // approximate spacing along edges
    public int ghostLayerOrder = -1;             // optional separate layer (or keep default)

    Camera _cam;
    float _curWidth;
    float _curHeight;

    [SerializeField] GameObject _frameGO;
    LineRenderer _lr;

    void Awake()
    {
        _curWidth = width;
        _curHeight = height;
        _cam = Camera.main;
        EnsureFrame();
        ApplyImmediate();
    }

    void OnValidate()
    {
        if (!Application.isPlaying)
        {
            _curWidth = width;
            _curHeight = height;
            EnsureFrame();
            UpdateFrame();
            FitCameraImmediate();
        }
    }

    void Update()
    {
        if (!Mathf.Approximately(_curWidth, width) || !Mathf.Approximately(_curHeight, height))
        {
            float t = 1f - Mathf.Exp(-transitionSpeed * Time.deltaTime);
            _curWidth = Mathf.Lerp(_curWidth, width, t);
            _curHeight = Mathf.Lerp(_curHeight, height, t);
            UpdateFrame();
            FitCameraSmooth(t);
        }
        else
        {
            FitCameraSmooth(1f - Mathf.Exp(-transitionSpeed * Time.deltaTime));
        }
    }

    void EnsureFrame()
    {
        if (_frameGO == null)
        {
            _frameGO = new GameObject("WallFrame");
            _frameGO.transform.SetParent(transform, false);
            _lr = _frameGO.AddComponent<LineRenderer>();
            _lr.useWorldSpace = false;
            _lr.loop = true;
            _lr.positionCount = 4;
            _lr.material = new Material(Shader.Find("Sprites/Default"));
            _lr.textureMode = LineTextureMode.Stretch;
            _lr.sortingOrder = frameSortingOrder;
        }
        else if (_lr == null) _lr = _frameGO.GetComponent<LineRenderer>();
    }

    void UpdateFrame()
    {
        EnsureFrame();
        if (_lr == null) return;
        _frameGO.SetActive(showFrame);
        if (!showFrame) return;

        float hw = _curWidth * 0.5f;
        float hh = _curHeight * 0.5f;

        _lr.startColor = frameColor;
        _lr.endColor = frameColor;
        _lr.startWidth = frameLineWidth;
        _lr.endWidth = frameLineWidth;

        _lr.SetPosition(0, new Vector3(-hw, -hh, 0));
        _lr.SetPosition(1, new Vector3(hw, -hh, 0));
        _lr.SetPosition(2, new Vector3(hw, hh, 0));
        _lr.SetPosition(3, new Vector3(-hw, hh, 0));
    }

    void FitCameraImmediate()
    {
        if (_cam == null) _cam = Camera.main;
        if (_cam == null) return;
        _cam.orthographic = true;
        float halfHNeeded = _curHeight / 2f + cameraMargin;
        float halfHFromWidth = (_curWidth / 2f) / Mathf.Max(_cam.aspect, 0.0001f) + cameraMargin;
        _cam.orthographicSize = Mathf.Max(halfHNeeded, halfHFromWidth);
        _cam.transform.position = new Vector3(transform.position.x, transform.position.y, -10f);
        _cam.backgroundColor = new Color(0.92f, 0.95f, 0.98f);
    }

    void FitCameraSmooth(float t)
    {
        if (_cam == null) _cam = Camera.main;
        if (_cam == null) return;
        float halfHNeeded = _curHeight / 2f + cameraMargin;
        float halfHFromWidth = (_curWidth / 2f) / Mathf.Max(_cam.aspect, 0.0001f) + cameraMargin;
        float targetSize = Mathf.Max(halfHNeeded, halfHFromWidth);
        _cam.orthographicSize = Mathf.Lerp(_cam.orthographicSize, targetSize, t);
        Vector3 targetPos = new Vector3(transform.position.x, transform.position.y, _cam.transform.position.z);
        _cam.transform.position = Vector3.Lerp(_cam.transform.position, targetPos, t);
    }

    public void ApplyImmediate()
    {
        _curWidth = width;
        _curHeight = height;
        UpdateFrame();
        FitCameraImmediate();
    }

    // Bounds used by particles (no thickness subtraction)
    public Vector2 GetClampHalfExtents() => new Vector2(_curWidth * 0.5f, _curHeight * 0.5f);
    public Vector2 GetCurrentSize() => new Vector2(_curWidth, _curHeight);

    public void GetCentralSpawnBounds(out float minX, out float maxX, out float minY, out float maxY)
    {
        float frac = Mathf.Clamp(spawnAreaFraction, 0.05f, 0.95f);
        float w = _curWidth * frac;
        float h = _curHeight * frac;
        minX = -w * 0.5f;
        maxX =  w * 0.5f;
        minY = -h * 0.5f;
        maxY =  h * 0.5f;
    }

    // World-space corner helpers (clockwise)
    public void GetFrameCorners(Vector3[] outCorners)
    {
        if (outCorners == null || outCorners.Length < 4) return;
        float hw = _curWidth * 0.5f;
        float hh = _curHeight * 0.5f;
        Vector3 c = transform.position;
        outCorners[0] = c + new Vector3(-hw, -hh, 0);
        outCorners[1] = c + new Vector3(hw, -hh, 0);
        outCorners[2] = c + new Vector3(hw, hh, 0);
        outCorners[3] = c + new Vector3(-hw, hh, 0);
    }

    public float CurrentWidth => _curWidth;
    public float CurrentHeight => _curHeight;
}