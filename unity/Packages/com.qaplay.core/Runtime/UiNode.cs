using System.Collections.Generic;

namespace Qaplay
{
    /// <summary>화면의 UI 요소 하나. 필드의 뜻은 protocol/README.md 4절.</summary>
    public sealed class UiNode
    {
        public string name;
        public string type;
        /// <summary>프로젝트가 붙인 고유 ID. 붙이지 않았으면 null이고 JSON에서 빠진다.</summary>
        public string id;
        /// <summary>요소가 보여 주는 글자. 없으면 null이고 JSON에서 빠진다.</summary>
        public string text;
        /// <summary>화면 영역. 왼쪽 위가 (0,0), 오른쪽 아래가 (1,1)인 정규화 좌표.</summary>
        public float x, y, w, h;
        public bool visible;
        public bool interactable;
        public readonly List<UiNode> children = new List<UiNode>();
        /// <summary>어댑터가 넣는 원본 객체(uGUI는 GameObject). JSON에는 나오지 않는다.</summary>
        public object handle;

        /// <summary>프로토콜의 요소 형식(4절)으로 바꾼다. find 결과처럼 자식을 싣지 않을 때는 withChildren을 끈다.</summary>
        public Dictionary<string, object> ToData(bool withChildren)
        {
            var data = new Dictionary<string, object> { ["name"] = name, ["type"] = type };
            if (id != null) data["id"] = id;
            if (text != null) data["text"] = text;
            data["rect"] = new Dictionary<string, object> { ["x"] = x, ["y"] = y, ["w"] = w, ["h"] = h };
            data["visible"] = visible;
            data["interactable"] = interactable;
            if (withChildren) data["children"] = children.ConvertAll(child => child.ToData(true));
            return data;
        }
    }

    /// <summary>dump 명령의 결과.</summary>
    public sealed class UiDump
    {
        public int screenWidth, screenHeight;
        public readonly List<UiNode> roots = new List<UiNode>();

        public Dictionary<string, object> ToData() => new Dictionary<string, object>
        {
            ["screen"] = new Dictionary<string, object> { ["width"] = screenWidth, ["height"] = screenHeight },
            ["roots"] = roots.ConvertAll(root => root.ToData(true)),
        };

        public string ToJson() => Json.Write(ToData());
    }
}
