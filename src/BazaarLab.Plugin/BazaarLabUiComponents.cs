using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BazaarLab.Plugin;

internal sealed class BazaarLabWindowDrag : MonoBehaviour, IBeginDragHandler, IDragHandler,
    IPointerDownHandler
{
    public RectTransform? Target { get; set; }
    public Canvas? Canvas { get; set; }
    private Vector2 _startPointer;
    private Vector2 _startPosition;

    public void OnPointerDown(PointerEventData eventData) => Target?.SetAsLastSibling();

    public void OnBeginDrag(PointerEventData eventData)
    {
        if (Target is null) return;
        _startPointer = eventData.position;
        _startPosition = Target.anchoredPosition;
        Target.SetAsLastSibling();
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (Target is null) return;
        float scale = Canvas is null ? 1f : Math.Max(0.01f, Canvas.scaleFactor);
        Target.anchoredPosition = _startPosition + (eventData.position - _startPointer) / scale;
        ClampToCanvas();
    }

    private void ClampToCanvas()
    {
        if (Target is null || Target.parent is not RectTransform parent) return;
        Rect parentRect = parent.rect;
        Vector2 size = Target.rect.size;
        Vector2 position = Target.anchoredPosition;
        float anchorX = parentRect.width * Target.anchorMin.x;
        float left = anchorX + position.x - size.x * Target.pivot.x;
        left = Mathf.Clamp(left, -size.x + 46f, parentRect.width - 46f);
        position.x = left - anchorX + size.x * Target.pivot.x;
        position.y = Mathf.Clamp(position.y, -parentRect.height + 30f, 0f);
        Target.anchoredPosition = position;
    }
}

internal sealed class BazaarLabCurveGraphic : MaskableGraphic, IPointerMoveHandler,
    IPointerExitHandler
{
    private double[] _times = Array.Empty<double>();
    private double[] _damage = Array.Empty<double>();
    private double[] _shield = Array.Empty<double>();
    private double[] _healing = Array.Empty<double>();
    private double _maximum = 1d;
    private bool _hovering;
    private float _hoverRatio;

    public RectTransform? TooltipRoot { get; set; }
    public TMP_Text? TooltipText { get; set; }
    public double Maximum => _maximum;

    public void SetData(double[] times, double[] damage, double[] shield, double[] healing)
    {
        _times = times ?? Array.Empty<double>();
        _damage = damage ?? Array.Empty<double>();
        _shield = shield ?? Array.Empty<double>();
        _healing = healing ?? Array.Empty<double>();
        _maximum = 1d;
        for (int index = 0; index < _times.Length; index++)
        {
            if (index < _damage.Length) _maximum = Math.Max(_maximum, _damage[index]);
            if (index < _shield.Length) _maximum = Math.Max(_maximum, _shield[index]);
            if (index < _healing.Length) _maximum = Math.Max(_maximum, _healing[index]);
        }
        SetVerticesDirty();
    }

    public void OnPointerMove(PointerEventData eventData)
    {
        if (_times.Length < 2) return;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rectTransform,
                eventData.position, eventData.pressEventCamera, out Vector2 local)) return;
        Rect rect = rectTransform.rect;
        _hoverRatio = Mathf.Clamp01((local.x - rect.xMin) / Math.Max(1f, rect.width));
        _hovering = true;
        UpdateTooltip(local);
        SetVerticesDirty();
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        _hovering = false;
        if (TooltipRoot is not null) TooltipRoot.gameObject.SetActive(false);
        SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper helper)
    {
        helper.Clear();
        Rect rect = rectTransform.rect;
        Color grid = new Color(1f, 1f, 1f, 0.14f);
        for (int index = 0; index <= 4; index++)
        {
            float y = Mathf.Lerp(rect.yMin, rect.yMax, index / 4f);
            AddLine(helper, new Vector2(rect.xMin, y), new Vector2(rect.xMax, y), grid, 1f);
        }
        DrawSeries(helper, _damage, new Color(1f, 0.28f, 0.22f, 1f));
        DrawSeries(helper, _shield, new Color(0.24f, 0.72f, 1f, 1f));
        DrawSeries(helper, _healing, new Color(0.28f, 1f, 0.43f, 1f));
        if (_hovering)
        {
            float x = Mathf.Lerp(rect.xMin, rect.xMax, _hoverRatio);
            AddLine(helper, new Vector2(x, rect.yMin), new Vector2(x, rect.yMax),
                new Color(1f, 1f, 1f, 0.75f), 1f);
        }
    }

    private void DrawSeries(VertexHelper helper, double[] values, Color lineColor)
    {
        int count = Math.Min(_times.Length, values.Length);
        if (count < 2) return;
        Rect rect = rectTransform.rect;
        double duration = Math.Max(0.001d, _times[count - 1]);
        for (int index = 1; index < count; index++)
        {
            Vector2 from = new Vector2(
                Mathf.Lerp(rect.xMin, rect.xMax, (float)(_times[index - 1] / duration)),
                Mathf.Lerp(rect.yMin, rect.yMax, (float)(values[index - 1] / _maximum)));
            Vector2 to = new Vector2(
                Mathf.Lerp(rect.xMin, rect.xMax, (float)(_times[index] / duration)),
                Mathf.Lerp(rect.yMin, rect.yMax, (float)(values[index] / _maximum)));
            AddLine(helper, from, to, lineColor, 2f);
        }
    }

    private void UpdateTooltip(Vector2 pointer)
    {
        if (TooltipRoot is null || TooltipText is null || _times.Length < 2) return;
        double duration = Math.Max(0.001d, _times[_times.Length - 1]);
        double time = _hoverRatio * duration;
        int upper = 1;
        while (upper < _times.Length && _times[upper] < time) upper++;
        upper = Math.Min(upper, _times.Length - 1);
        int lower = Math.Max(0, upper - 1);
        double span = Math.Max(0.000001d, _times[upper] - _times[lower]);
        double ratio = Math.Max(0d, Math.Min(1d, (time - _times[lower]) / span));
        double damage = Interpolate(_damage, lower, upper, ratio);
        double shield = Interpolate(_shield, lower, upper, ratio);
        double healing = Interpolate(_healing, lower, upper, ratio);
        TooltipText.text = $"时间：{time:0.0} 秒\n伤害：{damage:0}\n护盾：{shield:0}    治疗：{healing:0}";
        Rect rect = rectTransform.rect;
        const float width = 206f;
        const float height = 80f;
        float x = Mathf.Clamp(pointer.x + 12f, rect.xMin + width * 0.5f + 3f,
            rect.xMax - width * 0.5f - 3f);
        float y = Mathf.Clamp(pointer.y + 12f, rect.yMin + height * 0.5f + 3f,
            rect.yMax - height * 0.5f - 3f);
        TooltipRoot.anchoredPosition = new Vector2(x, y);
        TooltipRoot.gameObject.SetActive(true);
        TooltipRoot.SetAsLastSibling();
    }

    private static double Interpolate(double[] values, int lower, int upper, double ratio)
    {
        if (values.Length == 0) return 0d;
        lower = Math.Min(lower, values.Length - 1);
        upper = Math.Min(upper, values.Length - 1);
        return values[lower] + (values[upper] - values[lower]) * ratio;
    }

    private static void AddLine(VertexHelper helper, Vector2 from, Vector2 to,
        Color lineColor, float thickness)
    {
        Vector2 delta = to - from;
        if (delta.sqrMagnitude < 0.001f) return;
        Vector2 normal = new Vector2(-delta.y, delta.x).normalized * (thickness * 0.5f);
        int start = helper.currentVertCount;
        helper.AddVert(from - normal, lineColor, Vector2.zero);
        helper.AddVert(from + normal, lineColor, Vector2.zero);
        helper.AddVert(to + normal, lineColor, Vector2.zero);
        helper.AddVert(to - normal, lineColor, Vector2.zero);
        helper.AddTriangle(start, start + 1, start + 2);
        helper.AddTriangle(start, start + 2, start + 3);
    }
}
