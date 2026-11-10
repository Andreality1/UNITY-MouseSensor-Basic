using UnityEngine;

public class Camera_SimulationSensor_0_10 : MonoBehaviour
{
    [Header("Movement Settings")]
    [SerializeField] private float moveSpeed = 10f;
    [SerializeField] private float lookSpeed = 2f;

    private float _rotationX = 0f;
    private float _rotationY = 0f;

    private void Start()
    {
        // Initialize internal rotations based on current camera starting angle
        Vector3 euler = transform.localRotation.eulerAngles;
        _rotationX = euler.y;
        _rotationY = -euler.x;
    }

    private void Update()
    {
        // --- 1. Rotation (Only when holding Right Click) ---
        if (Input.GetMouseButton(1))
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;

            _rotationX += Input.GetAxis("Mouse X") * lookSpeed;
            _rotationY += Input.GetAxis("Mouse Y") * lookSpeed;
            _rotationY = Mathf.Clamp(_rotationY, -90f, 90f);

            transform.localRotation = Quaternion.Euler(-_rotationY, _rotationX, 0f);
        }
        else
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        // --- 2. Translation (Keyboard inputs) ---
        Vector3 moveDirection = Vector3.zero;

        if (Input.GetKey(KeyCode.W)) moveDirection += transform.forward;
        if (Input.GetKey(KeyCode.S)) moveDirection -= transform.forward;
        if (Input.GetKey(KeyCode.D)) moveDirection += transform.right;
        if (Input.GetKey(KeyCode.A)) moveDirection -= transform.right;
        
        // Vertical actions
        if (Input.GetKey(KeyCode.E)) moveDirection += Vector3.up;
        if (Input.GetKey(KeyCode.Q)) moveDirection += Vector3.down;

        // Apply spatial movement over frame ticks
        if (moveDirection != Vector3.zero)
        {
            transform.position += moveDirection.normalized * (moveSpeed * Time.deltaTime);
        }
    }
}