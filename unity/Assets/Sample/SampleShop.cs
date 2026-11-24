using System.Globalization;
using Qaplay;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

/// <summary>
/// 샘플 화면. 씬 파일 없이 Play를 누르면 코드로 만든다.
/// 위치는 프로토콜과 같은 정규화 좌표(왼쪽 위 원점)로 적어서, dump 결과와 바로 비교할 수 있다.
/// </summary>
public static class SampleShop
{
    static GameObject s_Root;

    /// <summary>화면을 처음 상태로 만든다. 다시 부르면 이전 화면을 버리고 새로 만든다(테스트마다 호출).</summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    public static void Build()
    {
        if (s_Root != null)
        {
            // Destroy는 프레임 끝에 처리되므로, 그 전에 dump에 나오지 않도록 먼저 끈다
            s_Root.SetActive(false);
            Object.Destroy(s_Root);
        }
        s_Root = new GameObject("SampleShop");

        var eventSystem = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
        eventSystem.transform.SetParent(s_Root.transform, false);

        Transform canvas = New("Canvas", s_Root.transform, typeof(Canvas), typeof(GraphicRaycaster));
        canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;

        // 구매를 누르면 골드가 100 줄어든다
        int gold = 1200;
        // 화면 글자("1,200")가 아니라 실제 값(1200)을 get 명령으로 읽을 수 있게 등록한다
        GameValues.Register("gold", () => gold);
        Text goldLabel = Label("GoldLabel", canvas, "1,200", 0.7f, 0.02f, 0.25f, 0.03f);
        Button("BuyButton", canvas, "구매", 0.35f, 0.82f, 0.3f, 0.05f).onClick.AddListener(() =>
        {
            gold -= 100;
            goldLabel.text = gold.ToString("N0", CultureInfo.InvariantCulture);
            // 알림이 0.5초 동안 떴다 사라진다: wait의 visible과 gone을 확인하는 데 쓴다
            Object.Destroy(Label("Toast", canvas, "구매 완료", 0.3f, 0.76f, 0.4f, 0.04f).gameObject, 0.5f);
        });

        // TextMesh Pro로 그린 글자와, 글자가 TextMesh Pro인 버튼: 구형 Text와 똑같이 나와야 한다
        TmpLabel("Title", canvas, "SHOP", 0.3f, 0.1f, 0.4f, 0.05f);
        Transform refresh = New("RefreshButton", canvas, typeof(Image), typeof(Button));
        Place(refresh, 0.7f, 0.82f, 0.25f, 0.05f);
        TmpLabel("Text", refresh, "REFRESH", 0f, 0f, 1f, 1f).color = Color.black;

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

        // 뒤에 만든 패널이 위에 그려져 버튼을 가린다: 누르려 하면 occluded가 나와야 한다
        Button("CoveredButton", canvas, "가려짐", 0.05f, 0.6f, 0.2f, 0.05f);
        Panel("Cover", canvas, 0.03f, 0.58f, 0.24f, 0.09f);
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

    // 글자는 영문만 쓴다. TextMesh Pro 기본 글꼴에는 한글이 없다
    static TMP_Text TmpLabel(string name, Transform parent, string text, float x, float y, float w, float h)
    {
        Transform t = New(name, parent, typeof(TextMeshProUGUI));
        Place(t, x, y, w, h);
        var label = t.GetComponent<TMP_Text>();
        label.text = text;
        label.alignment = TextAlignmentOptions.Center;
        label.enableAutoSizing = true;
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
