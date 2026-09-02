using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TMPro;
using TheBazaar;
using UnityEngine;
using UnityEngine.UI;

namespace BazaarLab.Plugin;

public sealed partial class Plugin
{
    private Canvas? _labCanvas;
    private RuntimeUiTheme? _labTheme;
    private float _nextThemeProbeAt;
    private readonly List<Image> _labPanels = new();
    private readonly List<Image> _labButtons = new();
    private readonly List<Image> _labCompactButtons = new();
    private readonly List<Image> _labInputs = new();
    private readonly List<TMP_Text> _labTexts = new();
    private readonly List<TMP_Text> _labButtonTexts = new();
    private LabWindow? _placementUi;
    private Button? _placementPlanButton, _placementApplyButton, _placementUndoButton;
    private Image? _placementProgressFill;
    private TMP_Text? _placementProgressText, _placementStatusText, _placementScoreText,
        _placementBoardText;
    private LabWindow? _baselineUi;
    private TMP_Text? _baselineSummaryText;
    private BazaarLabCurveGraphic? _baselineGraph;
    private readonly TMP_Text?[] _baselineAxisLabels = new TMP_Text?[5];
    private BaselineReportDto? _uiBaselineSource;
    private LabWindow? _lineupUi;
    private TMP_InputField? _lineupInputA, _lineupInputB, _lineupSeedInput;
    private TMP_Text? _lineupDescriptionA, _lineupDescriptionB, _lineupStatusText,
        _lineupCatalogText, _lineupResultText, _lineupPlayText, _lineupToastText;
    private Button? _lineupClearButton, _lineupPlayButton;
    private RectTransform? _lineupToastRoot;
    private string _uiLastCodeA = "\0", _uiLastCodeB = "\0";

    private sealed class RuntimeUiTheme
    {
        public TMP_FontAsset? Font;
        public Color PanelColor = new(0.075f, 0.09f, 0.12f, 0.58f);
        public Color TitleColor = new(0.22f, 0.14f, 0.075f, 0.72f);
        public Color ButtonColor = new(0.28f, 0.20f, 0.11f, 0.78f);
        public Color InputColor = new(0.035f, 0.045f, 0.06f, 0.48f);
        public Color TextColor = new(0.96f, 0.90f, 0.72f, 1f);
        public Color ButtonTextColor = new(1f, 0.61f, 0.24f, 1f);
        public int NativeScore;
    }

    private sealed class LabWindow
    {
        public RectTransform Root = null!;
        public RectTransform Body = null!;
        public Image Panel = null!;
        public Image Title = null!;
        public TMP_Text MinimizeText = null!;
    }

    private sealed class ButtonBinding
    {
        public Button Button = null!;
        public TMP_Text Text = null!;
    }

    private void InitializeFloatingWindowControls()
    {
        _labTheme = ResolveRuntimeUiTheme();
        GameObject root = new("BazaarLab.NativeUi");
        DontDestroyOnLoad(root);
        _labCanvas = root.AddComponent<Canvas>();
        _labCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _labCanvas.sortingOrder = 32760;
        CanvasScaler scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        root.AddComponent<GraphicRaycaster>();
        CreatePlacementWindow(root.transform);
        CreateBaselineWindow(root.transform);
        CreateLineupWindow(root.transform);
        ApplyRuntimeUiTheme();
        UpdateFloatingWindowControls();
    }

    private void DisposeFloatingWindowControls()
    {
        if (_labCanvas is not null) Destroy(_labCanvas.gameObject);
        _labCanvas = null;
        _placementUi = null;
        _baselineUi = null;
        _lineupUi = null;
        _labPanels.Clear();
        _labButtons.Clear();
        _labCompactButtons.Clear();
        _labInputs.Clear();
        _labTexts.Clear();
        _labButtonTexts.Clear();
    }

    private void SetFloatingWindowBlocker(int index, Rect rect, bool visible) { }

    private void UpdateFloatingWindowControls()
    {
        if (_labCanvas is null) return;
        if (Time.unscaledTime >= _nextThemeProbeAt)
        {
            _nextThemeProbeAt = Time.unscaledTime + 5f;
            RuntimeUiTheme candidate = ResolveRuntimeUiTheme();
            if (_labTheme is null || candidate.NativeScore > _labTheme.NativeScore)
            {
                _labTheme = candidate;
                ApplyRuntimeUiTheme();
            }
        }
        UpdatePlacementWindow();
        UpdateBaselineWindow();
        UpdateLineupWindow();
    }

    private void CreatePlacementWindow(Transform parent)
    {
        _placementUi = CreateWindow(parent, "Placement", "本地摆位规划", 340f, 270f,
            new Vector2(-358f, -70f), true,
            () => _placementWindowMinimized = !_placementWindowMinimized);
        RectTransform body = _placementUi.Body;
        _placementPlanButton = CreateButton(body, "规划", 10f, 10f, 98f, 34f,
            StartPlacementSearch).Button;
        _placementApplyButton = CreateButton(body, "应用", 116f, 10f, 98f, 34f,
            StartMovePlan).Button;
        _placementUndoButton = CreateButton(body, "撤销", 222f, 10f, 98f, 34f,
            StartUndoPlan).Button;
        Image progressBackground = CreateImage(body, "进度背景", 10f, 53f, 310f, 22f,
            true, new Color(0f, 0f, 0f, 0.55f));
        _labInputs.Add(progressBackground);
        GameObject fillObject = new("进度");
        fillObject.transform.SetParent(progressBackground.transform, false);
        _placementProgressFill = fillObject.AddComponent<Image>();
        _placementProgressFill.color = new Color(0.27f, 0.75f, 0.35f, 0.92f);
        RectTransform fill = _placementProgressFill.rectTransform;
        fill.anchorMin = Vector2.zero;
        fill.anchorMax = new Vector2(0f, 1f);
        fill.pivot = new Vector2(0f, 0.5f);
        fill.offsetMin = new Vector2(2f, 2f);
        fill.offsetMax = new Vector2(2f, -2f);
        _placementProgressText = CreateText(progressBackground.transform, "等待启动", 12f,
            TextAlignmentOptions.Center, 0f, 0f, 310f, 22f);
        _placementStatusText = CreateText(body, string.Empty, 15f,
            TextAlignmentOptions.TopLeft, 10f, 82f, 310f, 46f);
        _placementScoreText = CreateText(body, string.Empty, 13f,
            TextAlignmentOptions.TopLeft, 10f, 128f, 310f, 40f);
        _placementBoardText = CreateText(body, string.Empty, 14f,
            TextAlignmentOptions.TopLeft, 10f, 168f, 310f, 76f);
        _placementBoardText.overflowMode = TextOverflowModes.Truncate;
    }

    private void UpdatePlacementWindow()
    {
        if (_placementUi is null) return;
        bool visible = Data.Run?.Player is not null;
        _placementUi.Root.gameObject.SetActive(visible);
        if (!visible) return;
        bool hasResult = _placementResult?.Recommendation?.Board is { Count: > 0 };
        SetWindowState(_placementUi, _placementWindowMinimized,
            _placementWindowMinimized ? 34f : hasResult ? 270f : 170f);
        if (_placementPlanButton is not null)
            _placementPlanButton.interactable = !IsSearching && !IsMoving;
        if (_placementApplyButton is not null)
            _placementApplyButton.interactable = !IsSearching && !IsMoving && hasResult;
        if (_placementUndoButton is not null)
            _placementUndoButton.interactable = !IsSearching && !IsMoving && _undoPlan is not null;
        float progress;
        string progressLabel;
        lock (_placementProgressLock)
        {
            progress = Mathf.Clamp01(_placementProgress);
            progressLabel = _placementProgressLabel;
        }
        if (_placementProgressFill is not null)
            _placementProgressFill.rectTransform.anchorMax = new Vector2(progress, 1f);
        if (_placementProgressText is not null)
        {
            _placementProgressText.text = IsSearching
                ? $"{progressLabel}  {progress * 100f:0}%" : string.Empty;
            _placementProgressText.transform.parent.gameObject.SetActive(IsSearching);
        }
        if (_placementStatusText is not null) _placementStatusText.text = _placementStatus;
        if (_placementScoreText is not null && _placementBoardText is not null)
        {
            PlacementScoreDto? score = _placementResult?.Recommendation?.ValidationScore;
            _placementScoreText.text = hasResult
                ? $"10秒评分：{score?.Score ?? 0d:0.#}  伤害：{score?.AverageDamageScore ?? 0d:0.#}\n防御：{score?.AverageDefenseScore ?? 0d:0.#}  样本：{score?.Samples ?? 0}"
                : string.Empty;
            _placementBoardText.text = hasResult
                ? string.Join("\n", _placementResult!.Recommendation!.Board!
                    .OrderBy(value => value.BoardPosition).Select(item =>
                        $"{item.BoardPosition}: {item.Name} ({item.Span})" +
                        (item.FromStash ? "  [来自背包]" : string.Empty)))
                : string.Empty;
        }
    }

    private void CreateBaselineWindow(Transform parent)
    {
        _baselineUi = CreateWindow(parent, "Baseline", "30 秒无限血白板——累计输出",
            630f, 306f, new Vector2(18f, -754f), false,
            () => _baselineWindowMinimized = !_baselineWindowMinimized);
        RectTransform body = _baselineUi.Body;
        _baselineSummaryText = CreateText(body, string.Empty, 14f,
            TextAlignmentOptions.TopLeft, 12f, 8f, 606f, 40f);
        Image graphBackground = CreateImage(body, "曲线背景", 70f, 50f, 540f, 188f,
            true, new Color(0.02f, 0.027f, 0.04f, 0.88f));
        _labInputs.Add(graphBackground);
        RectTransform graphRect = CreateRect(graphBackground.transform, "曲线绘图", 0f, 0f,
            540f, 188f);
        _baselineGraph = graphRect.gameObject.AddComponent<BazaarLabCurveGraphic>();
        _baselineGraph.color = Color.white;
        _baselineGraph.raycastTarget = true;
        for (int index = 0; index <= 4; index++)
            _baselineAxisLabels[index] = CreateText(body, "0", 12f,
                TextAlignmentOptions.MidlineRight, 4f, 42f + index * 47f, 60f, 20f);
        CreateText(body, "红：伤害   蓝：护盾   绿：治疗", 13f,
            TextAlignmentOptions.Left, 70f, 242f, 360f, 22f);
        CreateText(body, "30 秒", 13f, TextAlignmentOptions.Right, 540f, 242f, 70f, 22f);
        Image tooltipPanel = CreateImage(graphRect, "曲线提示", 0f, 0f,
            206f, 80f, true, new Color(0.04f, 0.05f, 0.07f, 0.98f));
        _labInputs.Add(tooltipPanel);
        RectTransform tooltipRoot = tooltipPanel.rectTransform;
        tooltipRoot.anchorMin = tooltipRoot.anchorMax = new Vector2(0f, 1f);
        tooltipRoot.pivot = new Vector2(0.5f, 0.5f);
        TMP_Text tooltipText = CreateText(tooltipPanel.transform, string.Empty, 13f,
            TextAlignmentOptions.TopLeft, 8f, 6f, 190f, 68f);
        tooltipPanel.gameObject.SetActive(false);
        _baselineGraph.TooltipRoot = tooltipRoot;
        _baselineGraph.TooltipText = tooltipText;
    }

    private void UpdateBaselineWindow()
    {
        if (_baselineUi is null) return;
        bool visible = Data.Run?.Player is not null &&
            _baselineResult?.Points is { Count: > 1 };
        _baselineUi.Root.gameObject.SetActive(visible);
        if (!visible || _baselineResult is null) return;
        SetWindowState(_baselineUi, _baselineWindowMinimized,
            _baselineWindowMinimized ? 34f : 306f);
        if (_baselineSummaryText is not null)
            _baselineSummaryText.text = $"伤害 {_baselineResult.TotalDamage:0}   护盾 {_baselineResult.TotalShield:0}   " +
                $"治疗 {_baselineResult.TotalHealing:0}\n{_baselineStatus}";
        if (!ReferenceEquals(_uiBaselineSource, _baselineResult) && _baselineGraph is not null)
        {
            _uiBaselineSource = _baselineResult;
            List<BaselinePointDto> points = _baselineResult.Points!;
            _baselineGraph.SetData(points.Select(value => value.TimeSeconds).ToArray(),
                points.Select(value => value.Damage).ToArray(),
                points.Select(value => value.Shield).ToArray(),
                points.Select(value => value.Healing).ToArray());
            for (int index = 0; index <= 4; index++)
                if (_baselineAxisLabels[index] is not null)
                    _baselineAxisLabels[index]!.text = FormatCurveAxisValue(
                        _baselineGraph.Maximum * (4 - index) / 4d);
        }
    }

    private void CreateLineupWindow(Transform parent)
    {
        _lineupUi = CreateWindow(parent, "Lineup", "本地阵容码对战", 690f, 574f,
            new Vector2(18f, -70f), false,
            () => _lineupWindowMinimized = !_lineupWindowMinimized);
        RectTransform title = _lineupUi.Title.rectTransform;
        CreateButton(title, "打开历史目录", 398f, 4f, 122f, 26f,
            OpenLineupHistoryDirectory);
        CreateButton(title, "复制当前阵容", 526f, 4f, 126f, 26f,
            CopyPreferredLineup);
        RectTransform body = _lineupUi.Body;
        CreateButton(body, "当前阵容 → A", 10f, 8f, 150f, 32f,
            () => ExportPreferredTo(ref _lineupCodeA));
        CreateButton(body, "当前阵容 → B", 168f, 8f, 150f, 32f,
            () => ExportPreferredTo(ref _lineupCodeB));
        CreateButton(body, "对手阵容 → B", 326f, 8f, 150f, 32f, ExportOpponentToB);
        CreateText(body, "阵容 A", 14f, TextAlignmentOptions.Left, 10f, 47f, 90f, 22f);
        _lineupInputA = CreateInput(body, "阵容 A", 10f, 69f, 660f, 70f, true);
        _lineupInputA.onValueChanged.AddListener(value => _lineupCodeA = value);
        CreateButton(body, "粘贴 A", 10f, 145f, 86f, 28f,
            () => _lineupCodeA = GUIUtility.systemCopyBuffer.Trim());
        _lineupDescriptionA = CreateText(body, string.Empty, 13f,
            TextAlignmentOptions.Left, 104f, 145f, 566f, 28f);
        CreateText(body, "阵容 B", 14f, TextAlignmentOptions.Left, 10f, 178f, 90f, 22f);
        _lineupInputB = CreateInput(body, "阵容 B", 10f, 200f, 660f, 70f, true);
        _lineupInputB.onValueChanged.AddListener(value => _lineupCodeB = value);
        CreateButton(body, "粘贴 B", 10f, 276f, 86f, 28f,
            () => _lineupCodeB = GUIUtility.systemCopyBuffer.Trim());
        _lineupDescriptionB = CreateText(body, string.Empty, 13f,
            TextAlignmentOptions.Left, 104f, 276f, 566f, 28f);
        CreateButton(body, "交换", 10f, 312f, 74f, 34f, () =>
        {
            string temporary = _lineupCodeA;
            _lineupCodeA = _lineupCodeB;
            _lineupCodeB = temporary;
        });
        _lineupClearButton = CreateButton(body, "清空输入", 92f, 312f, 88f, 34f,
            ClearLineupDuelInputs).Button;
        CreateText(body, "种子", 14f, TextAlignmentOptions.Center, 190f, 312f, 42f, 34f);
        _lineupSeedInput = CreateInput(body, "种子", 236f, 312f, 120f, 34f, false);
        _lineupSeedInput.characterLimit = 12;
        _lineupSeedInput.onValueChanged.AddListener(value => _lineupSeed = value);
        ButtonBinding play = CreateButton(body, "确认并播放", 366f, 312f, 160f, 34f, () =>
        {
            _lineupAutoPlayRequested = true;
            StartLocalDuel();
        });
        _lineupPlayButton = play.Button;
        _lineupPlayText = play.Text;
        _lineupStatusText = CreateText(body, string.Empty, 14f,
            TextAlignmentOptions.TopLeft, 10f, 355f, 660f, 48f);
        _lineupCatalogText = CreateText(body, string.Empty, 13f,
            TextAlignmentOptions.TopLeft, 10f, 407f, 660f, 25f);
        _lineupResultText = CreateText(body, string.Empty, 14f,
            TextAlignmentOptions.TopLeft, 10f, 437f, 660f, 82f);
        Image toast = CreateImage(_lineupUi.Root, "提示", 180f, 38f, 340f, 38f,
            true, new Color(0.12f, 0.24f, 0.12f, 0.98f));
        _labInputs.Add(toast);
        _lineupToastRoot = toast.rectTransform;
        _lineupToastText = CreateText(toast.transform, string.Empty, 14f,
            TextAlignmentOptions.Center, 6f, 3f, 328f, 32f);
        toast.gameObject.SetActive(false);
    }

    private void UpdateLineupWindow()
    {
        if (_lineupUi is null) return;
        _lineupUi.Root.gameObject.SetActive(true);
        SetWindowState(_lineupUi, _lineupWindowMinimized,
            _lineupWindowMinimized ? 34f : 574f);
        SyncInput(_lineupInputA, _lineupCodeA);
        SyncInput(_lineupInputB, _lineupCodeB);
        SyncInput(_lineupSeedInput, _lineupSeed);
        if (!string.Equals(_uiLastCodeA, _lineupCodeA, StringComparison.Ordinal))
        {
            _uiLastCodeA = _lineupCodeA;
            if (_lineupDescriptionA is not null)
                _lineupDescriptionA.text = TranslateCodeDescription(DescribeCode(_lineupCodeA));
        }
        if (!string.Equals(_uiLastCodeB, _lineupCodeB, StringComparison.Ordinal))
        {
            _uiLastCodeB = _lineupCodeB;
            if (_lineupDescriptionB is not null)
                _lineupDescriptionB.text = TranslateCodeDescription(DescribeCode(_lineupCodeB));
        }
        bool available = !IsLocalDuelCalculating && !IsSearching && !IsMoving &&
            !IsMonsterCalculating && !IsBaselineCalculating && !IsEncounterPreviewCalculating;
        if (_lineupClearButton is not null) _lineupClearButton.interactable = available;
        if (_lineupPlayButton is not null) _lineupPlayButton.interactable = available;
        if (_lineupPlayText is not null)
            _lineupPlayText.text = IsLocalDuelCalculating ? "正在准备回放……" : "确认并播放";
        if (_lineupStatusText is not null) _lineupStatusText.text = _lineupStatus;
        if (_lineupCatalogText is not null)
        {
            string fingerprint = GetCatalogFingerprint();
            _lineupCatalogText.text = "卡表：" + _catalogStatus + " · " +
                (IsSha256(fingerprint) ? fingerprint.Substring(0, 12) : fingerprint);
        }
        if (_lineupResultText is not null)
        {
            if (_lineupDuelResult is null) _lineupResultText.text = string.Empty;
            else
            {
                var builder = new StringBuilder();
                builder.Append($"A 胜率 {_lineupDuelResult.PlayerOutcomeProbability:P1}   ");
                builder.Append($"胜/负/平 {_lineupDuelResult.PlayerWins}/");
                builder.Append($"{_lineupDuelResult.OpponentWins}/{_lineupDuelResult.Draws}   ");
                builder.Append($"样本 {_lineupDuelResult.Samples}");
                if (_lineupDuelResult.UnsupportedActions is { Count: > 0 })
                    builder.Append("\n未支持动作：" + string.Join(", ",
                        _lineupDuelResult.UnsupportedActions.Select(pair =>
                            pair.Key + "=" + pair.Value)));
                if (_lineupDuelTraceEvents > 0)
                    builder.Append($"\n回放源轨迹：{_lineupDuelTraceEvents} 个完整事件");
                _lineupResultText.text = builder.ToString();
            }
        }
        if (_lineupToastRoot is not null && _lineupToastText is not null)
        {
            bool show = !string.IsNullOrEmpty(_lineupClipboardToast) &&
                Time.realtimeSinceStartup < _lineupClipboardToastUntil;
            _lineupToastRoot.gameObject.SetActive(show);
            if (show) _lineupToastText.text = _lineupClipboardToast;
        }
    }

    private LabWindow CreateWindow(Transform parent, string name, string title, float width,
        float height, Vector2 position, bool rightAnchored, Action toggle)
    {
        GameObject rootObject = new("BazaarLab." + name + "Window");
        rootObject.transform.SetParent(parent, false);
        Image panel = rootObject.AddComponent<Image>();
        panel.raycastTarget = true;
        _labPanels.Add(panel);
        RectTransform root = panel.rectTransform;
        root.anchorMin = root.anchorMax = rightAnchored ? new Vector2(1f, 1f) : new Vector2(0f, 1f);
        root.pivot = new Vector2(0f, 1f);
        root.anchoredPosition = position;
        root.sizeDelta = new Vector2(width, height);
        Image titleImage = CreateImage(root, "标题栏", 0f, 0f, width, 34f, true,
            new Color(0.22f, 0.14f, 0.075f, 0.98f));
        TMP_Text titleText = CreateText(titleImage.transform, title, 18f,
            TextAlignmentOptions.Left, 28f, 0f, width - 88f, 34f);
        titleText.fontStyle = FontStyles.Bold;
        ButtonBinding minimize = CreateButton(titleImage.rectTransform, "—",
            width - 38f, 4f, 30f, 26f, toggle);
        RectTransform body = CreateRect(root, "内容", 0f, 34f, width, height - 34f);
        BazaarLabWindowDrag drag = titleImage.gameObject.AddComponent<BazaarLabWindowDrag>();
        drag.Target = root;
        drag.Canvas = _labCanvas;
        return new LabWindow { Root = root, Body = body, Panel = panel, Title = titleImage,
            MinimizeText = minimize.Text };
    }

    private void SetWindowState(LabWindow window, bool minimized, float height)
    {
        window.Root.sizeDelta = new Vector2(window.Root.sizeDelta.x, height);
        window.Body.gameObject.SetActive(!minimized);
        window.MinimizeText.text = minimized ? "+" : "—";
    }

    private ButtonBinding CreateButton(RectTransform parent, string label, float x, float y,
        float width, float height, Action action)
    {
        Image image = CreateImage(parent, label, x, y, width, height, true,
            _labTheme?.ButtonColor ?? Color.gray);
        if (height < 30f || width < 60f) _labCompactButtons.Add(image);
        else _labButtons.Add(image);
        Button button = image.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        ColorBlock colors = button.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(1.15f, 1.08f, 0.88f, 1f);
        colors.pressedColor = new Color(0.72f, 0.68f, 0.58f, 1f);
        colors.disabledColor = new Color(0.45f, 0.45f, 0.45f, 0.65f);
        colors.colorMultiplier = 1f;
        button.colors = colors;
        button.onClick.AddListener(() => action());
        TMP_Text text = CreateText(image.transform, label, 14f,
            TextAlignmentOptions.Center, 2f, 1f, width - 4f, height - 2f);
        _labButtonTexts.Add(text);
        return new ButtonBinding { Button = button, Text = text };
    }

    private TMP_InputField CreateInput(RectTransform parent, string placeholder, float x,
        float y, float width, float height, bool multiline)
    {
        Image background = CreateImage(parent, "输入框", x, y, width, height, true,
            _labTheme?.InputColor ?? new Color(0.03f, 0.04f, 0.05f, 0.95f));
        _labInputs.Add(background);
        TMP_InputField input = background.gameObject.AddComponent<TMP_InputField>();
        RectTransform viewport = CreateRect(background.rectTransform, "文本区域", 7f, 4f,
            width - 14f, height - 8f);
        viewport.gameObject.AddComponent<RectMask2D>();
        TMP_Text text = CreateText(viewport, string.Empty, 13f, TextAlignmentOptions.TopLeft,
            0f, 0f, width - 14f, height - 8f);
        text.textWrappingMode = multiline ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
        TMP_Text hint = CreateText(viewport, placeholder, 13f, TextAlignmentOptions.TopLeft,
            0f, 0f, width - 14f, height - 8f);
        hint.color = new Color(1f, 1f, 1f, 0.35f);
        input.textViewport = viewport;
        input.textComponent = text;
        input.placeholder = hint;
        input.lineType = multiline ? TMP_InputField.LineType.MultiLineNewline :
            TMP_InputField.LineType.SingleLine;
        input.characterLimit = multiline ? MaxLineupCodeLength : 64;
        return input;
    }

    private TMP_Text CreateText(Transform parent, string value, float fontSize,
        TextAlignmentOptions alignment, float x, float y, float width, float height)
    {
        GameObject textObject = new("文本");
        textObject.transform.SetParent(parent, false);
        TextMeshProUGUI text = textObject.AddComponent<TextMeshProUGUI>();
        text.text = value;
        text.fontSize = fontSize;
        text.alignment = alignment;
        text.color = _labTheme?.TextColor ?? Color.white;
        text.raycastTarget = false;
        text.textWrappingMode = TextWrappingModes.Normal;
        if (_labTheme?.Font is not null) text.font = _labTheme.Font;
        SetTopLeft(text.rectTransform, x, y, width, height);
        _labTexts.Add(text);
        return text;
    }

    private Image CreateImage(Transform parent, string name, float x, float y, float width,
        float height, bool raycast, Color color)
    {
        GameObject imageObject = new(name);
        imageObject.transform.SetParent(parent, false);
        Image image = imageObject.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = raycast;
        SetTopLeft(image.rectTransform, x, y, width, height);
        return image;
    }

    private static RectTransform CreateRect(Transform parent, string name, float x, float y,
        float width, float height)
    {
        GameObject child = new(name, typeof(RectTransform));
        child.transform.SetParent(parent, false);
        RectTransform rect = (RectTransform)child.transform;
        SetTopLeft(rect, x, y, width, height);
        return rect;
    }

    private static void SetTopLeft(RectTransform rect, float x, float y, float width, float height)
    {
        rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(x, -y);
        rect.sizeDelta = new Vector2(width, height);
    }

    private static void SyncInput(TMP_InputField? input, string value)
    {
        if (input is null || input.isFocused || string.Equals(input.text, value,
                StringComparison.Ordinal)) return;
        input.SetTextWithoutNotify(value);
    }

    private static string TranslateCodeDescription(string value)
    {
        if (value == "empty") return "未输入";
        if (value.StartsWith("invalid: ", StringComparison.Ordinal))
            return "无效：" + value.Substring(9);
        return value.Replace(" board", " 件上阵物品")
            .Replace(" skills", " 个技能")
            .Replace(" [catalog differs]", " [卡表不一致]");
    }

    private RuntimeUiTheme ResolveRuntimeUiTheme()
    {
        var theme = new RuntimeUiTheme();
        try
        {
            TMP_Text? text = Resources.FindObjectsOfTypeAll<TMP_Text>()
                .Where(value => value is not null && value.font is not null &&
                    !IsBazaarLabObject(value.gameObject))
                .OrderByDescending(value => value.font.HasCharacter('中'))
                .FirstOrDefault();
            if (text is not null)
            {
                theme.Font = text.font;
                theme.TextColor = text.color.a > 0.5f ? text.color : theme.TextColor;
                theme.NativeScore += 2;
            }
        }
        catch (Exception exception)
        {
            Logger.LogDebug("native UI theme probe skipped: " + exception.Message);
        }
        return theme;
    }

    private static bool IsBazaarLabObject(GameObject gameObject)
    {
        Transform? root = gameObject.transform.root;
        return root is not null && root.name.StartsWith("BazaarLab.", StringComparison.Ordinal);
    }

    private void ApplyRuntimeUiTheme()
    {
        RuntimeUiTheme theme = _labTheme ?? new RuntimeUiTheme();
        foreach (Image panel in _labPanels.Where(value => value is not null))
        {
            panel.sprite = null;
            panel.type = Image.Type.Simple;
            panel.color = theme.PanelColor;
        }
        foreach (Image button in _labButtons.Where(value => value is not null))
        {
            button.sprite = null;
            button.type = Image.Type.Simple;
            button.color = theme.ButtonColor;
            Button? control = button.GetComponent<Button>();
            if (control is not null) control.transition = Selectable.Transition.ColorTint;
        }
        foreach (Image button in _labCompactButtons.Where(value => value is not null))
        {
            button.sprite = null;
            button.type = Image.Type.Simple;
            button.color = theme.ButtonColor;
            Button? control = button.GetComponent<Button>();
            if (control is not null) control.transition = Selectable.Transition.ColorTint;
        }
        foreach (Image input in _labInputs.Where(value => value is not null))
        {
            input.sprite = null;
            input.type = Image.Type.Simple;
            input.color = theme.InputColor;
        }
        foreach (TMP_Text text in _labTexts.Where(value => value is not null))
        {
            if (theme.Font is not null) text.font = theme.Font;
            if (text.color.a > 0.5f) text.color = theme.TextColor;
        }
        foreach (TMP_Text text in _labButtonTexts.Where(value => value is not null))
            text.color = theme.ButtonTextColor;
        ApplyTitleTheme(_placementUi, theme);
        ApplyTitleTheme(_baselineUi, theme);
        ApplyTitleTheme(_lineupUi, theme);
    }

    private static void ApplyTitleTheme(LabWindow? window, RuntimeUiTheme theme)
    {
        if (window is null) return;
        window.Title.sprite = null;
        window.Title.type = Image.Type.Simple;
        window.Title.color = theme.TitleColor;
    }
}
