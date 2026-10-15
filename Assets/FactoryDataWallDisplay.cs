using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

public class FactoryDataWallDisplay : MonoBehaviour
{
    private const string DataFolderName = "FactoryData";

    // WebGL 下没有文件系统，无法像桌面端那样列目录，所以文件名在这里显式列出。
    // 顺序就是时间顺序（文件名升序），与原 Directory.GetFiles + Array.Sort 的结果一致。
    private static readonly string[] DataFiles =
    {
        "09_Oct_2026H15.csv", "09_Oct_2026H16.csv", "09_Oct_2026H17.csv", "09_Oct_2026H18.csv",
        "09_Oct_2026H19.csv", "09_Oct_2026H20.csv", "09_Oct_2026H21.csv", "09_Oct_2026H22.csv",
        "09_Oct_2026H23.csv", "10_Oct_2026H00.csv", "10_Oct_2026H01.csv", "10_Oct_2026H02.csv",
        "10_Oct_2026H03.csv", "10_Oct_2026H04.csv", "10_Oct_2026H05.csv", "10_Oct_2026H06.csv",
        "10_Oct_2026H07.csv", "10_Oct_2026H08.csv", "10_Oct_2026H09.csv", "10_Oct_2026H10.csv",
        "10_Oct_2026H11.csv", "10_Oct_2026H12.csv", "10_Oct_2026H13.csv", "10_Oct_2026H14.csv",
        "10_Oct_2026H15.csv"
    };

    private const int MaxChartPoints = 900;
    // 贴图高度固定，宽度按图表区实际长宽比推算，保证线宽不被拉伸
    private const int ChartTextureHeight = 1410;
    private const float CurveThickness = 5f;
    private const float GridThickness = 2f;

    // 进门左手墙净长 11795（Z 方向）。单块大屏铺满整面墙，宽度只留很窄的边距，
    // 参数列表占左侧一列（可上下滚动），图表占右侧其余全部宽度。
    private const float BoardWidth = 11700f;
    private const float BoardHeight = 9800f;
    private const float BoardMargin = 45f;
    private const float WindowWidth = BoardWidth - 2f * BoardMargin;
    private const float WindowHeight = 9000f;
    private const float WindowLeft = BoardMargin;
    private const float ListLeft = 40f;
    private const float ListWidth = 1700f;
    private const float ListTop = 370f;
    private const float ListPlotGap = 90f;
    private const float PlotRightPadding = 70f;
    private const float RowHeight = 610f;
    private const float ParameterFontSize = 200f;
    private const float ScrollWheelStep = RowHeight;
    private const float ScrollBarWidth = 22f;
    private const float ScrollBarMinHandle = 120f;

    private static readonly Color BoardColor = new Color(0.025f, 0.045f, 0.07f, 0.97f);
    private static readonly Color PanelColor = new Color(0.045f, 0.075f, 0.105f, 1f);
    private static readonly Color AccentColor = new Color(0.18f, 0.86f, 0.98f, 1f);
    private static readonly Color TextColor = new Color(0.86f, 0.93f, 0.97f, 1f);
    private static readonly Color32 GridPixelColor = new Color32(44, 74, 94, 255);
    private static readonly Color32 CurvePixelColor = new Color32(46, 219, 250, 255);

    private readonly List<FactoryParameter> parameters = new List<FactoryParameter>();
    private readonly List<Button> parameterButtons = new List<Button>();

    // 自由视角的滚轮缩放要避让：鼠标停在参数列表上时，滚轮用于滚动列表
    public static bool ScrollCapturedByUi { get; private set; }

    private TextMeshProUGUI statusText;
    private TextMeshProUGUI selectionTitle;
    private TextMeshProUGUI parameterRangeText;
    private TextMeshProUGUI rangeText;
    private TextMeshProUGUI minimumText;
    private TextMeshProUGUI maximumText;
    private TextMeshProUGUI startTimeText;
    private TextMeshProUGUI endTimeText;
    private Image chartSurface;
    private Sprite chartSprite;
    private Texture2D chartTexture;
    private RectTransform boardRect;
    private RectTransform windowRect;
    private RectTransform listViewportRect;
    private RectTransform listContentRect;
    private RectTransform scrollHandleRect;
    private int selectedIndex = -1;
    private float scrollOffset;
    private string firstTimestamp;
    private string lastTimestamp;
    private int sampleCount;

    private void Start()
    {
        CreateBoard();
        StartCoroutine(LoadData());
    }

    private void Update()
    {
        Camera eventCamera = Camera.main;
        if (eventCamera == null) return;

        // 参数字典会随滚动移出可视区，所以先判定鼠标是否在列表窗口内，
        // 再逐个判断到底压在哪一行上，避免命中被裁掉的隐藏行。
        bool overList = listViewportRect != null &&
            RectTransformUtility.RectangleContainsScreenPoint(listViewportRect, Input.mousePosition, eventCamera);
        ScrollCapturedByUi = overList;

        if (overList)
        {
            float notch = Input.mouseScrollDelta.y;
            if (Mathf.Abs(notch) > 0.0001f) ScrollParameterList(-notch * ScrollWheelStep);
        }

        if (!Input.GetMouseButtonDown(0) || !overList) return;

        for (int i = 0; i < parameterButtons.Count; i++)
        {
            RectTransform buttonRect = parameterButtons[i].transform as RectTransform;
            if (buttonRect != null &&
                RectTransformUtility.RectangleContainsScreenPoint(buttonRect, Input.mousePosition, eventCamera))
            {
                SelectParameter(i);
                return;
            }
        }
    }

    private void OnDisable()
    {
        ScrollCapturedByUi = false;
    }

    private void CreateBoard()
    {
        GameObject board = new GameObject("FactoryDataWallDisplay", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
        board.transform.SetPositionAndRotation(new Vector3(6950f, 6500f, 534.25f), Quaternion.Euler(0f, 90f, 0f));

        Canvas canvas = board.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = Camera.main;
        canvas.overrideSorting = true;
        canvas.sortingOrder = 20;
        boardRect = board.GetComponent<RectTransform>();
        boardRect.sizeDelta = new Vector2(BoardWidth, BoardHeight);

        CreateImage("BoardBackground", boardRect, Vector2.zero, new Vector2(BoardWidth, BoardHeight), BoardColor, true);
        CreateText("FACTORY DATA  |  09-10 OCT 2026", boardRect, new Vector2(80f, 35f),
            new Vector2(6500f, 190f), 155f, AccentColor, TextAlignmentOptions.Left);
        statusText = CreateText("Loading CSV data...", boardRect, new Vector2(80f, 210f),
            new Vector2(BoardWidth - 160f, 110f), 82f, TextColor, TextAlignmentOptions.Left);
        CreateImage("HeaderRule", boardRect, new Vector2(70f, 365f),
            new Vector2(BoardWidth - 140f, 12f), AccentColor);

        CreateWindow();
    }

    private void CreateWindow()
    {
        GameObject window = CreateRectObject("TrendWindow", boardRect,
            new Vector2(WindowLeft, 440f), new Vector2(WindowWidth, WindowHeight));
        windowRect = window.GetComponent<RectTransform>();

        Image windowBackground = window.AddComponent<Image>();
        windowBackground.color = PanelColor;
        windowBackground.raycastTarget = false;

        CreateText("PARAMETER TREND", windowRect,
            new Vector2(55f, 35f), new Vector2(WindowWidth - 110f, 150f),
            145f, AccentColor, TextAlignmentOptions.Left);
        CreateImage("WindowHeaderRule", windowRect,
            new Vector2(50f, 205f), new Vector2(WindowWidth - 100f, 8f), new Color(0.16f, 0.36f, 0.43f, 1f));
        parameterRangeText = CreateText("PARAMETERS",
            windowRect, new Vector2(45f, 250f),
            new Vector2(ListWidth, 105f), 82f, TextColor, TextAlignmentOptions.Left);

        CreateParameterListViewport();
        CreateChartArea();
    }

    private void CreateParameterListViewport()
    {
        float viewportHeight = WindowHeight - ListTop - 70f;
        GameObject viewport = CreateRectObject("ParameterListViewport", windowRect,
            new Vector2(ListLeft, ListTop), new Vector2(ListWidth, viewportHeight));
        listViewportRect = viewport.GetComponent<RectTransform>();
        viewport.AddComponent<RectMask2D>();

        GameObject content = CreateRectObject("ParameterListContent", listViewportRect,
            Vector2.zero, new Vector2(ListWidth, viewportHeight));
        listContentRect = content.GetComponent<RectTransform>();

        Image track = CreateImage("ParameterListScrollTrack", listViewportRect,
            new Vector2(ListWidth - ScrollBarWidth, 0f), new Vector2(ScrollBarWidth, viewportHeight),
            new Color(0.07f, 0.12f, 0.16f, 1f));

        Image handle = CreateImage("ParameterListScrollHandle", track.rectTransform,
            Vector2.zero, new Vector2(ScrollBarWidth, ScrollBarMinHandle), new Color(0.22f, 0.55f, 0.65f, 1f));
        scrollHandleRect = handle.rectTransform;
    }

    private void CreateChartArea()
    {
        float plotLeft = ListLeft + ListWidth + ListPlotGap;
        float plotTop = 850f;
        Vector2 plotPosition = new Vector2(plotLeft, plotTop);
        Vector2 plotSize = new Vector2(WindowWidth - plotLeft - PlotRightPadding, 6900f);
        float textWidth = WindowWidth - plotLeft - PlotRightPadding;
        CreateImage("ChartBackground", windowRect, plotPosition, plotSize, BoardColor);

        chartSurface = CreateImage("ChartSurface", windowRect, plotPosition, plotSize, BoardColor);

        selectionTitle = CreateText("Select a parameter", windowRect,
            new Vector2(plotLeft, 265f), new Vector2(textWidth, 115f),
            120f, AccentColor, TextAlignmentOptions.Left);
        rangeText = CreateText("", windowRect,
            new Vector2(plotLeft, 405f), new Vector2(textWidth, 90f),
            74f, TextColor, TextAlignmentOptions.Left);
        maximumText = CreateText("", windowRect,
            new Vector2(plotLeft, 770f), new Vector2(700f, 70f), 88f, TextColor, TextAlignmentOptions.Left);
        minimumText = CreateText("", windowRect,
            new Vector2(plotLeft, 7780f), new Vector2(700f, 70f), 88f, TextColor, TextAlignmentOptions.Left);
        startTimeText = CreateText("", windowRect,
            new Vector2(plotLeft, 7920f), new Vector2(2400f, 75f), 72f, TextColor, TextAlignmentOptions.Left);
        endTimeText = CreateText("", windowRect,
            new Vector2(WindowWidth - 2500f, 7920f), new Vector2(2400f, 75f), 72f, TextColor, TextAlignmentOptions.Right);
    }

    private IEnumerator LoadData()
    {
        string folder = Path.Combine(Application.streamingAssetsPath, DataFolderName);
        if (!Directory.Exists(folder))
        {
            ShowLoadError("CSV folder is missing: " + folder);
            yield break;
        }

        string[] files = null;
        string listingError = null;
        try
        {
            files = Directory.GetFiles(folder, "*.csv");
        }
        catch (IOException ex)
        {
            listingError = "Unable to list CSV files: " + ex.Message;
        }
        catch (UnauthorizedAccessException ex)
        {
            listingError = "Access denied while listing CSV files: " + ex.Message;
        }
        if (listingError != null)
        {
            ShowLoadError(listingError);
            yield break;
        }

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        if (files.Length == 0)
        {
            ShowLoadError("No CSV files found in " + folder);
            yield break;
        }

        string[] headers = null;
        int invalidRows = 0;
        foreach (string file in files)
        {
            statusText.text = "Loading " + Path.GetFileName(file) + "...";
            string readError = null;
            try
            {
                ReadCsvFile(file, ref headers, ref invalidRows);
            }
            catch (IOException ex)
            {
                readError = "Unable to read " + Path.GetFileName(file) + ": " + ex.Message;
            }
            catch (UnauthorizedAccessException ex)
            {
                readError = "Access denied while reading " + Path.GetFileName(file) + ": " + ex.Message;
            }
            catch (FormatException ex)
            {
                readError = "Invalid CSV " + Path.GetFileName(file) + ": " + ex.Message;
            }

            if (readError != null)
            {
                ShowLoadError(readError);
                yield break;
            }
            yield return null;
        }

        if (sampleCount == 0 || parameters.Count == 0)
        {
            ShowLoadError("CSV files contained no usable numeric samples.");
            yield break;
        }

        BuildParameterList();
        statusText.text = string.Format(CultureInfo.InvariantCulture,
            "{0} parameters  |  {1:N0} samples  |  {2} CSV files", parameters.Count, sampleCount, files.Length);
        if (invalidRows > 0)
        {
            Debug.LogWarning("FactoryDataWallDisplay skipped " + invalidRows + " malformed CSV rows.");
        }

        if (parameters.Count > 0) SelectParameter(0);
    }

    private void ReadCsvFile(string file, ref string[] headers, ref int invalidRows)
    {
        using (StreamReader reader = new StreamReader(file))
        {
            string line = reader.ReadLine();
            if (line == null) return;

            string[] fileHeaders = SplitFields(line);
            if (headers == null)
            {
                headers = fileHeaders;
                for (int i = 0; i < headers.Length; i++)
                {
                    string name = headers[i].Trim();
                    if (name.Equals("Date-Time", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Time_s", StringComparison.OrdinalIgnoreCase))
                        continue;

                    parameters.Add(new FactoryParameter(name));
                }
            }
            else if (!HeadersMatch(headers, fileHeaders))
            {
                throw new FormatException(Path.GetFileName(file) + " has a different column header.");
            }

            while ((line = reader.ReadLine()) != null)
            {
                string[] values = SplitFields(line);
                if (values.Length != headers.Length)
                {
                    invalidRows++;
                    continue;
                }

                int parameterIndex = 0;
                for (int i = 0; i < headers.Length; i++)
                {
                    string header = headers[i].Trim();
                    if (header.Equals("Date-Time", StringComparison.OrdinalIgnoreCase))
                    {
                        if (sampleCount == 0) firstTimestamp = values[i];
                        lastTimestamp = values[i];
                        continue;
                    }

                    if (header.Equals("Time_s", StringComparison.OrdinalIgnoreCase))
                        continue;

                    float value;
                    bool valid = float.TryParse(values[i], NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                                 !float.IsNaN(value) && !float.IsInfinity(value);
                    parameters[parameterIndex].Values.Add(valid ? value : float.NaN);
                    parameterIndex++;
                }

                sampleCount++;
            }
        }
    }

    private static string[] SplitFields(string line)
    {
        string[] fields = line.Split(';');
        for (int i = 0; i < fields.Length; i++)
        {
            fields[i] = fields[i].Trim();
            if (i == 0) fields[i] = fields[i].TrimStart('\uFEFF');
        }
        return fields;
    }

    private static bool HeadersMatch(string[] expected, string[] actual)
    {
        if (expected.Length != actual.Length) return false;
        for (int i = 0; i < expected.Length; i++)
        {
            if (!string.Equals(expected[i].Trim(), actual[i].Trim(), StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private void BuildParameterList()
    {
        parameterRangeText.text = string.Format(CultureInfo.InvariantCulture,
            "PARAMETERS  1-{0} / {0}", parameters.Count);

        for (int parameterIndex = 0; parameterIndex < parameters.Count; parameterIndex++)
        {
            parameterButtons.Add(CreateParameterButton(parameters[parameterIndex].DisplayName,
                parameterIndex * RowHeight, parameterIndex));
        }

        listContentRect.sizeDelta = new Vector2(ListWidth, parameters.Count * RowHeight);
        scrollOffset = 0f;
        ApplyScroll();
    }

    private Button CreateParameterButton(string label, float top, int parameterIndex)
    {
        float buttonWidth = ListWidth - ScrollBarWidth - 60f;
        GameObject buttonObject = CreateRectObject("Parameter_" + parameterIndex,
            listContentRect, new Vector2(0f, top), new Vector2(buttonWidth, RowHeight - 30f));

        Image image = buttonObject.AddComponent<Image>();
        image.color = new Color(0.07f, 0.12f, 0.16f, 1f);

        Button button = buttonObject.AddComponent<Button>();
        button.targetGraphic = image;
        button.transition = Selectable.Transition.None;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        button.onClick.AddListener(delegate { SelectParameter(parameterIndex); });

        TextMeshProUGUI labelText = CreateText(label, buttonObject.GetComponent<RectTransform>(),
            new Vector2(40f, 26f), new Vector2(buttonWidth - 80f, RowHeight - 70f),
            ParameterFontSize, TextColor, TextAlignmentOptions.Left);
        labelText.raycastTarget = false;
        return button;
    }

    private void ScrollParameterList(float delta)
    {
        float maxScroll = GetMaxScroll();
        float next = Mathf.Clamp(scrollOffset + delta, 0f, maxScroll);
        if (Mathf.Approximately(next, scrollOffset)) return;
        scrollOffset = next;
        ApplyScroll();
    }

    private float GetMaxScroll()
    {
        if (listViewportRect == null || listContentRect == null) return 0f;
        return Mathf.Max(0f, listContentRect.sizeDelta.y - listViewportRect.rect.height);
    }

    private void ApplyScroll()
    {
        if (listContentRect == null) return;
        listContentRect.anchoredPosition = new Vector2(0f, scrollOffset);

        if (scrollHandleRect == null || listViewportRect == null) return;

        float viewportHeight = listViewportRect.rect.height;
        float contentHeight = Mathf.Max(viewportHeight, listContentRect.sizeDelta.y);
        float handleHeight = Mathf.Max(ScrollBarMinHandle, viewportHeight * viewportHeight / contentHeight);
        float travel = Mathf.Max(0f, viewportHeight - handleHeight);
        float maxScroll = GetMaxScroll();
        float fraction = maxScroll <= 0f ? 0f : scrollOffset / maxScroll;

        scrollHandleRect.sizeDelta = new Vector2(scrollHandleRect.sizeDelta.x, handleHeight);
        scrollHandleRect.anchoredPosition = new Vector2(0f, -fraction * travel);
    }

    private void SelectParameter(int parameterIndex)
    {
        if (parameterIndex < 0 || parameterIndex >= parameters.Count) return;
        if (selectedIndex == parameterIndex) return;
        selectedIndex = parameterIndex;

        for (int i = 0; i < parameterButtons.Count; i++)
        {
            Image image = parameterButtons[i].targetGraphic as Image;
            if (image != null)
                image.color = i == parameterIndex
                    ? new Color(0.1f, 0.33f, 0.39f, 1f)
                    : new Color(0.07f, 0.12f, 0.16f, 1f);
        }

        FactoryParameter parameter = parameters[parameterIndex];
        selectionTitle.text = parameter.DisplayName;
        UpdateChart(parameter);
    }

    private void UpdateChart(FactoryParameter parameter)
    {
        float minimum = float.PositiveInfinity;
        float maximum = float.NegativeInfinity;
        for (int i = 0; i < parameter.Values.Count; i++)
        {
            float value = parameter.Values[i];
            if (float.IsNaN(value) || float.IsInfinity(value)) continue;
            minimum = Mathf.Min(minimum, value);
            maximum = Mathf.Max(maximum, value);
        }

        if (float.IsInfinity(minimum) || float.IsInfinity(maximum))
        {
            ShowLoadError("Selected parameter has no numeric values: " + parameter.Name);
            return;
        }

        float range = maximum - minimum;
        if (range <= Mathf.Epsilon)
        {
            float padding = Mathf.Max(Mathf.Abs(maximum) * 0.05f, 0.5f);
            minimum -= padding;
            maximum += padding;
            range = maximum - minimum;
        }
        else
        {
            float padding = range * 0.04f;
            minimum -= padding;
            maximum += padding;
            range = maximum - minimum;
        }

        List<Vector2> points = BuildMinMaxSeries(parameter.Values, minimum, range);
        ApplyChartTexture(points);
        minimumText.text = minimum.ToString("0.###", CultureInfo.InvariantCulture);
        maximumText.text = maximum.ToString("0.###", CultureInfo.InvariantCulture);
        rangeText.text = string.Format(CultureInfo.InvariantCulture,
            "Absolute values: {0:0.###} to {1:0.###}",
            GetFiniteMinimum(parameter.Values), GetFiniteMaximum(parameter.Values));
        startTimeText.text = FormatTimestamp(firstTimestamp);
        endTimeText.text = FormatTimestamp(lastTimestamp);
    }

    private static List<Vector2> BuildMinMaxSeries(List<float> values, float minimum, float range)
    {
        List<Vector2> points = new List<Vector2>(MaxChartPoints * 2);
        if (values.Count == 0) return points;

        int bucketCount = Mathf.Min(MaxChartPoints / 2, values.Count);
        for (int bucket = 0; bucket < bucketCount; bucket++)
        {
            int start = bucket * values.Count / bucketCount;
            int end = (bucket + 1) * values.Count / bucketCount;
            int minIndex = -1;
            int maxIndex = -1;
            float localMin = float.PositiveInfinity;
            float localMax = float.NegativeInfinity;

            for (int i = start; i < end; i++)
            {
                float value = values[i];
                if (float.IsNaN(value) || float.IsInfinity(value)) continue;
                if (value < localMin) { localMin = value; minIndex = i; }
                if (value > localMax) { localMax = value; maxIndex = i; }
            }

            if (minIndex < 0) continue;
            if (minIndex <= maxIndex)
            {
                AddPoint(points, minIndex, values[minIndex], values.Count, minimum, range);
                if (maxIndex != minIndex) AddPoint(points, maxIndex, values[maxIndex], values.Count, minimum, range);
            }
            else
            {
                AddPoint(points, maxIndex, values[maxIndex], values.Count, minimum, range);
                AddPoint(points, minIndex, values[minIndex], values.Count, minimum, range);
            }
        }
        return points;
    }

    private static void AddPoint(List<Vector2> points, int index, float value, int count, float minimum, float range)
    {
        float x = count <= 1 ? 0f : (float)index / (count - 1);
        float y = Mathf.Clamp01((value - minimum) / range);
        points.Add(new Vector2(x, y));
    }

    // 网格和曲线都画进一张贴图，再用 Image 显示；Image 是已验证可用的渲染路径。
    private void ApplyChartTexture(List<Vector2> points)
    {
        if (chartSurface == null) return;

        int height = ChartTextureHeight;
        Vector2 plotSize = chartSurface.rectTransform.rect.size;
        float aspect = plotSize.y > 1f ? plotSize.x / plotSize.y : 1f;
        int width = Mathf.Clamp(Mathf.RoundToInt(height * aspect), 64, 2048);
        Color32[] buffer = new Color32[width * height];

        for (int i = 0; i <= 4; i++)
        {
            int y = Mathf.RoundToInt((height - 1) * (i / 4f));
            DrawHorizontalLine(buffer, width, height, y, GridPixelColor, GridThickness);
        }
        for (int i = 0; i <= 4; i++)
        {
            int x = Mathf.RoundToInt((width - 1) * (i / 4f));
            DrawVerticalLine(buffer, width, height, x, GridPixelColor, GridThickness);
        }

        for (int i = 1; i < points.Count; i++)
        {
            Vector2 from = new Vector2(points[i - 1].x * (width - 1), points[i - 1].y * (height - 1));
            Vector2 to = new Vector2(points[i].x * (width - 1), points[i].y * (height - 1));
            DrawThickLine(buffer, width, height, from, to, CurveThickness, CurvePixelColor);
        }

        Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.filterMode = FilterMode.Bilinear;
        texture.SetPixels32(buffer);
        texture.Apply(false, false);

        Sprite previousSprite = chartSprite;
        Texture2D previousTexture = chartTexture;
        chartTexture = texture;
        chartSprite = Sprite.Create(texture, new Rect(0f, 0f, width, height), new Vector2(0.5f, 0.5f));
        chartSurface.sprite = chartSprite;
        chartSurface.color = Color.white;

        if (previousSprite != null) Destroy(previousSprite);
        if (previousTexture != null) Destroy(previousTexture);
    }

    private static void DrawHorizontalLine(Color32[] buffer, int width, int height, int y, Color32 color, float thickness)
    {
        int half = Mathf.Max(0, Mathf.RoundToInt(thickness * 0.5f));
        for (int offset = -half; offset <= half; offset++)
        {
            int row = y + offset;
            if (row < 0 || row >= height) continue;
            int start = row * width;
            for (int x = 0; x < width; x++) buffer[start + x] = color;
        }
    }

    private static void DrawVerticalLine(Color32[] buffer, int width, int height, int x, Color32 color, float thickness)
    {
        int half = Mathf.Max(0, Mathf.RoundToInt(thickness * 0.5f));
        for (int offset = -half; offset <= half; offset++)
        {
            int column = x + offset;
            if (column < 0 || column >= width) continue;
            for (int y = 0; y < height; y++) buffer[y * width + column] = color;
        }
    }

    private static void DrawThickLine(Color32[] buffer, int width, int height, Vector2 from, Vector2 to,
        float thickness, Color32 color)
    {
        float half = thickness * 0.5f;
        int minX = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(from.x, to.x) - half));
        int maxX = Mathf.Min(width - 1, Mathf.CeilToInt(Mathf.Max(from.x, to.x) + half));
        int minY = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(from.y, to.y) - half));
        int maxY = Mathf.Min(height - 1, Mathf.CeilToInt(Mathf.Max(from.y, to.y) + half));

        Vector2 delta = to - from;
        float lengthSquared = delta.sqrMagnitude;
        float halfSquared = half * half;

        for (int y = minY; y <= maxY; y++)
        {
            int row = y * width;
            for (int x = minX; x <= maxX; x++)
            {
                Vector2 center = new Vector2(x + 0.5f, y + 0.5f);
                float t = lengthSquared <= Mathf.Epsilon
                    ? 0f
                    : Mathf.Clamp01(Vector2.Dot(center - from, delta) / lengthSquared);
                if ((from + delta * t - center).sqrMagnitude <= halfSquared)
                    buffer[row + x] = color;
            }
        }
    }

    private static float GetFiniteMinimum(List<float> values)
    {
        float result = float.PositiveInfinity;
        for (int i = 0; i < values.Count; i++)
            if (!float.IsNaN(values[i]) && !float.IsInfinity(values[i])) result = Mathf.Min(result, values[i]);
        return result;
    }

    private static float GetFiniteMaximum(List<float> values)
    {
        float result = float.NegativeInfinity;
        for (int i = 0; i < values.Count; i++)
            if (!float.IsNaN(values[i]) && !float.IsInfinity(values[i])) result = Mathf.Max(result, values[i]);
        return result;
    }

    private static string FormatTimestamp(string value)
    {
        DateTime parsed;
        if (DateTime.TryParseExact(value, "dd MM yyyy HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out parsed))
            return parsed.ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture);
        return value;
    }

    private void ShowLoadError(string message)
    {
        Debug.LogError("FactoryDataWallDisplay: " + message);
        if (statusText != null) statusText.text = "DATA ERROR  |  " + message;
    }

    private static GameObject CreateRectObject(string name, RectTransform parent, Vector2 position, Vector2 size)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(position.x, -position.y);
        rect.sizeDelta = size;
        return go;
    }

    private static Image CreateImage(string name, RectTransform parent, Vector2 position, Vector2 size, Color color, bool stretch = false)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        if (stretch)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
        else
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(position.x, -position.y);
            rect.sizeDelta = size;
        }

        Image image = go.GetComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    private static TextMeshProUGUI CreateText(string content, RectTransform parent, Vector2 position,
        Vector2 size, float fontSize, Color color, TextAlignmentOptions alignment)
    {
        GameObject go = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(position.x, -position.y);
        rect.sizeDelta = size;

        TextMeshProUGUI text = go.GetComponent<TextMeshProUGUI>();
        text.text = content;
        text.font = TMP_Settings.defaultFontAsset;
        text.fontSize = fontSize;
        text.color = color;
        text.alignment = alignment;
        text.textWrappingMode = TextWrappingModes.Normal;
        text.raycastTarget = false;
        return text;
    }

    private sealed class FactoryParameter
    {
        public readonly string Name;
        public readonly string DisplayName;
        public readonly List<float> Values = new List<float>();

        public FactoryParameter(string name)
        {
            Name = name;
            DisplayName = name.Replace('_', ' ').Trim().TrimEnd('.');
        }
    }
}

