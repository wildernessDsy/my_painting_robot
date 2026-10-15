using UnityEngine;
using Unity.Cinemachine;
using UnityEngine.EventSystems;

// 演示时的自由视角：按 F 进入/退出。
//   平移：↑↓ 前后、←→ 左右、W/S 升降（按住 Shift 加速）
//   水平旋转：Z/X，或按住鼠标右键用鼠标转
//   缩放：鼠标滚轮（改相机 FOV）
// 实现方式是临时接管相机 —— 把 CinemachineBrain 停掉后直接驱动相机，
// 所以不需要额外的虚拟相机，退出时把 Brain 开回来即可回到演示机位。
//
// 另外支持「让位」：进入近景/细节机位时（近景优先级更高），会 Suspend 把相机交还
// Cinemachine，但保留此刻的机位、朝向和视野；退出近景时 Resume 原地回来。
public class FreeLookCamera : MonoBehaviour
{
    [Header("开关")]
    public KeyCode toggleKey = KeyCode.F;
    public bool showHint = true;

    [Header("平移（↑↓ 前后，←→ 左右，W/S 升降）")]
    public KeyCode moveForwardKey = KeyCode.UpArrow;
    public KeyCode moveBackwardKey = KeyCode.DownArrow;
    public KeyCode strafeLeftKey = KeyCode.LeftArrow;
    public KeyCode strafeRightKey = KeyCode.RightArrow;
    public KeyCode ascendKey = KeyCode.W;
    public KeyCode descendKey = KeyCode.S;
    public float moveSpeed = 2000f;      // 单位/秒
    public float boostMultiplier = 4f;   // 按住 Shift 的倍率

    [Header("水平旋转（Z/X，或按住鼠标右键）")]
    public KeyCode yawLeftKey = KeyCode.Z;
    public KeyCode yawRightKey = KeyCode.X;
    public float yawSpeed = 90f;         // 角速度（度/秒）
    public bool mouseYaw = true;         // 按住鼠标右键用鼠标转
    public float mouseSensitivity = 3f;

    [Header("滚轮缩放（FOV）")]
    public bool scrollZoom = true;
    public float zoomDegreesPerNotch = 5f;  // 滚一格滚轮的 FOV 变化量（度）
    public float zoomLerpSpeed = 12f;       // 平滑过渡速度（0 = 不平滑）
    public float minFov = 12f;
    public float maxFov = 90f;

    private CinemachineBrain brain;
    private bool active;
    private bool suspended;          // 被近景机位临时接管中（仍保留自由视角状态）
    private float savedFov;
    private float savedOrthoSize;
    private float targetFov;
    private Vector3 suspendPosition;
    private Quaternion suspendRotation;
    private float suspendFov;
    private float suspendOrthoSize;
    private float suspendTargetFov;

    // 是否正在接管相机
    public bool IsActive { get { return active && !suspended; } }

    // 是否已让位给近景机位（可以 Resume 原地回来）
    public bool IsSuspended { get { return active && suspended; } }

    void Update()
    {
        if (Input.GetKeyDown(toggleKey))
        {
            if (active) Exit(); else Enter();
        }

        if (!IsActive) return;

        Camera cam = Camera.main;
        if (cam == null) return;

        Transform t = cam.transform;
        float dt = Time.deltaTime;

        // 水平面内的前后左右（用相机朝向的水平投影，俯仰不影响平移方向）
        Vector3 forward = Vector3.ProjectOnPlane(t.forward, Vector3.up);
        if (forward.sqrMagnitude < 1e-6f) forward = Vector3.ProjectOnPlane(t.up, Vector3.up);
        forward.Normalize();

        Vector3 move = Vector3.zero;
        if (Input.GetKey(moveForwardKey)) move += forward;
        if (Input.GetKey(moveBackwardKey)) move -= forward;
        if (Input.GetKey(strafeRightKey)) move += t.right;
        if (Input.GetKey(strafeLeftKey)) move -= t.right;
        if (Input.GetKey(ascendKey)) move += Vector3.up;
        if (Input.GetKey(descendKey)) move -= Vector3.up;

        if (move.sqrMagnitude > 1e-6f)
        {
            bool boost = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            t.position += move.normalized * (moveSpeed * (boost ? boostMultiplier : 1f) * dt);
        }

        // 水平旋转（只绕世界 Y 轴，视角不会歪）
        float yaw = 0f;
        if (Input.GetKey(yawLeftKey)) yaw -= yawSpeed * dt;
        if (Input.GetKey(yawRightKey)) yaw += yawSpeed * dt;
        if (mouseYaw && Input.GetMouseButton(1)) yaw += Input.GetAxis("Mouse X") * mouseSensitivity;

        if (Mathf.Abs(yaw) > 0f) t.Rotate(0f, yaw, 0f, Space.World);

        // 滚轮缩放：透视相机改 FOV，正交相机改 size
        // 鼠标停在墙面数据面板的参数列表上时滚轮归列表使用，避免同时缩放视角
        if (scrollZoom && !FactoryDataWallDisplay.ScrollCapturedByUi)
        {
            float notch = Input.mouseScrollDelta.y;
            if (Mathf.Abs(notch) > 0.0001f)
            {
                if (cam.orthographic)
                {
                    targetFov = Mathf.Clamp(targetFov - notch * zoomDegreesPerNotch * 0.1f,
                        minFov * 0.1f, maxFov * 0.1f);
                }
                else
                {
                    // 向下滚（负值）拉近 → FOV 变小 → 视野放大
                    targetFov = Mathf.Clamp(targetFov - notch * zoomDegreesPerNotch, minFov, maxFov);
                }
            }
        }
    }

    // 缩放平滑放在 LateUpdate，免得被其它逻辑覆盖
    void LateUpdate()
    {
        if (!IsActive) return;

        Camera cam = Camera.main;
        if (cam == null) return;

        if (cam.orthographic)
        {
            if (!Mathf.Approximately(cam.orthographicSize, targetFov))
                cam.orthographicSize = Step(cam.orthographicSize, targetFov);
        }
        else
        {
            if (!Mathf.Approximately(cam.fieldOfView, targetFov))
                cam.fieldOfView = Step(cam.fieldOfView, targetFov);
        }
    }

    private float Step(float current, float target)
    {
        if (zoomLerpSpeed <= 0f) return target;
        return Mathf.Lerp(current, target, 1f - Mathf.Exp(-zoomLerpSpeed * Time.deltaTime));
    }

    public void Enter()
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        brain = cam.GetComponent<CinemachineBrain>();
        if (brain != null) brain.enabled = false;

        // 从当前朝向的水平方向起步，避免带着俯仰导致旋转时视角歪斜
        Vector3 e = cam.transform.eulerAngles;
        cam.transform.rotation = Quaternion.Euler(0f, e.y, 0f);

        savedFov = cam.fieldOfView;
        savedOrthoSize = cam.orthographicSize;
        targetFov = cam.orthographic ? savedOrthoSize : savedFov;

        // 取消 UI 选中，否则方向键会顺带把仪表盘滑块的值改掉
        if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);

        active = true;
    }

    public void Exit()
    {
        active = false;
        suspended = false;

        // 恢复进入自由视角前的视野，免得退出后演示机位还带着缩放
        Camera cam = Camera.main;
        if (cam != null)
        {
            cam.fieldOfView = savedFov;
            cam.orthographicSize = savedOrthoSize;
        }

        if (brain != null)
        {
            brain.enabled = true;
            brain = null;
        }
    }

    // 让位给近景机位：把相机交还 Cinemachine（它会切到优先级最高的机位），
    // 同时记住此刻的机位、朝向和视野，供 Resume 原样恢复。
    public void Suspend()
    {
        if (!active || suspended) return;

        Camera cam = Camera.main;
        if (cam != null)
        {
            suspendPosition = cam.transform.position;
            suspendRotation = cam.transform.rotation;
            suspendFov = cam.fieldOfView;
            suspendOrthoSize = cam.orthographicSize;

            if (brain == null) brain = cam.GetComponent<CinemachineBrain>();
        }
        suspendTargetFov = targetFov;

        suspended = true;

        // 重新打开 Brain 就是一次干净的直接切换，不会从别的机位飞过来
        if (brain != null) brain.enabled = true;
    }

    // 从近景机位返回：重新接管相机，并还原离开前的机位、朝向与视野。
    public void Resume()
    {
        if (!active || !suspended) return;

        suspended = false;
        if (brain != null) brain.enabled = false;

        Camera cam = Camera.main;
        if (cam != null)
        {
            cam.transform.SetPositionAndRotation(suspendPosition, suspendRotation);
            cam.fieldOfView = suspendFov;
            cam.orthographicSize = suspendOrthoSize;
        }
        targetFov = suspendTargetFov;
    }

    void OnDisable()
    {
        if (active) Exit();
    }

    // 提示用的简易文字（用内置 GUI，不依赖 TMP 字体）
    void OnGUI()
    {
        if (!IsActive || !showHint) return;

        GUI.Label(new Rect(20f, 20f, 1200f, 40f),
            "FREE CAMERA   arrows move  |  W / S up-down  |  Z / X or RMB yaw  |  scroll zoom  |  Shift boost  |  F / Esc exit");
    }
}
