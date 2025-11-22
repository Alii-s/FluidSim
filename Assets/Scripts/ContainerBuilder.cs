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
    public float frameLineWidth = 0.06f;
    public int frameSortingOrder = 10;

    [Header("Spawn Region")]
    [Range(0.05f, 0.95f)]
    public float spawnAreaFraction = 0.5f;

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

    public System.Action OnSizeChanged; // notify listeners (ParticleSpawner) when frame size actually changes
    public bool instantResize = false;  // optional: jump to new size immediately

    void Update()
    {
        float prevW = _curWidth;
        float prevH = _curHeight;

        if (instantResize)
        {
            _curWidth = width;
            _curHeight = height;
        }
        else
        {
            // Linear move (no asymptotic stall)
            _curWidth = Mathf.MoveTowards(_curWidth, width, transitionSpeed * Time.deltaTime);
            _curHeight = Mathf.MoveTowards(_curHeight, height, transitionSpeed * Time.deltaTime);
        }

        if (!Mathf.Approximately(prevW, _curWidth) || !Mathf.Approximately(prevH, _curHeight))
        {
            UpdateFrame();
            FitCameraImmediate(); // keep camera in sync for large changes
            OnSizeChanged?.Invoke();
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

    // Bounds helpers (world-scale aware)
    public Vector2 GetClampHalfExtents()
    {
        Vector3 s = transform.lossyScale;
        return new Vector2(_curWidth * 0.5f * s.x, _curHeight * 0.5f * s.y);
    }

    public Vector2 GetCurrentSize()
    {
        Vector3 s = transform.lossyScale;
        return new Vector2(_curWidth * s.x, _curHeight * s.y);
    }

    public void GetCentralSpawnBounds(out float minX, out float maxX, out float minY, out float maxY)
    {
        float f = Mathf.Clamp(spawnAreaFraction, 0.05f, 0.95f);
        float w = _curWidth * f;
        float h = _curHeight * f;
        minX = -w * 0.5f; maxX = w * 0.5f;
        minY = -h * 0.5f; maxY = h * 0.5f;
    }

    // World-space corners (clockwise)
    public void GetFrameCorners(Vector3[] out4)
    {
        if (out4 == null || out4.Length < 4) return;
        float hw = _curWidth * 0.5f, hh = _curHeight * 0.5f;
        Vector3 c = transform.position;
        out4[0] = c + new Vector3(-hw, -hh, 0);
        out4[1] = c + new Vector3(hw, -hh, 0);
        out4[2] = c + new Vector3(hw, hh, 0);
        out4[3] = c + new Vector3(-hw, hh, 0);
    }

    public float CurrentWidth => _curWidth;
    public float CurrentHeight => _curHeight;
}