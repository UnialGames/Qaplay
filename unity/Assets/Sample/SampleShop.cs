using System.Collections;
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
        void AddGold(int amount)
        {
            gold += amount;
            goldLabel.text = gold.ToString("N0", CultureInfo.InvariantCulture);
        }
        Button buy = Button("BuyButton", canvas, "구매", 0.35f, 0.82f, 0.3f, 0.05f);
        // 고유 ID를 붙인 요소: 이름이나 글자가 바뀌어도 {"id": "shop.buy"}로 찾을 수 있다
        buy.gameObject.AddComponent<QaplayId>().id = "shop.buy";
        buy.onClick.AddListener(() =>
        {
            AddGold(-100);
            // 알림이 0.5초 동안 떴다 사라진다: wait의 visible과 gone을 확인하는 데 쓴다
            Object.Destroy(Label("Toast", canvas, "구매 완료", 0.3f, 0.76f, 0.4f, 0.04f).gameObject, 0.5f);
        });

        // 일부러 심은 버그 1, 연타하면 중복 처리: 선물은 한 번만 받을 수 있어야 하는데, 서버 응답(0.5초)이 올 때까지
        // 버튼을 막지 않아서 그 사이에 누른 만큼 요청이 나가고 그만큼 골드가 들어온다.
        // 요청 수는 누르는 즉시 오르므로 규칙이 기다리지 않고 바로 확인할 수 있다
        int giftRequests = 0;
        GameValues.Register("gift.requests", () => giftRequests);
        Button gift = Button("GiftButton", canvas, "선물", 0.05f, 0.82f, 0.25f, 0.05f);
        gift.gameObject.AddComponent<QaplayId>().id = "shop.gift";
        IEnumerator GiftArrives()
        {
            yield return new WaitForSeconds(0.5f);
            AddGold(500);
            gift.interactable = false;
        }
        gift.onClick.AddListener(() =>
        {
            giftRequests++;
            gift.StartCoroutine(GiftArrives());
        });

        // 일부러 심은 버그 2, 동시에 누르면 상태가 꼬임: 판매 팝업의 확인과 취소를 같은 순간에 누르면 둘 다 처리된다.
        // 팝업은 닫히는 연출(0.2초) 동안 화면에 남아 있고 그동안 버튼을 막지 않는다. 그래서 같은 프레임의 두 번째 클릭이
        // 그대로 들어오고, 닫기가 두 번 불려 열린 팝업 수가 -1이 된다. 판매도 되고 취소도 된 상태다.
        // (바로 끄거나(SetActive) 바로 없애는(Destroy) 팝업에서는 이 버그가 나지 않는다. 먼저 처리된 클릭이 팝업을 없애면
        // Unity가 같은 프레임의 나머지 클릭을 전달하지 않는다)
        int popups = 0; // 열려 있는 팝업 수. 안내 팝업도 함께 센다
        GameValues.Register("popups", () => popups);
        Button("SellButton", canvas, "판매", 0.7f, 0.9f, 0.25f, 0.05f).onClick.AddListener(() =>
        {
            popups++;
            Transform sell = Panel("SellPopup", canvas, 0f, 0f, 1f, 1f);
            void Close()
            {
                popups--;
                Object.Destroy(sell.gameObject, 0.2f);
            }
            Label("Message", sell, "검을 300골드에 팝니다", 0.1f, 0.4f, 0.8f, 0.05f);
            Button("ConfirmButton", sell, "확인", 0.15f, 0.5f, 0.3f, 0.06f).onClick.AddListener(() =>
            {
                AddGold(300);
                Close();
            });
            Button("CancelButton", sell, "취소", 0.55f, 0.5f, 0.3f, 0.06f).onClick.AddListener(Close);
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

        // 화면 오른쪽 밖: visible=false로 나와야 한다
        Label("OffscreenBadge", canvas, "화면 밖", 1.2f, 0.5f, 0.2f, 0.05f);

        // 뒤에 만든 패널이 위에 그려져 버튼을 가린다: 누르려 하면 occluded가 나와야 한다
        Button("CoveredButton", canvas, "가려짐", 0.05f, 0.6f, 0.2f, 0.05f);
        Panel("Cover", canvas, 0.03f, 0.58f, 0.24f, 0.09f);

        // 스크롤 목록: 아이템 6개 중 3개만 영역 안에 있다. 영역 밖으로 잘린 아이템은 visible=false로 나와야 한다.
        // 아이템을 누르면 SelectedLabel에 이름이 뜬다
        Text selected = Label("SelectedLabel", canvas, "-", 0.05f, 0.52f, 0.25f, 0.04f);
        Transform list = Panel("ItemList", canvas, 0.35f, 0.52f, 0.55f, 0.22f);
        Transform viewport = New("Viewport", list, typeof(RectMask2D));
        Place(viewport, 0f, 0f, 1f, 1f);
        Transform content = New("Content", viewport);
        Place(content, 0f, 0f, 1f, 2f); // 영역의 두 배 높이
        var scroll = list.gameObject.AddComponent<ScrollRect>();
        scroll.viewport = (RectTransform)viewport;
        scroll.content = (RectTransform)content;
        scroll.horizontal = false;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        string[] items = { "검", "방패", "활", "지팡이", "갑옷", "투구" };
        for (int i = 0; i < items.Length; i++)
        {
            string item = items[i];
            // 프리팹으로 찍어 낸 목록처럼 이름이 모두 같다. 글자나 index로 구분한다
            Button("Item", content, item, 0f, i / 6f, 1f, 1f / 6f).onClick.AddListener(() => selected.text = item);
        }

        // 일부러 심은 버그 3, 메모리 누수: 보상 팝업은 열 때마다 배너 그림(1MB)을 새로 만들고, 받기를 누르면 팝업만 없앤다.
        // 그림은 없애지 않아서 메모리에 남는다(Unity는 코드로 만든 텍스처를 스스로 치우지 않는다). 열 때마다 1MB씩 는다
        Button("RewardButton", canvas, "보상", 0.05f, 0.16f, 0.28f, 0.05f).onClick.AddListener(() =>
        {
            Transform reward = Panel("RewardPopup", canvas, 0f, 0f, 1f, 1f);
            Transform banner = New("Banner", reward, typeof(RawImage));
            Place(banner, 0.3f, 0.3f, 0.4f, 0.15f);
            banner.GetComponent<RawImage>().texture = new Texture2D(512, 512, TextureFormat.RGBA32, false);
            Button("ClaimButton", reward, "받기", 0.35f, 0.5f, 0.3f, 0.06f).onClick.AddListener(() => Object.Destroy(reward.gameObject));
        });

        // 일부러 심은 버그 4, 끊김: 정렬을 메인 스레드에서 한 번에 한다. 누르면 화면이 0.15초 멈춘다
        Button("SortButton", canvas, "정렬", 0.67f, 0.16f, 0.28f, 0.05f).onClick.AddListener(() =>
            System.Threading.Thread.Sleep(150));

        // 결제: 스토어(SampleStore.cs)에 결제를 맡기고, 결제됐다는 답이 오면 보석 100개를 준다.
        // 스토어를 부르는 자리에 PluginCalls 두 줄을 넣어, 호출과 콜백이 짝이 맞는지 테스트가 볼 수 있게 한다.
        // 일부러 심은 버그 5, 콜백 중복: 결제 완료가 두 번 오면 보석도 두 번 준다(이미 지급한 결제인지 확인하지 않는다)
        int gems = 0;
        GameValues.Register("gems", () => gems);
        PluginCalls.Track("store.buy");
        Text payResult = Label("PayResult", canvas, "-", 0.05f, 0.76f, 0.25f, 0.04f);
        Button pay = Button("PayButton", canvas, "결제", 0.05f, 0.7f, 0.25f, 0.05f);
        IStore store = SampleStore.Create(pay);
        pay.onClick.AddListener(() =>
        {
            payResult.text = "결제 중";
            PluginCalls.Started("store.buy");
            store.Buy("gems100", result =>
            {
                PluginCalls.Finished("store.buy", result == PayResult.Paid);
                if (result == PayResult.Paid) gems += 100;
                payResult.text = result == PayResult.Paid ? "결제 완료" : result == PayResult.Cancelled ? "결제 취소" : "결제 실패";
            });
        });

        // 일부러 심은 버그 6, 글자 넘침: 이벤트 패널의 글자 둘은 자기 칸에 들어가지 않는다.
        // 배너는 칸이 좁아 말줄임표로 잘리고(긴 번역문에서 흔하다), 공지는 칸 밖으로 삐져나와 그려진다. 제목은 멀쩡하다.
        // 패널은 다른 버튼을 가리지 않는 빈 자리에 뜬다
        Button("EventButton", canvas, "이벤트", 0.7f, 0.75f, 0.25f, 0.05f).onClick.AddListener(() =>
        {
            Transform ev = Panel("EventPanel", canvas, 0.1f, 0.31f, 0.8f, 0.18f);
            Label("EventTitle", ev, "이벤트", 0.3f, 0.02f, 0.4f, 0.25f);
            TMP_Text banner = TmpLabel("Banner", ev, "LIMITED TIME SUPER SALE - EVERYTHING MUST GO TODAY", 0.05f, 0.35f, 0.25f, 0.25f);
            banner.enableAutoSizing = false;
            banner.fontSize = 48f;
            banner.textWrappingMode = TextWrappingModes.NoWrap;
            banner.overflowMode = TextOverflowModes.Ellipsis;
            Text notice = Label("Notice", ev, "오늘 하루만 전설의 검과 방패와 투구를 모두 90% 할인합니다", 0.05f, 0.68f, 0.25f, 0.25f);
            notice.resizeTextForBestFit = false;
            notice.fontSize = 40;
            notice.horizontalOverflow = HorizontalWrapMode.Overflow;
            Button("EventCloseButton", ev, "닫기", 0.75f, 0.35f, 0.2f, 0.5f).onClick.AddListener(() => Object.Destroy(ev.gameObject));
        });

        // 일부러 문제를 내는 버튼 셋: 러너가 예외, 크래시, 프리징을 구분해 기록하는지 확인하는 데 쓴다.
        // 크래시와 프리징은 에디터를 죽이지 않도록 빌드에서만 실제로 일어난다
        Button("ExceptionButton", canvas, "예외", 0.05f, 0.23f, 0.28f, 0.05f).onClick.AddListener(() =>
            throw new System.InvalidOperationException("sample exception from ExceptionButton"));
        Button("CrashButton", canvas, "크래시", 0.36f, 0.23f, 0.28f, 0.05f).onClick.AddListener(() =>
        {
            if (Application.isEditor) Debug.LogWarning("[sample] CrashButton does nothing in the editor");
            else UnityEngine.Diagnostics.Utils.ForceCrash(UnityEngine.Diagnostics.ForcedCrashCategory.AccessViolation);
        });
        Button("FreezeButton", canvas, "프리징", 0.67f, 0.23f, 0.28f, 0.05f).onClick.AddListener(() =>
        {
            if (Application.isEditor) Debug.LogWarning("[sample] FreezeButton does nothing in the editor");
            else while (true) System.Threading.Thread.Sleep(1000); // 메인 스레드가 돌아오지 않는다
        });

        // 안내 버튼을 누르면 화면 전체를 덮는 팝업이 열린다. 열려 있는 동안 뒤의 버튼은 가려진다.
        // 팝업은 처음에 꺼져 있어 dump에 나오지 않아야 하고, 맨 위에 그려지도록 마지막에 만든다
        Button info = Button("InfoButton", canvas, "안내", 0.05f, 0.9f, 0.25f, 0.05f);
        Transform popup = Panel("InfoPopup", canvas, 0f, 0f, 1f, 1f);
        Transform window = Panel("Window", popup, 0.1f, 0.35f, 0.8f, 0.3f);
        Label("Message", window, "구매하면 골드가 100 줄어듭니다", 0.05f, 0.1f, 0.9f, 0.4f);
        Button("CloseButton", window, "닫기", 0.35f, 0.6f, 0.3f, 0.25f).onClick.AddListener(() =>
        {
            popups--;
            popup.gameObject.SetActive(false);
        });
        popup.gameObject.SetActive(false);
        info.onClick.AddListener(() =>
        {
            popups++;
            popup.gameObject.SetActive(true);
        });
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
