using System;
using System.Diagnostics;
using UnityEngine;

namespace Qaplay
{
    /// <summary>
    /// 이미지를 늦게 불러올 때 생기는 잔상을 테스트가 잡을 수 있게, 불러오기의 시작과 끝을 알린다.
    /// 잔상은 새 그림이 오기 전까지 그 자리에 이전 그림이 그대로 보이는 것이다(목록의 칸을 재사용할 때 흔하다).
    /// <code>
    /// ImageLoads.Started(icon);              // 불러오기를 시작한다. 이 순간 icon이 보여 주던 그림이 "이전 그림"이다
    /// icon.sprite = await LoadAsync(itemId);
    /// ImageLoads.Finished(icon);             // 새 그림을 넣은 뒤에
    /// </code>
    /// 그 사이에 icon이 이전 그림을 보여 준 프레임을 도구가 센다. 시작할 때 그림을 비우거나, 숨기거나,
    /// 자리 표시 그림으로 바꾸는 게임은 한 프레임도 세지 않는다.
    /// Addressables, 웹 이미지, 자체 로더 어느 것이든 같은 두 줄이다. 넘기는 것은 그림을 그리는 컴포넌트(Image, RawImage)다.
    /// GameValues.Register처럼 QA 빌드와 에디터에서만 컴파일되고, 출시 빌드에서는 호출 자체가 사라진다.
    /// </summary>
    public static class ImageLoads
    {
        // 세는 쪽(QA 전용 어셈블리)이 시작할 때 걸어 둔다. 걸려 있지 않으면 아무 일도 하지 않는다
        internal static Action<Component> OnStarted, OnFinished;

        /// <summary>불러오기를 시작할 때, 그림을 바꾸기 전에.</summary>
        [Conditional("QAPLAY"), Conditional("UNITY_EDITOR")]
        public static void Started(Component image) => OnStarted?.Invoke(image);

        /// <summary>새 그림을 넣은 뒤에. 불러오기가 실패해 그림을 그대로 둔 경우에도 부른다.</summary>
        [Conditional("QAPLAY"), Conditional("UNITY_EDITOR")]
        public static void Finished(Component image) => OnFinished?.Invoke(image);
    }
}
