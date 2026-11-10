using UnityEngine;

[RequireComponent(typeof(Camera))]
public class Main_SimulationSensor_12_2 : MonoBehaviour
{
    public enum MouseMotion { Static, Linear, Circular }

    [Header("Dependencies")]
    [SerializeField] private ComputeShader hybridShader;

    [Header("Light Source Settings")]
    [SerializeField] private Vector3 lightPosition = new Vector3(0.5f, 2.0f, 3.8f);
    [SerializeField] private float lightIntensity = 150000.0f;

    [Header("Mouse Body (1 unit = 1 mm)")]
    [SerializeField, Min(0.1f)] private float mouseHeightMM = 3.50f;
    [SerializeField] private bool showOptics = true;

    [Header("Visual Display Toggles")]
    [SerializeField] private bool showSensorHousing = true;
    [SerializeField] private bool showLensFlange = true;
    [SerializeField] private bool showRoughPatch = true;

    [Header("Mouse Motion (mm, on the surface)")]
    [SerializeField] private MouseMotion motion = MouseMotion.Static;
    [SerializeField] private Vector2 mouseStartXZ = new Vector2(0.0f, 0.0f);
    [SerializeField] private Vector2 motionAmplitude = new Vector2(1.0f, 0.0f);
    [SerializeField] private float motionFrequencyHz = 0.25f;

    [Header("Lens Module Assembly")]
    [SerializeField] private bool autoDesignLens = false;
    [SerializeField, Min(0.05f)] private float magnification = 1.0f;
    [SerializeField, Min(0.01f)] private float manualFocalLengthMM = 1.15f;
    [Tooltip("Distance from lens center to sensor die layer (positions lens outside/below housing box)")]
    [SerializeField, Min(0.01f)] private float manualLensToSensorMM = 2.20f;
    [SerializeField, Min(0.01f)] private float lensDiameterMM = 1.20f;
    [SerializeField] private bool enableLensVisuals = true;

    [Header("Lens Material & Seating Frame (biconvex PMMA)")]
    [SerializeField, Range(1.2f, 2.0f)] private float refractiveIndex = 1.491f;
    [SerializeField, Min(0.005f)] private float lensEdgeThicknessMM = 0.20f;
    [SerializeField, Min(0.0f)] private float lensFlangeWidthMM = 0.40f;
    [SerializeField] private bool useRealLens = true;

    [Header("Lens Results (calculated - edits are ignored)")]
    [SerializeField] private string lensStatus;
    [SerializeField] private float focalLengthMM;
    [SerializeField] private float curvatureRadiusMM;
    [SerializeField] private float centerThicknessMM;
    [SerializeField] private float principalPlaneOffsetMM;
    [SerializeField] private float lensToSensorMM;
    [SerializeField] private float lensToSurfaceFocusMM;
    [SerializeField] private float focusErrorMM;
    [SerializeField] private float fNumber;
    [SerializeField] private Vector2 fieldOfViewMM;

    [Header("Sensor Package & Silicon Die")]
    [SerializeField] private Vector2 sensorSize = new Vector2(2.40f, 2.40f);
    [SerializeField] private Color sensorColor = new Color(0.05f, 0.35f, 0.45f, 1.0f);
    [SerializeField] private bool showSensorImage = true;
    [SerializeField, Range(1, 256)] private int sensorSamples = 64;
    [SerializeField] private float sensorExposure = 2.5f;
    [SerializeField, Range(0.0f, 1.0f)] private float sensorOpacity = 0.35f;

    [Header("Sensor Grid Matrix")]
    [SerializeField] private bool showGrid = true;
    [SerializeField, Range(0, 2)] private int gridDisplayMode = 2; // 0 = Surface Plane Only, 1 = Grid Only, 2 = Both Combined
    [SerializeField] private Vector2Int gridCells = new Vector2Int(36, 36);
    [SerializeField, Range(0.0f, 0.5f)] private float gridLineWidth = 0.06f;
    [SerializeField] private Color gridLineColor = new Color(0.45f, 0.80f, 0.90f, 1.0f);
    [SerializeField, Range(0.0f, 0.5f)] private float borderWidth = 0.12f;
    [SerializeField] private Color borderColor = new Color(0.85f, 0.95f, 1.0f, 1.0f);

    [Header("Rough Surface Patch")]
    [SerializeField] private Vector3 patchCenter = new Vector3(0.0f, -1.0f, 0.0f);
    [SerializeField] private float patchSize = 10.0f;
    [SerializeField] private float patchAmplitude = 0.25f;
    [SerializeField] private float patchNoiseFrequency = 3.0f;
    [SerializeField] private Color patchLowColor = new Color(0.25f, 0.18f, 0.12f, 1.0f);
    [SerializeField] private Color patchHighColor = new Color(0.85f, 0.75f, 0.6f, 1.0f);

    [Header("Performance Optimization")]
    [SerializeField] private float intensityClamp = 5000.0f;

    private Camera _camera;
    private RenderTexture _targetBuffer;
    private RenderTexture _accumulationBuffer;
    private RenderTexture _sensorImage;
    private int _currentSample = 0;
    private int _sensorSample = 0;
    private Vector2 _lastMousePos;
    private bool _hasLastMousePos = false;
    private float _lensD, _lensH, _lensDelta;

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

    private bool SolveLens(float f, float a, out float R, out float d, out float h)
    {
        float n = Mathf.Max(refractiveIndex, 1.01f);
        float e = Mathf.Max(lensEdgeThicknessMM, 0.005f);
        bool fits = true;
        d = e;
        R = 2.0f * (n - 1.0f) * f;
        for (int i = 0; i < 40; i++)
        {
            float disc = Mathf.Max(f * f - f * d / n, 0.0f);
            R = (n - 1.0f) * (f + Mathf.Sqrt(disc));
            if (R < a * 1.001f) { R = a * 1.001f; fits = false; }
            else fits = true;
            float sag = R - Mathf.Sqrt(R * R - a * a);
            d = 2.0f * sag + e;
        }
        h = (n - 1.0f) * f * d / (n * R);
        return fits;
    }

    private void UpdateLensDesign()
    {
        float T = Mathf.Max(mouseHeightMM, 0.1f);
        float a = Mathf.Max(lensDiameterMM, 0.001f) * 0.5f;
        float R = 1.0f, d = 0.0f, h = 0.0f, delta = 0.0f;
        float f, si;
        bool fits;

        if (autoDesignLens)
        {
            float m = Mathf.Max(magnification, 0.05f);
            f = m * T / ((1.0f + m) * (1.0f + m));
            for (int it = 0; it < 12; it++)
            {
                SolveLens(f, a, out R, out d, out h);
                delta = d - 2.0f * h;
                float track = Mathf.Max(T - delta, 0.05f);
                f = m * track / ((1.0f + m) * (1.0f + m));
            }
            si = m * Mathf.Max(T - delta, 0.05f) / (1.0f + m);
        }
        else
        {
            f = Mathf.Max(manualFocalLengthMM, 0.001f);
            si = manualLensToSensorMM;
        }

        fits = SolveLens(f, a, out R, out d, out h);
        delta = d - 2.0f * h;

        si = Mathf.Max(si, f * 1.001f);
        float so = f * si / (si - f);

        _lensD = d; _lensH = h; _lensDelta = delta;

        focalLengthMM = f;
        curvatureRadiusMM = R;
        centerThicknessMM = d;
        principalPlaneOffsetMM = h;
        lensToSensorMM = si;
        lensToSurfaceFocusMM = so;
        focusErrorMM = T - si - delta - so;
        fNumber = f / Mathf.Max(lensDiameterMM, 0.001f);
        float mag = si / so;
        fieldOfViewMM = new Vector2(sensorSize.x, sensorSize.y) / Mathf.Max(mag, 1e-4f);

        float lowestPoint = T - si - delta - h;
        if (!fits) lensStatus = "Aperture too large for this focal length: reduce Lens Diameter.";
        else if (lowestPoint <= 0.0f) lensStatus = "Lens does not fit: it would touch the surface.";
        else if (Mathf.Abs(focusErrorMM) > 0.02f) lensStatus = "Out of focus: surface is not at the focus plane.";
        else lensStatus = "OK - lens fits inside the mouse and the surface is in focus.";
    }

    private void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        int width = source.width;
        int height = source.height;

        EnsureBuffersCreated(width, height);
        EnsureSensorImage();
        UpdateLensDesign();

        Vector2 mousePos = ComputeMousePosition(Time.time);
        bool mouseMoved = !_hasLastMousePos || (mousePos - _lastMousePos).sqrMagnitude > 1e-12f;
        _lastMousePos = mousePos;
        _hasLastMousePos = true;

        if (mouseMoved)
        {
            _currentSample = 0;
            _sensorSample = 0;
        }

        if (transform.hasChanged)
        {
            _currentSample = 0;
            transform.hasChanged = false;
        }

        _currentSample++;
        _sensorSample++;

        float surfaceRefY = patchCenter.y + Mathf.Max(patchAmplitude, 0.0f) * 0.875f;
        float sensorY = surfaceRefY + mouseHeightMM;
        float lensY = sensorY - lensToSensorMM - _lensD * 0.5f + _lensH;
        float focusPlaneY = sensorY - lensToSensorMM - _lensDelta - lensToSurfaceFocusMM;
        float sensorToLensCenter = sensorY - lensY;
        float lensCenterToFocus = lensY - focusPlaneY;

        int kernelMain = hybridShader.FindKernel("CSMain");
        int kernelSensor = hybridShader.FindKernel("CSSensor");

        // --- Uniform Bindings ---
        hybridShader.SetFloat("_Time", Time.time);
        hybridShader.SetInt("_SampleIndex", _currentSample);
        hybridShader.SetVector("_LightPosition", new Vector4(lightPosition.x, lightPosition.y, lightPosition.z, 1.0f));
        hybridShader.SetFloat("_LightIntensity", lightIntensity);
        hybridShader.SetFloat("_IntensityClamp", intensityClamp);

        int cellsX = Mathf.Clamp(gridCells.x, 1, 256);
        int cellsY = Mathf.Clamp(gridCells.y, 1, 256);
        hybridShader.SetVector("_SensorCenter", new Vector4(mousePos.x, sensorY, mousePos.y, 0.0f));
        hybridShader.SetVector("_SensorHalfSize", new Vector4(Mathf.Max(sensorSize.x, 0.001f) * 0.5f, Mathf.Max(sensorSize.y, 0.001f) * 0.5f, 0, 0));
        hybridShader.SetVector("_SensorGridCells", new Vector4(cellsX, cellsY, 0, 0));
        hybridShader.SetFloat("_SensorGridEnabled", showGrid ? 1.0f : 0.0f);
        hybridShader.SetInt("_GridDisplayMode", gridDisplayMode);
        hybridShader.SetFloat("_SensorLineWidth", gridLineWidth);
        hybridShader.SetFloat("_SensorBorderWidth", borderWidth);
        hybridShader.SetVector("_SensorColor", ColorToVector(sensorColor));
        hybridShader.SetVector("_SensorGridColor", ColorToVector(gridLineColor));
        hybridShader.SetVector("_SensorBorderColor", ColorToVector(borderColor));

        hybridShader.SetFloat("_LensY", lensY);
        hybridShader.SetFloat("_LensRadius", Mathf.Max(lensDiameterMM, 0.001f) * 0.5f);
        hybridShader.SetFloat("_SensorImageDist", sensorToLensCenter);
        hybridShader.SetFloat("_ObjectDist", lensCenterToFocus);
        hybridShader.SetFloat("_LensCurvRadius", curvatureRadiusMM);
        hybridShader.SetFloat("_LensThickness", centerThicknessMM);
        hybridShader.SetFloat("_LensEdge", Mathf.Max(lensEdgeThicknessMM, 0.005f));
        hybridShader.SetFloat("_LensIndex", Mathf.Max(refractiveIndex, 1.01f));
        hybridShader.SetFloat("_LensFlange", lensFlangeWidthMM);
        hybridShader.SetFloat("_UseRealLens", useRealLens ? 1.0f : 0.0f);
        hybridShader.SetFloat("_SensorOpacity", sensorOpacity);
        hybridShader.SetInt("_SensorSamples", sensorSamples);
        hybridShader.SetFloat("_SensorBlend", 1.0f / _sensorSample);
        hybridShader.SetFloat("_ShowSensorImage", showSensorImage ? 1.0f : 0.0f);
        hybridShader.SetFloat("_SensorExposure", sensorExposure);
        hybridShader.SetFloat("_ShowOptics", showOptics ? 1.0f : 0.0f);
        hybridShader.SetFloat("_EnableLensVisuals", enableLensVisuals ? 1.0f : 0.0f);

        hybridShader.SetFloat("_ShowSensorHousing", showSensorHousing ? 1.0f : 0.0f);
        hybridShader.SetFloat("_ShowLensFlange", showLensFlange ? 1.0f : 0.0f);
        hybridShader.SetFloat("_ShowRoughPatch", showRoughPatch ? 1.0f : 0.0f);

        hybridShader.SetVector("_PatchCenter", patchCenter);
        hybridShader.SetFloat("_PatchHalfSize", Mathf.Max(patchSize, 0.001f) * 0.5f);
        hybridShader.SetFloat("_PatchAmplitude", Mathf.Max(patchAmplitude, 0.0f));
        hybridShader.SetFloat("_PatchNoiseFrequency", patchNoiseFrequency);
        hybridShader.SetVector("_PatchLowColor", ColorToVector(patchLowColor));
        hybridShader.SetVector("_PatchHighColor", ColorToVector(patchHighColor));

        hybridShader.SetTexture(kernelSensor, "SensorImage", _sensorImage);
        hybridShader.Dispatch(kernelSensor, Mathf.CeilToInt(_sensorImage.width / 8f), Mathf.CeilToInt(_sensorImage.height / 8f), 1);

        hybridShader.SetTexture(kernelMain, "SensorImage", _sensorImage);
        hybridShader.SetTexture(kernelMain, "AccumulationBuffer", _accumulationBuffer);
        hybridShader.SetTexture(kernelMain, "Result", _targetBuffer);

        hybridShader.SetMatrix("_CameraToWorld", _camera.cameraToWorldMatrix);
        hybridShader.SetMatrix("_CameraInverseProjection", _camera.projectionMatrix.inverse);

        int threadGroupsX = Mathf.CeilToInt(width / 32f);
        int threadGroupsY = Mathf.CeilToInt(height / 32f);
        hybridShader.Dispatch(kernelMain, threadGroupsX, threadGroupsY, 1);

        Graphics.Blit(_targetBuffer, destination);
    }

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
            _SensorImageCreateSafe();
        }
    }

    private void _SensorImageCreateSafe()
    {
        if (_sensorImage != null)
        {
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