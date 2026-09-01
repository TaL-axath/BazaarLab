using UnityEngine;
using UnityEngine.UI;

namespace BazaarLab.Plugin;

public sealed partial class Plugin
{
    private Canvas? _floatingWindowBlockerCanvas;
    private readonly Image?[] _floatingWindowBlockers = new Image?[3];

    private void InitializeFloatingWindowControls()
    {
        var root = new GameObject("BazaarLab.FloatingWindowInputBlockers");
        DontDestroyOnLoad(root);
        _floatingWindowBlockerCanvas = root.AddComponent<Canvas>();
        _floatingWindowBlockerCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _floatingWindowBlockerCanvas.sortingOrder = short.MaxValue;
        root.AddComponent<GraphicRaycaster>();
        for (int index = 0; index < _floatingWindowBlockers.Length; index++)
        {
            var blockerObject = new GameObject("WindowBlocker" + index);
            blockerObject.transform.SetParent(root.transform, false);
            Image blocker = blockerObject.AddComponent<Image>();
            blocker.color = new Color(0f, 0f, 0f, 0f);
            blocker.raycastTarget = true;
            blockerObject.SetActive(false);
            _floatingWindowBlockers[index] = blocker;
        }
    }

    private void DisposeFloatingWindowControls()
    {
        if (_floatingWindowBlockerCanvas is not null)
            Destroy(_floatingWindowBlockerCanvas.gameObject);
        _floatingWindowBlockerCanvas = null;
        for (int index = 0; index < _floatingWindowBlockers.Length; index++)
            _floatingWindowBlockers[index] = null;
    }

    private void SetFloatingWindowBlocker(int index, Rect rect, bool visible)
    {
        Image? blocker = index >= 0 && index < _floatingWindowBlockers.Length
            ? _floatingWindowBlockers[index] : null;
        if (blocker is null) return;
        blocker.gameObject.SetActive(visible);
        if (!visible) return;
        RectTransform transform = blocker.rectTransform;
        transform.anchorMin = new Vector2(0f, 1f);
        transform.anchorMax = new Vector2(0f, 1f);
        transform.pivot = new Vector2(0f, 1f);
        transform.anchoredPosition = new Vector2(rect.x, -rect.y);
        transform.sizeDelta = new Vector2(rect.width, rect.height);
    }
}
