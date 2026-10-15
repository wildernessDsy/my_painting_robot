using UnityEngine;
using TMPro;
using UnityEngine.UI;

public class UI_Controller : MonoBehaviour
{
    public TextMeshProUGUI rpmText;
    public Slider rpmSlider;

    private SprayGunController currentGun;
    private float shownRpm = float.NaN; // float.NaN 与任何值都不相等，保证第一帧一定会刷新

    void Awake()
    {
        // 面板一开始是隐藏的，Start() 要等到第一次显示才会执行，
        // 所以放在 Awake 里绑定，保证 ClickManager 调用 Setup() 之前回调就已经注册好
        if (rpmSlider != null)
        {
            rpmSlider.onValueChanged.RemoveListener(OnSliderChanged);
            rpmSlider.onValueChanged.AddListener(OnSliderChanged);
        }
    }

    void Update()
    {
        if (currentGun == null) return;

        // 每帧把喷头真实的 rpm 反映到仪表盘上，这样不论转速是谁改的
        // （拖动滑块、脚本、Inspector）显示都会立刻跟着变
        if (rpmSlider != null && !Mathf.Approximately(rpmSlider.value, currentGun.rpm))
            rpmSlider.value = currentGun.rpm;

        UpdateText(currentGun.rpm);
    }

    public void Setup(SprayGunController gun)
    {
        currentGun = gun;
        if (currentGun == null) return;

        // 初始同步
        if (rpmSlider != null) rpmSlider.value = currentGun.rpm;
        UpdateText(currentGun.rpm);
    }

    void OnSliderChanged(float value)
    {
        if (currentGun != null)
        {
            currentGun.rpm = value;
            UpdateText(value);
        }
    }

    void UpdateText(float value)
    {
        if (rpmText == null) return;

        float rpm = Mathf.Round(value);
        if (rpm == shownRpm) return; // 值没变就不重建字符串，避免每帧产生垃圾
        shownRpm = rpm;

        // 工程里只有 LiberationSans（无中日韩字形），所以仪表盘先用英文标签
        rpmText.text = "RPM: " + rpm.ToString("F0");
    }
}