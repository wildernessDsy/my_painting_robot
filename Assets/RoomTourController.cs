using System.Collections;
using UnityEngine;
using Unity.Cinemachine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// 云端看房流程：
//   1. 站在门外，点击门上的 ENTER 提示牌（或门扇本身）→ 门向外旋开
//   2. 镜头飞进房间，停在喷涂机前方（喷涂机约占画面 75% 高）
//   3. 点击喷头旁的提示牌（或喷头本体）→ 切到近景机位 + 仪表盘
//   4. Esc 逐级后退：近景 → 自由视角 → 整体复位，方便反复演示
//
// 两块提示牌是屏幕空间 Canvas 里的普通 UI，位置由代码每帧从「世界锚点」投影得到，
// 大小按距离换算，所以看起来是贴在门/喷头旁边，同时不会踩到世界空间 Canvas 的那些坑。
//
// 机位优先级：只要切到近景/细节机位，就让自由视角「让位」（Suspend），
// 退出近景时再原地 Resume 回到用户刚才的自由视角。
public class RoomTourController : MonoBehaviour
{
    [Header("相机")]
    public CinemachineCamera entranceCamera; // 门外机位
    public CinemachineCamera entryCamera;    // 进房后的机位
    public CinemachineCamera closeUpCamera;  // 仪表盘近景机位

    [Header("门")]
    public Transform doorPivot;
    public Transform doorLeaf;
    public float doorOpenAngle = -95f;     // 负值 = 朝屋外旋开
    public float doorOpenSeconds = 0.9f;

    [Header("提示牌（屏幕空间 UI，位置由代码从世界锚点投影）")]
    public RectTransform doorSignUi;
    public RectTransform gunSignUi;
    public Vector2 doorSignWorldSize = new Vector2(1400f, 600f); // 换算屏幕大小时用的世界尺寸
    public Vector2 gunSignWorldSize = new Vector2(840f, 364f);
    public float doorSignOutward = 80f;     // ENTER 牌离门扇表面的距离
    public float gunSignSideOffset = 1137f; // 喷头牌相对喷头中心的横向偏移
    public float pulseAmplitude = 0.06f;    // 呼吸缩放幅度，用来「诱导」点击
    public float pulseSpeed = 2.4f;

    [Header("提示牌引线（实线，从牌子指向喷头边缘）")]
    public RectTransform gunSignLeader;          // 引线（Image，pivot 在左端，运行时按长度拉伸）
    public float gunSignLeaderThickness = 2.5f;  // 实线粗细（画布像素）
    public Color gunSignLeaderColor = new Color(0.15f, 0.82f, 0.98f, 1f);

    [Header("仪表盘")]
    public GameObject uiPanel;
    public SprayGunController gunController;

    [Header("自由视角（近景机位会让它让位，退出近景后原地恢复）")]
    public FreeLookCamera freeLookCamera;

    [Header("返回键（逐级后退：近景 → 自由视角 → 整体复位）")]
    public KeyCode backKey = KeyCode.Escape;
    public bool showHint = true;

    [Header("相机优先级")]
    public int entrancePriority = 12;
    public int entryPriority = 15;
    public int dashboardPriority = 30;

    const float RayDistance = 50000f;
    static readonly Vector3 OffScreen = new Vector3(-100000f, -100000f, 0f);

    private MeshFilter gunMeshFilter;
    private MeshRenderer gunMeshRenderer;
    private Image gunSignLeaderImage;
    private bool doorOpened;
    private bool inside;
    private bool dashboardOpen;
    private bool entering;

    void Awake()
    {
        if (gunController != null)
        {
            gunMeshFilter = gunController.GetComponent<MeshFilter>();
            gunMeshRenderer = gunController.GetComponent<MeshRenderer>();
        }

        if (gunSignLeader != null)
        {
            gunSignLeaderImage = gunSignLeader.GetComponent<Image>();
            if (gunSignLeaderImage != null)
            {
                gunSignLeaderImage.color = gunSignLeaderColor;
                gunSignLeaderImage.raycastTarget = false;
            }
        }
    }

    void Start()
    {
        if (GetComponent<FactoryDataWallDisplay>() == null)
            gameObject.AddComponent<FactoryDataWallDisplay>();

        if (doorSignUi != null) doorSignUi.gameObject.SetActive(true);
        if (gunSignUi != null) gunSignUi.gameObject.SetActive(false);
        SetLeaderActive(false);
        if (uiPanel != null) uiPanel.SetActive(false);

        if (entryCamera != null) entryCamera.Priority = 0;
        if (closeUpCamera != null) closeUpCamera.Priority = 0;
        if (entranceCamera != null) entranceCamera.Priority = entrancePriority;
    }

    void LateUpdate()
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        if (doorSignUi != null && doorSignUi.gameObject.activeSelf)
            LayoutSign(doorSignUi, DoorSignAnchor(), doorSignWorldSize, cam);

        if (gunSignUi != null && gunSignUi.gameObject.activeSelf)
        {
            LayoutSign(gunSignUi, GunSignAnchor(), gunSignWorldSize, cam);
            LayoutLeader(cam);
        }
        else
        {
            SetLeaderActive(false);
        }
    }

    // 从牌子铺一条实线指向喷头边缘，明确「哪个是 spray gun」
    void LayoutLeader(Camera cam)
    {
        Vector3 signAnchor = GunSignAnchor();
        Vector3 startScreen = cam.WorldToScreenPoint(signAnchor);
        Vector3 endScreen = cam.WorldToScreenPoint(NozzleEdge(signAnchor));

        if (startScreen.z <= 1f || endScreen.z <= 1f || gunSignLeader == null)
        {
            SetLeaderActive(false);
            return;
        }

        Vector2 from = startScreen;
        Vector2 to = endScreen;
        Vector2 delta = to - from;
        float length = delta.magnitude;
        if (length < 1f)
        {
            SetLeaderActive(false);
            return;
        }

        SetLeaderActive(true);

        // pivot 在左端，所以从牌子锚点朝喷头方向长出去，正好指到喷头边缘
        gunSignLeader.pivot = new Vector2(0f, 0.5f);
        gunSignLeader.anchorMin = gunSignLeader.anchorMax = new Vector2(0.5f, 0.5f);
        gunSignLeader.anchoredPosition = Vector2.zero;
        gunSignLeader.sizeDelta = new Vector2(length, Mathf.Max(0.5f, gunSignLeaderThickness));
        gunSignLeader.position = from;
        gunSignLeader.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
    }

    void SetLeaderActive(bool active)
    {
        if (gunSignLeader != null && gunSignLeader.gameObject.activeSelf != active)
            gunSignLeader.gameObject.SetActive(active);
    }

    // 把提示牌摆到世界锚点在屏幕上的位置，并按距离缩放到与「世界尺寸」等价的观感
    void LayoutSign(RectTransform rect, Vector3 worldAnchor, Vector2 worldSize, Camera cam)
    {
        Vector3 sp = cam.WorldToScreenPoint(worldAnchor);

        // 锚点在相机背后或已经出画就挪到屏幕外，避免贴在画面边上乱晃
        if (sp.z <= 1f || sp.x < -Screen.width || sp.x > Screen.width * 2f ||
            sp.y < -Screen.height || sp.y > Screen.height * 2f)
        {
            rect.position = OffScreen;
            return;
        }

        float pixelsPerUnit = Screen.height / (2f * Mathf.Tan(0.5f * cam.fieldOfView * Mathf.Deg2Rad) * sp.z);
        float designWidth = rect.rect.width > 1f ? rect.rect.width : worldSize.x;
        float pulse = 1f + pulseAmplitude * Mathf.Sin(Time.time * pulseSpeed);

        rect.position = new Vector3(sp.x, sp.y, 0f);
        rect.localScale = Vector3.one * (pixelsPerUnit * (worldSize.x / designWidth) * pulse);
    }

    // ENTER 牌贴在门扇正中、朝屋外一面
    Vector3 DoorSignAnchor()
    {
        if (doorLeaf == null) return transform.position;

        Renderer r = doorLeaf.GetComponent<Renderer>();
        Vector3 center = r != null ? r.bounds.center : doorLeaf.position;
        return center + doorLeaf.forward * doorSignOutward;
    }

    // 喷头（mesh-144）旁边：喷涂机的长边是 X 轴，所以取世界 +X；同高度、同纵深
    Vector3 GunSignAnchor()
    {
        return NozzleCenter() + Vector3.right * gunSignSideOffset;
    }

    // 模型原点在喷头外约 1600 单位处，必须用网格包围盒才能拿到喷头本体中心
    Vector3 NozzleCenter()
    {
        if (gunMeshFilter == null && gunController != null) gunMeshFilter = gunController.GetComponent<MeshFilter>();

        return gunMeshFilter != null && gunMeshFilter.sharedMesh != null
            ? gunMeshFilter.transform.TransformPoint(gunMeshFilter.sharedMesh.bounds.center)
            : (gunController != null ? gunController.transform.position : Vector3.zero);
    }

    // 喷头包围盒上离牌子最近的那个点，虚线指到这里（即喷头边缘），不扎进喷头里
    Vector3 NozzleEdge(Vector3 towards)
    {
        if (gunMeshRenderer == null && gunController != null) gunMeshRenderer = gunController.GetComponent<MeshRenderer>();

        return gunMeshRenderer != null
            ? gunMeshRenderer.bounds.ClosestPoint(towards)
            : NozzleCenter();
    }

    void Update()
    {
        // 逐级后退，演示时想重来一遍就一路按到底
        if (Input.GetKeyDown(backKey))
        {
            if (dashboardOpen) { ExitCloseUp(); return; }
            if (freeLookCamera != null && freeLookCamera.IsActive) { freeLookCamera.Exit(); return; }
            ResetTour();
            return;
        }

        if (entering) return;
        if (!Input.GetMouseButtonDown(0)) return;

        // 提示牌是屏幕空间 UI，直接用矩形命中判定，最稳
        if (!inside && !doorOpened && SignClicked(doorSignUi))
        {
            StartCoroutine(EnterRoom());
            return;
        }

        if (inside && !dashboardOpen && SignClicked(gunSignUi))
        {
            OpenDashboard();
            return;
        }

        // 鼠标在其它 UI 上时交给 UI，不要穿透到场景里
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;
        if (Camera.main == null) return;

        RaycastHit hit;
        if (!Physics.Raycast(Camera.main.ScreenPointToRay(Input.mousePosition), out hit, RayDistance)) return;

        // 也允许直接点门扇 / 点喷头本体
        if (!inside && !doorOpened && doorLeaf != null && hit.collider.transform.IsChildOf(doorLeaf))
        {
            StartCoroutine(EnterRoom());
            return;
        }

        if (inside && !dashboardOpen && hit.collider.gameObject.name == "Spray_Gun")
            OpenDashboard();
    }

    static bool SignClicked(RectTransform rect)
    {
        // 屏幕空间 Overlay 画布，相机参数传 null
        return rect != null && rect.gameObject.activeInHierarchy &&
               RectTransformUtility.RectangleContainsScreenPoint(rect, Input.mousePosition, null);
    }

    IEnumerator EnterRoom()
    {
        entering = true;
        doorOpened = true;

        // 后续是「往前推进」的流程，自由视角直接退出（不是让位）
        if (freeLookCamera != null) freeLookCamera.Exit();

        if (doorSignUi != null) doorSignUi.gameObject.SetActive(false);

        yield return OpenDoor();

        // 门开完再切机位，Cinemachine 会沿着门口的直线混合飞入
        if (entryCamera != null) entryCamera.Priority = entryPriority;
        yield return new WaitForSeconds(2.4f);

        inside = true;
        if (gunSignUi != null) gunSignUi.gameObject.SetActive(true);
        entering = false;
    }

    IEnumerator OpenDoor()
    {
        if (doorPivot == null) yield break;

        Quaternion from = doorPivot.localRotation;
        Quaternion to = Quaternion.Euler(0f, doorOpenAngle, 0f);

        float t = 0f;
        while (t < doorOpenSeconds)
        {
            t += Time.deltaTime;
            doorPivot.localRotation = Quaternion.Slerp(from, to, Mathf.SmoothStep(0f, 1f, t / doorOpenSeconds));
            yield return null;
        }

        doorPivot.localRotation = to;
    }

    void OpenDashboard()
    {
        dashboardOpen = true;

        // 近景机位优先级更高：自由视角先让位（记下当前机位，退出时原地回来）
        if (freeLookCamera != null && freeLookCamera.IsActive) freeLookCamera.Suspend();

        if (gunSignUi != null) gunSignUi.gameObject.SetActive(false);
        SetLeaderActive(false);
        if (closeUpCamera != null) closeUpCamera.Priority = dashboardPriority;

        if (uiPanel != null)
        {
            uiPanel.SetActive(true);

            UI_Controller ui = uiPanel.GetComponent<UI_Controller>();
            if (ui != null) ui.Setup(gunController);
        }
    }

    // 退出近景视角：关掉仪表盘，并且回到进入近景之前的地方
    //   - 若近景是从自由视角点进来的 → 原地恢复那个自由视角（机位/朝向/视野都不变）
    //   - 否则 → 回到房内机位
    public void ExitCloseUp()
    {
        if (!dashboardOpen) return;

        dashboardOpen = false;

        if (uiPanel != null) uiPanel.SetActive(false);
        if (closeUpCamera != null) closeUpCamera.Priority = 0;

        // 让位中的自由视角原地回来（没让位时是个空操作）
        if (freeLookCamera != null) freeLookCamera.Resume();

        // 引导牌重新出现，方便再次点进近景
        if (inside && gunSignUi != null) gunSignUi.gameObject.SetActive(true);
    }

    public void ResetTour()
    {
        StopAllCoroutines();
        entering = false;
        inside = false;
        doorOpened = false;
        dashboardOpen = false;

        // 自由视角会接管相机，复位时要把它交还给 Cinemachine
        if (freeLookCamera != null) freeLookCamera.Exit();

        if (doorPivot != null) doorPivot.localRotation = Quaternion.identity;
        if (uiPanel != null) uiPanel.SetActive(false);
        if (gunSignUi != null) gunSignUi.gameObject.SetActive(false);
        SetLeaderActive(false);
        if (doorSignUi != null) doorSignUi.gameObject.SetActive(true);

        if (entryCamera != null) entryCamera.Priority = 0;
        if (closeUpCamera != null) closeUpCamera.Priority = 0;
        if (entranceCamera != null) entranceCamera.Priority = entrancePriority;
    }

    // 演示提示：告诉操作者 Esc 现在会退到哪一层
    void OnGUI()
    {
        if (!showHint) return;

        // 自由视角自己会显示按键提示，这里让开避免两行字叠在一起
        if (freeLookCamera != null && freeLookCamera.IsActive) return;

        GUI.Label(new Rect(20f, 20f, 800f, 40f),
            dashboardOpen ? "ESC  退出近景视角" : "ESC  复位重新演示");
    }
}
