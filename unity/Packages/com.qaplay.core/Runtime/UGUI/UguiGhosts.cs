using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Qaplay.UGUI
{
    /// <summary>
    /// ghosting 명령. 게임이 ImageLoads로 알린 불러오기마다, 새 그림이 오기 전에 이전 그림이 보인 프레임을 센다.
    /// protocol/README.md의 ghosting.
    /// </summary>
    public static class UguiGhosts
    {
        sealed class Load
        {
            public Graphic graphic;
            /// <summary>불러오기를 시작할 때 보여 주던 그림.</summary>
            public UnityEngine.Object previous;
            public string path, picture;
            public int frames;
            public double ms;
            public bool finished;
        }

        static readonly List<Load> s_Loads = new List<Load>();
        static double s_LastTick;

        /// <summary>게임의 알림을 받기 시작한다. 여러 번 불러도 된다.</summary>
        public static void Hook()
        {
            ImageLoads.OnStarted = Started;
            ImageLoads.OnFinished = Finished;
            s_LastTick = Time.realtimeSinceStartupAsDouble;
        }

        static void Started(Component image)
        {
            Graphic graphic = image == null ? null : image as Graphic ?? image.GetComponent<Graphic>();
            if (graphic == null) return;
            // 같은 이미지의 앞 불러오기가 끝나기 전에 다시 시작했으면, 앞의 것은 거기서 끝난 것으로 친다
            Finished(graphic);
            UnityEngine.Object previous = PictureOf(graphic);
            // 보여 주던 그림이 없으면 잔상이 될 것도 없다
            if (previous == null) return;
            s_Loads.Add(new Load { graphic = graphic, previous = previous, path = PathOf(graphic.transform), picture = previous.name });
        }

        static void Finished(Component image)
        {
            Graphic graphic = image == null ? null : image as Graphic ?? image.GetComponent<Graphic>();
            foreach (Load load in s_Loads)
                if (load.graphic == graphic) load.finished = true;
            // 잔상이 없었던 불러오기는 남길 것이 없다
            s_Loads.RemoveAll(load => load.finished && load.frames == 0);
        }

        /// <summary>프레임마다 한 번 부른다. 불러오는 중인 이미지가 아직 이전 그림을 보여 주고 있으면 그 프레임을 센다.</summary>
        public static void Tick()
        {
            double now = Time.realtimeSinceStartupAsDouble, ms = (now - s_LastTick) * 1000.0;
            s_LastTick = now;
            foreach (Load load in s_Loads)
            {
                if (load.finished) continue;
                // 불러오는 중에 없어진 이미지(닫힌 팝업 등)
                if (load.graphic == null) load.finished = true;
                else if (Showing(load.graphic) && PictureOf(load.graphic) == load.previous)
                {
                    load.frames++;
                    load.ms += ms;
                }
            }
            s_Loads.RemoveAll(load => load.finished && load.frames == 0);
        }

        /// <summary>
        /// 앞의 호출부터 지금까지 잔상이 보인 이미지들(프로토콜의 images 형식)을 돌려주고 새로 세기 시작한다.
        /// 아직 불러오는 중인 것은 지금까지 센 만큼만 들어가고, 그 뒤는 다음 호출에 나온다.
        /// </summary>
        public static List<object> Take()
        {
            var images = new List<object>();
            foreach (Load load in s_Loads)
            {
                if (load.frames == 0) continue;
                images.Add(new Dictionary<string, object>
                {
                    ["path"] = load.path, ["picture"] = load.picture, ["frames"] = load.frames, ["ms"] = Math.Round(load.ms),
                });
                load.frames = 0;
                load.ms = 0;
            }
            s_Loads.RemoveAll(load => load.finished);
            return images;
        }

        // 화면에 그려지고 있는지. 꺼져 있거나, 투명하거나, 마스크에 잘렸으면 이전 그림이 남아 있어도 보이지 않는다
        // ponytail: 화면 밖으로 나간 이미지와 다른 요소에 가려진 이미지는 가리지 않는다. 그런 경우까지 보려면 화면 읽기(dump)의 판정을 쓴다
        static bool Showing(Graphic graphic) =>
            graphic.isActiveAndEnabled && graphic.canvas != null && graphic.canvas.enabled
            && graphic.color.a > 0f && graphic.canvasRenderer.GetInheritedAlpha() > 0f && !graphic.canvasRenderer.cull;

        // 이 요소가 지금 보여 주는 그림. 없으면 null
        static UnityEngine.Object PictureOf(Graphic graphic)
        {
            switch (graphic)
            {
                case Image image: return image.overrideSprite;
                case RawImage raw: return raw.texture;
                default: return null;
            }
        }

        // layout과 같은 경로: 루트 Canvas부터의 이름을 /로 이은 것
        static string PathOf(Transform transform)
        {
            Transform root = transform.GetComponentInParent<Canvas>(true)?.rootCanvas.transform;
            string path = transform.name;
            while (transform != root && transform.parent != null)
            {
                transform = transform.parent;
                path = transform.name + "/" + path;
            }
            return path;
        }
    }
}
