using UnityEngine;

// 1 scene unit = 1 mm.
// Model: rough surface (patch)  ->  thin lens  ->  sensor pixel array, all inside a mouse of "mouseHeightMM" thickness.
[RequireComponent(typeof(Camera))]
public class Main_SimulationSensor_3_2 : MonoBehaviour
{
    public enum MouseMotion { Static, Linear, Circular }

    [Header("Dependencies")]
    [SerializeField] private ComputeShader hybridShader;

    [Header("Light Source Settings")]
    [SerializeField] private Vector3 lightPosition = new Vector3(0.5f, 2.0f, 3.8f);
    [SerializeField] private float lightIntensity = 150000.0f;

    // ------------------------------------------------------------------ Mouse body
    [Header("Mouse Body (1 unit = 1 mm)")]
    [Tooltip("Total mouse thickness: distance from the surface up to the sensor. The lens and the chip must fit inside it.")]
    [SerializeField, Min(0.1f)] private float mouseHeightMM = 3.0f;
    [Tooltip("Draw the sensor plane and the lens disk in the scene.")]
    [SerializeField] private bool showOptics = true;

    // ------------------------------------------------------------------ Mouse motion
    [Header("Mouse Motion (mm, on the surface)")]
    [SerializeField] private MouseMotion motion = MouseMotion.Static;
    [Tooltip("Mouse position (X, Z) at rest / center of the motion.")]
    [SerializeField] private Vector2 mouseStartXZ = new Vector2(0.0f, 0.0f);
    [Tooltip("Linear: travel (X, Z) either side of the start.  Circular: X = radius.")]
    [SerializeField] private Vector2 motionAmplitude = new Vector2(1.0f, 0.0f);
    [Tooltip("Cycles per second.")]
    [SerializeField] private float motionFrequencyHz = 0.25f;

    // ------------------------------------------------------------------ Lens
    [Header("Lens Design")]
    [Tooltip("ON: focal length and lens position are calculated from the mouse height and the magnification, so the surface is exactly in focus.")]
    [SerializeField] private bool autoDesignLens = true;
    [Tooltip("Image size / object size. 1 = sensor sees a surface area equal to its own size.")]
    [SerializeField, Min(0.05f)] private float magnification = 1.0f;
    [Tooltip("Used only when Auto Design Lens is OFF.")]
    [SerializeField, Min(0.01f)] private float manualFocalLengthMM = 0.75f;
    [Tooltip("Used only when Auto Design Lens is OFF. Distance from the lens to the sensor.")]
    [SerializeField, Min(0.01f)] private float manualLensToSensorMM = 1.5f;
    [Tooltip("Aperture diameter. Bigger = brighter but a shallower depth of field.")]
    [SerializeField, Min(0.01f)] private float lensDiameterMM = 0.5f;

    [Header("Lens Results (calculated - edits are ignored)")]
    [SerializeField] private float focalLengthMM;
    [SerializeField] private float lensToSensorMM;
    [SerializeField] private float lensToSurfaceFocusMM;
    [Tooltip("Height of the in-focus plane above the surface reference. 0 = perfectly focused.")]
    [SerializeField] private float focusErrorMM;
    [SerializeField] private float fNumber;
    [Tooltip("Surface area (mm) seen by the sensor at the focus plane.")]
    [SerializeField] private Vector2 fieldOfViewMM;

    // ------------------------------------------------------------------ Sensor
    [Header("Sensor Plane")]
    [Tooltip("Total width and depth of the sensor chip in mm.")]
    [SerializeField] private Vector2 sensorSize = new Vector2(3.0f, 3.0f);
    [SerializeField] private Color sensorColor = new Color(0.05f, 0.35f, 0.45f, 1.0f);
    [Tooltip("Show the image captured by the sensor on the plane (instead of a blank plane).")]
    [SerializeField] private bool showSensorImage = true;
    [Tooltip("Rays traced per sensor pixel per frame (accumulated over frames while the mouse is still).")]
    [SerializeField, Range(1, 256)] private int sensorSamples = 32;
    [SerializeField] private float sensorExposure = 1.0f;

    [Header("Sensor Grid")]
    [SerializeField] private bool showGrid = true;
    [Tooltip("Number of sensor pixels along X and Z (the texture size).")]
    [SerializeField] private Vector2Int gridCells = new Vector2Int(30, 30);
    [Tooltip("Grid line thickness as a fraction of one cell.")]
    [SerializeField, Range(0.0f, 0.5f)] private float gridLineWidth = 0.06f;
    [SerializeField] private Color gridLineColor = new Color(0.45f, 0.80f, 0.90f, 1.0f);
    [Tooltip("Outer border thickness as a fraction of one cell.")]
    [SerializeField, Range(0.0f, 0.5f)] private float borderWidth = 0.12f;
    [SerializeField] private Color borderColor = new Color(0.85f, 0.95f, 1.0f, 1.0f);

    // ------------------------------------------------------------------ Surface
    [Header("Rough Surface Patch")]
    [SerializeField] private Vector3 patchCenter = new Vector3(0.0f, -1.0f, 0.0f);
    [Tooltip("Total side length of the square patch in mm. Must cover the sensor field of view plus the mouse travel.")]
    [SerializeField] private float patchSize = 10.0f;
    [Tooltip("Bump height scale. Max bump height is 2x this value.")]
    [SerializeField] private float patchAmplitude = 0.25f;
    [Tooltip("Base noise frequency. Higher = smaller, denser bumps.")]
    [SerializeField] private float patchNoiseFrequency = 3.0f;
    [SerializeField] private Color patchLowColor = new Color(0.25f, 0.18f, 0.12f, 1.0f);
    [SerializeField] private Color patchHighColor = new Color(0.85f, 0.75f, 0.6f, 1.0f);

    [Header("Performance Optimization")]
    [Tooltip("Dynamic ceiling boundary passed as a uniform to break compiler evaluation bottlenecks and ensure instant loading.")]
    [SerializeField] private float intensityClamp = 5000.0f;

    private Camera _camera;
    private RenderTexture _targetBuffer;
    private RenderTexture _accumulationBuffer;
    private RenderTexture _sensorImage;
    private int _currentSample = 0;
    private int _sensorSample = 0;
    private Vector2 _lastMousePos;
    private bool _hasLastMousePos = false;

    private void Start()
    {
        _camera = GetComponent<Camera>();
    }

    private void OnValidate()
    {
        _currentSample = 0;
        _sensorSample = 0;
        UpdateLensDesign();
    }

    // Mouse (sensor + lens) position on the surface for the chosen motion.
    private Vector2 ComputeMousePosition(float t)
    {
        float w = 2.0f * Mathf.PI * motionFrequencyHz;
        switch (motion)
        {
            case MouseMotion.Linear:
                return mouseStartXZ + motionAmplitude * Mathf.Sin(w * t);
            case MouseMotion.Circular:
                return mouseStartXZ + new Vector2(Mathf.Cos(w * t), Mathf.Sin(w * t)) * motionAmplitude.x;
            default:
                return mouseStartXZ;
        }
    }

    // Thin lens: 1/f = 1/s_o + 1/s_i,  s_o + s_i = mouse height (total track), m = s_i / s_o.
    //   s_o = T / (1 + m),   s_i = m T / (1 + m),   f = m T / (1 + m)^2
    private void UpdateLensDesign()
    {
        float T = Mathf.Max(mouseHeightMM, 0.1f);
        float f, si;
        if (autoDesignLens)
        {
            float m = Mathf.Max(magnification, 0.05f);
            f = m * T / ((1.0f + m) * (1.0f + m));
            si = m * T / (1.0f + m);
        }
        else
        {
            f = Mathf.Max(manualFocalLengthMM, 0.001f);
            si = manualLensToSensorMM;
        }

        si = Mathf.Max(si, f * 1.001f);                 // a real image needs s_i > f
        float so = f * si / (si - f);                   // thin lens equation

        focalLengthMM = f;
        lensToSensorMM = si;
        lensToSurfaceFocusMM = so;
        focusErrorMM = T - si - so;                     // in-focus plane height above the surface reference
        fNumber = f / Mathf.Max(lensDiameterMM, 0.001f);
        float mag = si / so;
        fieldOfViewMM = new Vector2(sensorSize.x, sensorSize.y) / Mathf.Max(mag, 1e-4f);
    }

    private void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        int width = source.width;
        int height = source.height;

        EnsureBuffersCreated(width, height);
        EnsureSensorImage();
        UpdateLensDesign();

        // --- Mouse position and the geometry derived from it ---
        Vector2 mousePos = ComputeMousePosition(Time.time);
        bool mouseMoved = !_hasLastMousePos || (mousePos - _lastMousePos).sqrMagnitude > 1e-12f;
        _lastMousePos = mousePos;
        _hasLastMousePos = true;

        if (mouseMoved)
        {
            _currentSample = 0;     // camera image restarts
            _sensorSample = 0;      // sensor image restarts
        }

        if (transform.hasChanged)
        {
            _currentSample = 0;
            transform.hasChanged = false;
        }

        _currentSample++;
        _sensorSample++;

        // Surface reference = mean height of the noise surface (about 0.875 x amplitude above the patch base).
        float surfaceRefY = patchCenter.y + Mathf.Max(patchAmplitude, 0.0f) * 0.875f;
        float sensorY = surfaceRefY + mouseHeightMM;
        float lensY = sensorY - lensToSensorMM;

        int kernelMain = hybridShader.FindKernel("CSMain");
        int kernelSensor = hybridShader.FindKernel("CSSensor");

        // --- Uniform Bindings ---
        hybridShader.SetFloat("_Time", Time.time);
        hybridShader.SetInt("_SampleIndex", _currentSample);
        hybridShader.SetVector("_LightPosition", new Vector4(lightPosition.x, lightPosition.y, lightPosition.z, 1.0f));
        hybridShader.SetFloat("_LightIntensity", lightIntensity);
        hybridShader.SetFloat("_IntensityClamp", intensityClamp);

        // --- Sensor plane + grid ---
        int cellsX = Mathf.Clamp(gridCells.x, 1, 256);
        int cellsY = Mathf.Clamp(gridCells.y, 1, 256);
        hybridShader.SetVector("_SensorCenter", new Vector4(mousePos.x, sensorY, mousePos.y, 0.0f));
        hybridShader.SetVector("_SensorHalfSize", new Vector4(Mathf.Max(sensorSize.x, 0.001f) * 0.5f, Mathf.Max(sensorSize.y, 0.001f) * 0.5f, 0, 0));
        hybridShader.SetVector("_SensorGridCells", new Vector4(cellsX, cellsY, 0, 0));
        hybridShader.SetFloat("_SensorGridEnabled", showGrid ? 1.0f : 0.0f);
        hybridShader.SetFloat("_SensorLineWidth", gridLineWidth);
        hybridShader.SetFloat("_SensorBorderWidth", borderWidth);
        hybridShader.SetVector("_SensorColor", ColorToVector(sensorColor));
        hybridShader.SetVector("_SensorGridColor", ColorToVector(gridLineColor));
        hybridShader.SetVector("_SensorBorderColor", ColorToVector(borderColor));

        // --- Lens / sensor simulation ---
        hybridShader.SetFloat("_LensY", lensY);
        hybridShader.SetFloat("_LensRadius", Mathf.Max(lensDiameterMM, 0.001f) * 0.5f);
        hybridShader.SetFloat("_SensorImageDist", lensToSensorMM);
        hybridShader.SetFloat("_ObjectDist", lensToSurfaceFocusMM);
        hybridShader.SetInt("_SensorSamples", sensorSamples);
        hybridShader.SetFloat("_SensorBlend", 1.0f / _sensorSample);
        hybridShader.SetFloat("_ShowSensorImage", showSensorImage ? 1.0f : 0.0f);
        hybridShader.SetFloat("_SensorExposure", sensorExposure);
        hybridShader.SetFloat("_ShowOptics", showOptics ? 1.0f : 0.0f);

        // --- Rough surface patch ---
        hybridShader.SetVector("_PatchCenter", patchCenter);
        hybridShader.SetFloat("_PatchHalfSize", Mathf.Max(patchSize, 0.001f) * 0.5f);
        hybridShader.SetFloat("_PatchAmplitude", Mathf.Max(patchAmplitude, 0.0f));
        hybridShader.SetFloat("_PatchNoiseFrequency", patchNoiseFrequency);
        hybridShader.SetVector("_PatchLowColor", ColorToVector(patchLowColor));
        hybridShader.SetVector("_PatchHighColor", ColorToVector(patchHighColor));

        // --- Pass 1: sensor image (thin lens imaging the surface onto the pixel array) ---
        hybridShader.SetTexture(kernelSensor, "SensorImage", _sensorImage);
        hybridShader.Dispatch(kernelSensor, Mathf.CeilToInt(_sensorImage.width / 8f), Mathf.CeilToInt(_sensorImage.height / 8f), 1);

        // --- Pass 2: camera view (spectral path tracing) ---
        hybridShader.SetTexture(kernelMain, "SensorImage", _sensorImage);
        hybridShader.SetTexture(kernelMain, "AccumulationBuffer", _accumulationBuffer);
        hybridShader.SetTexture(kernelMain, "Result", _targetBuffer);

        hybridShader.SetMatrix("_CameraToWorld", _camera.cameraToWorldMatrix);
        hybridShader.SetMatrix("_CameraInverseProjection", _camera.projectionMatrix.inverse);

        int threadGroupsX = Mathf.CeilToInt(width / 32f);
        int threadGroupsY = Mathf.CeilToInt(height / 32f);
        hybridShader.Dispatch(kernelMain, threadGroupsX, threadGroupsY, 1);

        // Send final composite to screen
        Graphics.Blit(_targetBuffer, destination);
    }

    // Passes the color values as-is (no gamma-to-linear conversion), matching the original shader values.
    private static Vector4 ColorToVector(Color c)
    {
        return new Vector4(c.r, c.g, c.b, c.a);
    }

    private void EnsureSensorImage()
    {
        int w = Mathf.Clamp(gridCells.x, 1, 256);
        int h = Mathf.Clamp(gridCells.y, 1, 256);
        if (_sensorImage == null || _sensorImage.width != w || _sensorImage.height != h)
        {
            if (_sensorImage != null) _sensorImage.Release();
            _sensorImage = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
            {
                enableRandomWrite = true,
                filterMode = FilterMode.Point
            };
            _sensorImage.Create();
            _sensorSample = 0;
        }
    }

    private void EnsureBuffersCreated(int width, int height)
    {
        if (_targetBuffer == null || _targetBuffer.width != width || _targetBuffer.height != height)
        {
            if (_targetBuffer != null) _targetBuffer.Release();
            _targetBuffer = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear) { enableRandomWrite = true };
            _targetBuffer.Create();
        }

        if (_accumulationBuffer == null || _accumulationBuffer.width != width || _accumulationBuffer.height != height)
        {
            if (_accumulationBuffer != null) _accumulationBuffer.Release();
            _accumulationBuffer = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear) { enableRandomWrite = true };
            _accumulationBuffer.Create();
            _currentSample = 0;
        }
    }

    private void OnDisable()
    {
        if (_targetBuffer != null) _targetBuffer.Release();
        if (_accumulationBuffer != null) _accumulationBuffer.Release();
        if (_sensorImage != null) _sensorImage.Release();
    }
}
