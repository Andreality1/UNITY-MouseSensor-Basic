using UnityEngine;

[RequireComponent(typeof(Camera))]
public class Main_SimulationSensor_4_0 : MonoBehaviour
{
    [Header("Dependencies")]
    [SerializeField] private ComputeShader hybridShader;
    
    [Header("Light Source Settings")]
    [SerializeField] private Vector3 lightPosition = new Vector3(0.5f, 2.0f, 3.8f);
    [SerializeField] private float lightIntensity = 150000.0f;

    [Header("Sensor Plane (3 x 3 units)")]
    [SerializeField] private Vector3 sensorCenter = new Vector3(0.0f, 0.0f, 1.8f);
    [Tooltip("Total width and height of the sensor plane in world units.")]
    [SerializeField] private Vector2 sensorSize = new Vector2(3.0f, 3.0f);
    [SerializeField] private Color sensorColor = new Color(0.05f, 0.35f, 0.45f, 1.0f);

    [Header("Sensor Grid")]
    [SerializeField] private bool showGrid = true;
    [Tooltip("Number of grid squares along X and Y.")]
    [SerializeField] private Vector2Int gridCells = new Vector2Int(30, 30);
    [Tooltip("Grid line thickness as a fraction of one cell.")]
    [SerializeField, Range(0.0f, 0.5f)] private float gridLineWidth = 0.06f;
    [SerializeField] private Color gridLineColor = new Color(0.45f, 0.80f, 0.90f, 1.0f);
    [Tooltip("Outer border thickness as a fraction of one cell.")]
    [SerializeField, Range(0.0f, 0.5f)] private float borderWidth = 0.12f;
    [SerializeField] private Color borderColor = new Color(0.85f, 0.95f, 1.0f, 1.0f);

    [Header("Rough Surface Patch")]
    [SerializeField] private Vector3 patchCenter = new Vector3(0.0f, -1.0f, 0.0f);
    [Tooltip("Total side length of the square patch in world units.")]
    [SerializeField] private float patchSize = 2.0f;
    [Tooltip("Bump height scale. Max bump height is 2x this value.")]
    [SerializeField] private float patchAmplitude = 0.25f;
    [Tooltip("Base noise frequency. Higher = smaller, denser bumps.")]
    [SerializeField] private float patchNoiseFrequency = 3.0f;
    [SerializeField] private Color patchLowColor = new Color(0.25f, 0.18f, 0.12f, 1.0f);
    [SerializeField] private Color patchHighColor = new Color(0.85f, 0.75f, 0.6f, 1.0f);

    [Header("Optical Mouse Lens")]
    [SerializeField] private bool lensEnabled = true;
    [Tooltip("Center of the lens (middle of its thickness). The optical axis runs along world Z, parallel to the sensor normal.")]
    [SerializeField] private Vector3 lensCenter = new Vector3(0.0f, 0.0f, -0.15f);
    [Tooltip("Clear aperture radius of the lens in world units.")]
    [SerializeField] private float lensApertureRadius = 0.5f;
    [Tooltip("Radius of curvature of the camera-facing (object-side) surface. Must be >= aperture radius.")]
    [SerializeField] private float lensFrontRadius = 0.9f;
    [Tooltip("Radius of curvature of the sensor-facing (image-side) surface. Must be >= aperture radius.")]
    [SerializeField] private float lensBackRadius = 0.9f;
    [Tooltip("Refractive index at 589 nm. 1.49 = PMMA (acrylic), 1.58 = polycarbonate, the plastics used in mouse lenses.")]
    [SerializeField] private float lensIOR = 1.49f;
    [Tooltip("Cauchy dispersion coefficient B in um^2. 0.0048 = PMMA. Set to 0 to disable chromatic dispersion.")]
    [SerializeField] private float lensDispersion = 0.0048f;

    [Header("Performance Optimization")]
    [Tooltip("Dynamic ceiling boundary passed as a uniform to break compiler evaluation bottlenecks and ensure instant loading.")]
    [SerializeField] private float intensityClamp = 5000.0f;

    private Camera _camera;
    private RenderTexture _targetBuffer;
    private RenderTexture _accumulationBuffer;
    private int _currentSample = 0;

    private void Start()
    {
        _camera = GetComponent<Camera>();
    }

    private void OnValidate()
    {
        _currentSample = 0;
    }
    
    private void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        int width = source.width;
        int height = source.height;
        
        EnsureBuffersCreated(width, height);

        if (transform.hasChanged)
        {
            _currentSample = 0;
            transform.hasChanged = false;
        }

        _currentSample++;

        int kernelMain = hybridShader.FindKernel("CSMain");

        // --- Uniform Bindings ---
        hybridShader.SetFloat("_Time", Time.time);
        hybridShader.SetInt("_SampleIndex", _currentSample);
        hybridShader.SetVector("_LightPosition", new Vector4(lightPosition.x, lightPosition.y, lightPosition.z, 1.0f));
        hybridShader.SetFloat("_LightIntensity", lightIntensity);
        hybridShader.SetFloat("_IntensityClamp", intensityClamp);

        // --- Plane Parameters ---
        hybridShader.SetVector("_SensorCenter", sensorCenter);
        hybridShader.SetVector("_SensorHalfSize", new Vector4(Mathf.Max(sensorSize.x, 0.001f) * 0.5f, Mathf.Max(sensorSize.y, 0.001f) * 0.5f, 0, 0));
        hybridShader.SetVector("_SensorGridCells", new Vector4(Mathf.Max(gridCells.x, 1), Mathf.Max(gridCells.y, 1), 0, 0));
        hybridShader.SetFloat("_SensorGridEnabled", showGrid ? 1.0f : 0.0f);
        hybridShader.SetFloat("_SensorLineWidth", gridLineWidth);
        hybridShader.SetFloat("_SensorBorderWidth", borderWidth);
        hybridShader.SetVector("_SensorColor", ColorToVector(sensorColor));
        hybridShader.SetVector("_SensorGridColor", ColorToVector(gridLineColor));
        hybridShader.SetVector("_SensorBorderColor", ColorToVector(borderColor));

        hybridShader.SetVector("_PatchCenter", patchCenter);
        hybridShader.SetFloat("_PatchHalfSize", Mathf.Max(patchSize, 0.001f) * 0.5f);
        hybridShader.SetFloat("_PatchAmplitude", Mathf.Max(patchAmplitude, 0.0f));
        hybridShader.SetFloat("_PatchNoiseFrequency", patchNoiseFrequency);
        hybridShader.SetVector("_PatchLowColor", ColorToVector(patchLowColor));
        hybridShader.SetVector("_PatchHighColor", ColorToVector(patchHighColor));
        
        // --- Optical Mouse Lens (two spherical caps meeting at the aperture edge) ---
        float apertureR = Mathf.Max(lensApertureRadius, 0.001f);
        float frontR = Mathf.Max(lensFrontRadius, apertureR * 1.001f);
        float backR = Mathf.Max(lensBackRadius, apertureR * 1.001f);
        float frontSag = frontR - Mathf.Sqrt(frontR * frontR - apertureR * apertureR);
        float backSag = backR - Mathf.Sqrt(backR * backR - apertureR * apertureR);

        // Front apex sits frontSag before the center plane, back apex backSag after it.
        Vector3 frontSphereCenter = lensCenter + new Vector3(0.0f, 0.0f, frontR - frontSag);
        Vector3 backSphereCenter = lensCenter + new Vector3(0.0f, 0.0f, backSag - backR);

        hybridShader.SetVector("_LensFrontSphere", new Vector4(frontSphereCenter.x, frontSphereCenter.y, frontSphereCenter.z, frontR));
        hybridShader.SetVector("_LensBackSphere", new Vector4(backSphereCenter.x, backSphereCenter.y, backSphereCenter.z, backR));
        hybridShader.SetFloat("_LensIOR", Mathf.Max(lensIOR, 1.0f));
        hybridShader.SetFloat("_LensDispersion", lensDispersion);
        hybridShader.SetFloat("_LensEnabled", lensEnabled ? 1.0f : 0.0f);

        // --- Compute Pass: Screen Integration & Spectral Path Tracing ---
        // BackgroundSource binding removed completely
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
    }
}