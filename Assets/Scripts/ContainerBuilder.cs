// ...existing code...
using UnityEngine;

[ExecuteAlways]
public class ContainerBuilder : MonoBehaviour
{
    public float width = 6f;
    public float height = 3f;
    public float wallThickness = 0.2f;
    public Color wallColor = Color.white;
    public float cameraMargin = 1f;

    [Tooltip("Higher = faster transition")]
    public float transitionSpeed = 4f;

    Sprite pixelSprite;
    Camera cam;

    // runtime state (interpolated)
    float currentWidth;
    float currentHeight;

    GameObject leftWall;
    GameObject rightWall;
    GameObject bottomWall;

    void Awake()
    {
        CreatePixelSprite();

        // remove any leftover wall objects to avoid duplicate editor+runtime copies
        if (Application.isPlaying)
        {
            string[] baseNames = new[] { "LeftWall", "RightWall", "BottomWall" };
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                var child = transform.GetChild(i).gameObject;
                foreach (var bn in baseNames)
                {
                    if (child.name == bn || child.name == bn + "_runtime")
                    {
                        Destroy(child);
                        break;
                    }
                }
            }
        }

        EnsureWalls();
        cam = Camera.main;
        currentWidth = width;
        currentHeight = height;
        ApplyImmediate(); // set initial shape
    }

    void Start()
    {
        // ensure camera properties at start
        if (cam == null) cam = Camera.main;
        if (cam != null) cam.orthographic = true;
    }

    void Update()
    {
        // smooth towards target inspector values
        if (!Mathf.Approximately(currentWidth, width) || !Mathf.Approximately(currentHeight, height))
        {
            float t = 1f - Mathf.Exp(-transitionSpeed * Time.deltaTime); // smooth exponential lerp
            currentWidth = Mathf.Lerp(currentWidth, width, t);
            currentHeight = Mathf.Lerp(currentHeight, height, t);
            UpdateWallsTransform();
        }

        // smooth camera fit
        if (cam == null) cam = Camera.main;
        if (cam != null)
        {
            float requiredHalfHeight = currentHeight / 2f + cameraMargin;
            float requiredHalfHeightFromWidth = (currentWidth / 2f) / Mathf.Max(cam.aspect, 0.0001f) + cameraMargin;
            float targetSize = Mathf.Max(requiredHalfHeight, requiredHalfHeightFromWidth);
            // lerp camera size smoothly
            cam.orthographicSize = Mathf.Lerp(cam.orthographicSize, targetSize, 1f - Mathf.Exp(-transitionSpeed * Time.deltaTime));
            // keep camera centered on this object
            Vector3 targetCamPos = new Vector3(transform.position.x, transform.position.y, cam.transform.position.z);
            cam.transform.position = Vector3.Lerp(cam.transform.position, targetCamPos, 1f - Mathf.Exp(-transitionSpeed * Time.deltaTime));
            cam.backgroundColor = new Color(0.92f, 0.95f, 0.98f);
        }
    }

    void CreatePixelSprite()
    {
        if (pixelSprite != null) return;
        Texture2D pixelTex = new Texture2D(1, 1);
        pixelTex.SetPixel(0, 0, Color.white);
        pixelTex.Apply();
        pixelSprite = Sprite.Create(pixelTex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
    }

    void EnsureWalls()
    {
        if (leftWall == null) leftWall = CreateWall("LeftWall");
        if (rightWall == null) rightWall = CreateWall("RightWall");
        if (bottomWall == null) bottomWall = CreateWall("BottomWall");
        UpdateWallsTransform();
    }

    GameObject CreateWall(string name)
    {
        // create editor walls with plain name, runtime walls with "_runtime" suffix
        string actualName = name + (Application.isPlaying ? "_runtime" : "");

        // return existing if present
        var found = transform.Find(actualName);
        if (found != null) return found.gameObject;

        // when editing, prefer reusing an editor-created child with the plain name
        if (!Application.isPlaying)
        {
            var editorFound = transform.Find(name);
            if (editorFound != null) return editorFound.gameObject;
        }

        var g = new GameObject(actualName);
        g.transform.parent = transform;
        g.transform.localRotation = Quaternion.identity;

        var sr = g.AddComponent<SpriteRenderer>();
        sr.sprite = pixelSprite;
        sr.color = wallColor;

        var col = g.AddComponent<BoxCollider2D>();
        col.size = Vector2.one; // transform scale controls actual world size

        // Add a kinematic Rigidbody2D so the physics engine treats wall movements/scale changes correctly
        var rb = g.AddComponent<Rigidbody2D>();
        rb.bodyType = RigidbodyType2D.Kinematic;
        rb.simulated = true;
        rb.interpolation = RigidbodyInterpolation2D.Interpolate;

        return g;
    }

    void UpdateWallsTransform()
    {
        if (leftWall == null || rightWall == null || bottomWall == null) return;

        // left
        Vector2 leftPos = new Vector2(-currentWidth / 2f - wallThickness / 2f, 0f);
        Vector3 leftScale = new Vector3(wallThickness, currentHeight + wallThickness, 1f);
        leftWall.transform.localPosition = leftPos;
        leftWall.transform.localScale = leftScale;
        leftWall.GetComponent<SpriteRenderer>().color = wallColor;

        // right
        Vector2 rightPos = new Vector2(currentWidth / 2f + wallThickness / 2f, 0f);
        Vector3 rightScale = leftScale;
        rightWall.transform.localPosition = rightPos;
        rightWall.transform.localScale = rightScale;
        rightWall.GetComponent<SpriteRenderer>().color = wallColor;

        // bottom
        Vector2 bottomPos = new Vector2(0f, -currentHeight / 2f - wallThickness / 2f);
        Vector3 bottomScale = new Vector3(currentWidth + wallThickness * 2f, wallThickness, 1f);
        bottomWall.transform.localPosition = bottomPos;
        bottomWall.transform.localScale = bottomScale;
        bottomWall.GetComponent<SpriteRenderer>().color = wallColor;

        // Ensure physics sees the transform changes immediately (reduces missed collisions)
        Physics2D.SyncTransforms();
    }

    // instant apply (useful if you want immediate change)
    public void ApplyImmediate()
    {
        currentWidth = width;
        currentHeight = height;
        UpdateWallsTransform();
        if (cam == null) cam = Camera.main;
        if (cam != null)
        {
            cam.orthographic = true;
            float requiredHalfHeight = height / 2f + cameraMargin;
            float requiredHalfHeightFromWidth = (width / 2f) / Mathf.Max(cam.aspect, 0.0001f) + cameraMargin;
            cam.orthographicSize = Mathf.Max(requiredHalfHeight, requiredHalfHeightFromWidth);
            cam.transform.position = new Vector3(transform.position.x, transform.position.y, -10f);
            cam.backgroundColor = new Color(0.92f, 0.95f, 0.98f);
        }
    }

    void OnValidate()
    {
        CreatePixelSprite();
        // create walls in editor when values change so you see immediate feedback
        if (!Application.isPlaying)
        {
            EnsureWalls();
            ApplyImmediate();
        }
    }
}