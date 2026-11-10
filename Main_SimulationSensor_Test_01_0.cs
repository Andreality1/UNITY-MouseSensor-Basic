
using UnityEngine;

[RequireComponent(typeof(Camera))]
public class Main_SimulationSensor_Test_01_0 : MonoBehaviour
{
    public enum MouseMotion { Static, Linear, Circular }

    [Header("Dependencies")]
    [SerializeField] private ComputeShader hybridShader;

    [Header("Light Source Settings")]
    [SerializeField] private Vector3 lightPosition = new Vector3(0.5f, 2.0f, 3.8f);
    [SerializeField] private float lightIntensity = 150000.0f;
    [SerializeField] private float intensityClamp = 5000.0f;

    [Header("Mouse Body (1 unit = 1 mm)")]
    [SerializeField, Min(0.1f)] private float mouseHeightMM = 3.50f;

    [Header("Sensor Housing & Walled Pinhole Aperture")]
    [SerializeField, Min(0.01f)] private float housingApertureRadius = 0.35f;
    [SerializeField, Min(0.01f)] private float housingWallThickness = 0.15f;

    [Header("Mouse Motion (mm, on the surface)")]
    [SerializeField] private MouseMotion motion = MouseMotion.Static;
    [SerializeField] private Vector2 mouseStartXZ = new Vector2(0.0f, 0.0f);
    [SerializeField] private Vector2 motionAmplitude = new Vector2(1.0f, 0.0f);
    [SerializeField] private float motionFrequencyHz = 0.25f;

    [Header("Sensor Package & Silicon Die")]
    [SerializeField] private Vector2 sensorSize = new Vector2(2.40f, 2.40f);
    [SerializeField] private Color sensorColor = new Color(0.05f, 0.35f, 0.45f, 1.0f);

    private Camera _camera;
    private RenderTexture _targetBuffer;
    private RenderTexture _accumulationBuffer;
    private int _currentSample = 0;
    private Vector2 _lastMousePos;
    private bool _hasLastMousePos = false;

    private void Start()
    {
        _camera = GetComponent<Camera>();
    }

    private void OnValidate()
    {
        _currentSample = 0;
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

    private void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        int width = source.width;
        int height = source.height;

        EnsureBuffersCreated(width, height);

        Vector2 mousePos = ComputeMousePosition(Time.time);
        bool mouseMoved = !_hasLastMousePos || (mousePos - _lastMousePos).sqrMagnitude > 1e-12f;
        _lastMousePos = mousePos;
        _hasLastMousePos = true;

        if (mouseMoved)
        {
            _currentSample = 0;
        }

        if (transform.hasChanged)
        {
            _currentSample = 0;
            transform.hasChanged = false;
        }

        _currentSample++;

        float sensorY = -1.5f + mouseHeightMM;

        int kernelMain = hybridShader.FindKernel("CSMain");

        // --- Uniform Bindings ---
        hybridShader.SetFloat("_Time", Time.time);
        hybridShader.SetInt("_SampleIndex", _currentSample);
        hybridShader.SetVector("_LightPosition", new Vector4(lightPosition.x, lightPosition.y, lightPosition.z, 1.0f));
        hybridShader.SetFloat("_LightIntensity", lightIntensity);
        hybridShader.SetFloat("_IntensityClamp", intensityClamp);

        hybridShader.SetVector("_SensorCenter", new Vector4(mousePos.x, sensorY, mousePos.y, 0.0f));
        hybridShader.SetVector("_SensorHalfSize", new Vector4(Mathf.Max(sensorSize.x, 0.001f) * 0.5f, Mathf.Max(sensorSize.y, 0.001f) * 0.5f, 0, 0));
        hybridShader.SetVector("_SensorColor", ColorToVector(sensorColor));
        hybridShader.SetFloat("_HousingApertureRadius", Mathf.Max(housingApertureRadius, 0.01f));
        hybridShader.SetFloat("_HousingWallThickness", Mathf.Max(housingWallThickness, 0.01f));

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