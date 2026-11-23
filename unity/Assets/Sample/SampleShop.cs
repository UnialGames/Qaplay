using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 샘플 화면. 씬 파일 없이 Play를 누르면 코드로 만든다.
/// 위치는 프로토콜과 같은 정규화 좌표(왼쪽 위 원점)로 적어서, dump 결과와 바로 비교할 수 있다.
/// </summary>
public static class SampleShop
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Build()
    {
        Transform canvas = New("Canvas", null, typeof(Canvas), typeof(GraphicRaycaster));
        canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;

        Label("GoldLabel", canvas, "1,200", 0.7f, 0.02f, 0.25f, 0.03f);
        Button("BuyButton", canvas, "구매", 0.35f, 0.82f, 0.3f, 0.05f);

        // 비활성화된 버튼: interactable=false로 나와야 한다
        Button("SoldOutButton", canvas, "품절", 0.35f, 0.9f, 0.3f, 0.05f).interactable = false;

        // 투명도 0인 패널: 자신과 자식 모두 visible=false로 나와야 한다
        Transform faded = Panel("FadedPanel", canvas, 0.1f, 0.3f, 0.8f, 0.2f);
        faded.gameObject.AddComponent<CanvasGroup>().alpha = 0f;
        Label("FadedText", faded, "안 보임", 0f, 0f, 1f, 1f);

        // 꺼진 팝업: dump에 나오지 않아야 한다
        Panel("HiddenPopup", canvas, 0.1f, 0.55f, 0.8f, 0.2f).gameObject.SetActive(false);

        // 화면 오른쪽 밖: visible=false로 나와야 한다
        Label("OffscreenBadge", canvas, "화면 밖", 1.2f, 0.5f, 0.2f, 0.05f);
    }

    static Transform New(string name, Transform parent, params System.Type[] components)
    {
        var go = new GameObject(name, typeof(RectTransform));
        foreach (System.Type c in components) go.AddComponent(c);
        go.transform.SetParent(parent, false);
        return go.transform;
    }

    // 부모 영역 안에서의 정규화 좌표(왼쪽 위 원점)로 배치한다
    static void Place(Transform t, float x, float y, float w, float h)
    {
        var rt = (RectTransform)t;
        rt.anchorMin = new Vector2(x, 1f - y - h);
        rt.anchorMax = new Vector2(x + w, 1f - y);
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }

    static Transform Panel(string name, Transform parent, float x, float y, float w, float h)
    {
        Transform t = New(name, parent, typeof(Image));
        t.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.3f);
        Place(t, x, y, w, h);
        return t;
    }

    static Text Label(string name, Transform parent, string text, float x, float y, float w, float h)
    {
        Transform t = New(name, parent, typeof(Text));
        Place(t, x, y, w, h);
        var label = t.GetComponent<Text>();
        label.text = text;
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.alignment = TextAnchor.MiddleCenter;
        label.resizeTextForBestFit = true;
        return label;
    }

    static Button Button(string name, Transform parent, string text, float x, float y, float w, float h)
    {
        Transform t = New(name, parent, typeof(Image), typeof(Button));
        Place(t, x, y, w, h);
        Label("Text", t, text, 0f, 0f, 1f, 1f).color = Color.black;
        return t.GetComponent<Button>();
    }
}
