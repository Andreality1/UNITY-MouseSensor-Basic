using UnityEngine;

[RequireComponent(typeof(Camera))]
public class Main_SimulationCamera : MonoBehaviour
{
    [Header("Dependencies")]
    [SerializeField] private ComputeShader hybridShader;
    
    [Header("Sensor Configuration")]
    [SerializeField] private Vector3 sensorPosition = new Vector3(8.0f, 0.65f, 4.5f); 
    [SerializeField] private Vector3 sensorRotation = new Vector3(0.0f, 0.0f, 90.0f);  
    [SerializeField] private Vector2 sensorExtents = new Vector2(2.0f, 2.0f);         

    [Header("Pinhole Settings")]
    [Tooltip("Distance in front of the sensor plane along its normal vector")]
    [SerializeField] private float pinholeDistance = 1.0f;

    [Header("Prism Transformation")]
    [SerializeField] private Vector3 prismPosition = new Vector3(1.0f, -0.2f, 4.5f);
    [SerializeField] private Vector3 prismRotation = new Vector3(0.0f, 0.0f, 0.0f);
    [SerializeField] private Vector3 prismScale = new Vector3(1.0f, 1.0f, 1.0f);

    [Header("Light Source Settings")]
    [SerializeField] private Vector3 lightPosition = new Vector3(0.5f, 2.0f, 3.8f);
    [SerializeField] private float lightIntensity = 150000.0f;

    [Header("Performance Optimization")]
    [Tooltip("Dynamic ceiling boundary passed as a uniform to break compiler evaluation bottlenecks and ensure instant loading.")]
    [SerializeField] private float intensityClamp = 5000.0f;

    private Camera _camera;
    private RenderTexture _targetBuffer;
    private RenderTexture _accumulationBuffer;
    private RenderTexture _sensorBuffer;
    private int _currentSample = 0;

    private void Start()
    {
        _camera = GetComponent<Camera>();
        _camera.depthTextureMode = DepthTextureMode.Depth;
    }

    private void OnValidate()
    {
        // Whenever inspector values change, reset convergence data
        _currentSample = 0;
    }

    private void ClearBuffer(RenderTexture buffer)
    {
        RenderTexture activeBackup = RenderTexture.active;
        RenderTexture.active = buffer;
        GL.Clear(true, true, Color.black);
        RenderTexture.active = activeBackup;
    }

    
    private void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        int width = source.width;
        int height = source.height;
        
        EnsureBuffersCreated(width, height);

        // Track frame matrix changes to reset accumulation if the camera moves
        if (transform.hasChanged)
        {
            _currentSample = 0;
            transform.hasChanged = false;
        }

        _currentSample++;

        // --- Calculate Matrix Transforms ---
        Matrix4x4 sensorLocalToWorld = Matrix4x4.TRS(sensorPosition, Quaternion.Euler(sensorRotation), Vector3.one);
        Matrix4x4 sensorWorldToLocal = sensorLocalToWorld.inverse;

        Matrix4x4 prismLocalToWorld = Matrix4x4.TRS(prismPosition, Quaternion.Euler(prismRotation), prismScale);
        Matrix4x4 prismWorldToLocal = prismLocalToWorld.inverse;

        int kernelPinhole = hybridShader.FindKernel("CS_RenderPinholeSensor");
        int kernelMain = hybridShader.FindKernel("CSMain");

        // --- Common Uniform Buffers Bindings ---
        hybridShader.SetFloat("_Time", Time.time);
        hybridShader.SetInt("_SampleIndex", _currentSample);
        hybridShader.SetVector("_LightPosition", new Vector4(lightPosition.x, lightPosition.y, lightPosition.z, 1.0f));
        hybridShader.SetFloat("_LightIntensity", lightIntensity);
        
        // CRITICAL UNIFORM: Passing this dynamically strips the compiler optimizer's ability to lock up threads
        hybridShader.SetFloat("_IntensityClamp", intensityClamp);

        // --- Compute Pass 1: Sensor Simulation Space ---
        hybridShader.SetTexture(kernelPinhole, "SensorBuffer", _sensorBuffer);
        hybridShader.SetMatrix("_SensorLocalToWorld", sensorLocalToWorld);
        hybridShader.SetMatrix("_SensorWorldToLocal", sensorWorldToLocal);
        hybridShader.SetMatrix("_PrismWorldToLocal", prismWorldToLocal);
        hybridShader.SetMatrix("_PrismLocalToWorld", prismLocalToWorld);
        hybridShader.SetFloat("_PinholeDistance", pinholeDistance);
        hybridShader.SetVector("_SensorExtents", sensorExtents);

        int threadGroupsX = Mathf.CeilToInt(width / 32f);
        int threadGroupsY = Mathf.CeilToInt(height / 32f);
        hybridShader.Dispatch(kernelPinhole, threadGroupsX, threadGroupsY, 1);

        // --- Compute Pass 2: Screen Integration Space ---
        hybridShader.SetTexture(kernelMain, "BackgroundSource", source);
        hybridShader.SetTexture(kernelMain, "SensorBuffer", _sensorBuffer);
        hybridShader.SetTexture(kernelMain, "AccumulationBuffer", _accumulationBuffer);
        hybridShader.SetTexture(kernelMain, "Result", _targetBuffer);

        hybridShader.SetMatrix("_CameraToWorld", _camera.cameraToWorldMatrix);
        hybridShader.SetMatrix("_CameraInverseProjection", _camera.projectionMatrix.inverse);
        
        // Standard projection extraction vector setup for LinearEyeDepth math
        Vector4 zBufferParams = Vector4.zero;
        zBufferParams.x = (1f - _camera.farClipPlane / _camera.nearClipPlane) / _camera.farClipPlane;
        zBufferParams.y = (_camera.farClipPlane / _camera.nearClipPlane) / _camera.farClipPlane;
        zBufferParams.z = zBufferParams.x / _camera.farClipPlane;
        zBufferParams.w = zBufferParams.y / _camera.farClipPlane;
        hybridShader.SetVector("_ZBufferParams", zBufferParams);

        hybridShader.SetMatrix("_SensorLocalToWorld", sensorLocalToWorld);
        hybridShader.SetMatrix("_SensorWorldToLocal", sensorWorldToLocal);
        hybridShader.SetMatrix("_PrismWorldToLocal", prismWorldToLocal);
        hybridShader.SetMatrix("_PrismLocalToWorld", prismLocalToWorld);
        hybridShader.SetFloat("_PinholeDistance", pinholeDistance);
        hybridShader.SetVector("_SensorExtents", sensorExtents);

        hybridShader.Dispatch(kernelMain, threadGroupsX, threadGroupsY, 1);

        // Output final tone-mapped result to monitor display pipeline
        Graphics.Blit(_targetBuffer, destination);
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

        if (_sensorBuffer == null || _sensorBuffer.width != width || _sensorBuffer.height != height)
        {
            if (_sensorBuffer != null) _sensorBuffer.Release();
            _sensorBuffer = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
            {
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            _sensorBuffer.Create();
            ClearBuffer(_sensorBuffer);
            _currentSample = 0;
        }
    }

    private void OnDisable()
    {
        if (_targetBuffer != null) _targetBuffer.Release();
        if (_accumulationBuffer != null) _accumulationBuffer.Release();
        if (_sensorBuffer != null) _sensorBuffer.Release();
    }
}