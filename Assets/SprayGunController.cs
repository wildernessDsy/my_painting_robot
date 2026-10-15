using UnityEngine;

public class SprayGunController : MonoBehaviour
{
    [Header("喷头参数")]
    public float rpm = 0f; // 转速

    // Transform.RotateAround 的角度单位是「度」，而 rpm 是「转/分钟」，
    // 所以需要换算：1 rpm = 360 度 / 60 秒 = 6 度/秒
    private const float DegreesPerSecondPerRpm = 360f / 60f;

    private MeshFilter meshFilter;
    private bool pivotReady;
    private Vector3 pivot;   // 喷头自身中心（世界坐标）
    private Vector3 axis;    // 旋转轴（世界坐标）

    void Update()
    {
        // 如果有转速，就让喷头绕自身中心自转
        if (rpm > 0)
        {
            PreparePivot();
            transform.RotateAround(pivot, axis, rpm * DegreesPerSecondPerRpm * Time.deltaTime);
        }
    }

    // 模型原点并不在喷头中心，必须先算出喷头自身中心作为轴心，
    // 否则会绕着模型原点（约 1600 单位外）打转，而不是原地自转。
    void PreparePivot()
    {
        if (pivotReady) return;

        if (meshFilter == null) meshFilter = GetComponent<MeshFilter>();

        pivot = meshFilter != null && meshFilter.sharedMesh != null
            ? transform.TransformPoint(meshFilter.sharedMesh.bounds.center)
            : transform.position;

        // 轴心正好落在旋转轴上，所以自转时喷头中心不会漂移
        axis = transform.up;
        pivotReady = true;
    }
}
