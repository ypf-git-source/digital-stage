using System.Collections.Generic;
using UnityEngine;

namespace DigitalPerformance
{
    /// <summary>
    /// 可以随时叠加、移除的舞台效果库。
    ///
    /// 关键点：效果定义写在**代码**里，而不是只存在于某一份 stage_program.json。
    /// 所以不管中途换过多少套演出、叠过多少别的效果，"叶心积水"随时都能重新加回来。
    ///
    /// 每个效果的图层都带 group 标签，施加和移除都按标签走，互不干扰；
    /// 演出层用 group = "show"，换演出时只替换它，效果原样保留。
    /// </summary>
    public static class StageEffects
    {
        // 按需求去掉了所有发光图层（叶心积水的高光、露珠的闪光、以及它们的光晕）。
        // 所以效果里不再有 "pool"；发光通道本身和材质里的自发光接口都还在，
        // 需要恢复时把 "pool" 加回这个数组，并把下面 Drops() 里注释掉的两条发光图层接回来即可。
        public static readonly string[] Ids = { "bowl", "wind", "ruffle", "drops" };
        public static readonly string[] Names = { "叶形", "风", "叶缘", "露珠" };

        public static int Count { get { return Ids.Length; } }

        public static bool IsEffect(string group)
        {
            if (string.IsNullOrEmpty(group)) return false;
            for (int i = 0; i < Ids.Length; i++)
                if (Ids[i] == group) return true;
            return false;
        }

        public static string NameOf(string id)
        {
            for (int i = 0; i < Ids.Length; i++)
                if (Ids[i] == id) return Names[i];
            return id;
        }

        public static bool Has(StageProgram prog, string id)
        {
            if (prog == null || prog.layers == null) return false;
            for (int i = 0; i < prog.layers.Count; i++)
                if (prog.layers[i] != null && prog.layers[i].group == id) return true;
            return false;
        }

        public static int Remove(StageProgram prog, string id)
        {
            if (prog == null || prog.layers == null) return 0;
            return prog.layers.RemoveAll(L => L != null && L.group == id);
        }

        /// <summary>施加一个效果：先清掉同名的旧层，再整组加回去。重复调用结果一致。</summary>
        public static void Apply(StageProgram prog, string id)
        {
            if (prog == null) return;
            if (prog.layers == null) prog.layers = new List<StageLayer>();

            Remove(prog, id);
            prog.layers.AddRange(Build(id));
        }

        public static void ApplyAll(StageProgram prog)
        {
            for (int i = 0; i < Ids.Length; i++) Apply(prog, Ids[i]);
        }

        public static List<StageLayer> Build(string id)
        {
            switch (id)
            {
                case "bowl": return Bowl();
                case "wind": return Wind();
                case "ruffle": return Ruffle();
                case "drops": return Drops();
                case "pool": return Pool();
            }
            return new List<StageLayer>();
        }

        // ------------------------------------------------------------ 各个效果

        /// <summary>叶形：中心略低的浅碗。碗不能太深，否则水会被锁在碗底流不出去。</summary>
        static List<StageLayer> Bowl()
        {
            return new List<StageLayer>
            {
                new StageLayer
                {
                    type = "radial", channel = "height", group = "bowl", scope = "all",
                    center_x = 0f, center_y = 0f, radius = 4.2f,
                    amplitude = -0.06f, falloff = "smooth",
                    start = 0f, fade_in = 2.5f, hold = 0f, fade_out = 0f, easing = "smooth",
                },
            };
        }

        /// <summary>风：绕茎扭转 + 从叶心向外推的波。</summary>
        static List<StageLayer> Wind()
        {
            return new List<StageLayer>
            {
                new StageLayer
                {
                    type = "twist", channel = "height", group = "wind", scope = "all",
                    radius = 4f, veins = 1, amplitude = 0.26f, speed = 0.9f,
                    jitter = 1.6f, gust = 0.35f, seed = 11,
                    start = 0f, fade_in = 2.5f, hold = 0f, fade_out = 0f, easing = "smooth",
                },
                new StageLayer
                {
                    type = "ring", channel = "height", group = "wind", scope = "all",
                    radius = 0f, wavelength = 5f, amplitude = 0.06f, speed = 0.8f,
                    jitter = 0.8f, gust = 0.30f, seed = 23,
                    start = 1f, fade_in = 2f, hold = 0f, fade_out = 0f, easing = "smooth",
                },
            };
        }

        static List<StageLayer> Ruffle()
        {
            return new List<StageLayer>
            {
                new StageLayer
                {
                    type = "ruffle", channel = "height", group = "ruffle", scope = "all",
                    radius = 3.6f, band = 0.55f, veins = 17, amplitude = 0.13f, speed = 0.45f,
                    jitter = 2.0f, gust = 0.30f, seed = 37,
                    start = 0f, fade_in = 2f, hold = 0f, fade_out = 0f, easing = "smooth",
                },
            };
        }

        /// <summary>
        /// 露珠：每滴三条图层共用一条轨迹——微凹、白色镜面高光、尾迹；
        /// 另加一条"水走了、叶心暗一下"的负向高光。
        /// </summary>
        static List<StageLayer> Drops()
        {
            var list = new List<StageLayer>();
            AddDrop(list, 2.0f, 35f, 1.20f, 1.3f, -0.07f);
            AddDrop(list, 7.0f, 150f, 1.35f, 1.2f, -0.08f);
            AddDrop(list, 12.0f, 268f, 1.15f, 1.4f, -0.06f);
            return list;
        }

        static void AddDrop(List<StageLayer> list, float start, float directionDeg, float speed,
                            float radius, float amplitude)
        {
            list.Add(new StageLayer
            {
                type = "drop", channel = "height", group = "drops", scope = "all",
                center_x = 0f, center_y = 0f,
                direction_deg = directionDeg, speed = speed, radius = radius, amplitude = amplitude,
                sharp = 1.6f, wake = 0.012f, wavelength = 1.2f, band = 0.9f,
                accel = 0.45f, jitter = 0.5f, seed = 51,
                start = start, fade_in = 0.6f, hold = 3.4f, fade_out = 0.6f, easing = "smooth",
            });

            // 露珠的发光已去掉，只留形变。需要恢复时接回这两条：
            //   1) drop + channel="glow"，radius = radius * 0.38f，amplitude = 1f，
            //      sharp = 2.4f，flicker = 6f（那颗锐利的闪光点）
            //   2) radial + channel="glow"，radius = 2.2f，amplitude = -0.55f，
            //      start = start + 1.1f（水走了，叶心暗一下）
        }

        /// <summary>
        /// 叶心积水：水洼是这一版的主角。三层叠加——亮而紧的水面、更宽的外圈柔光、
        /// 一层缓慢推移的水波，再加几次错开的呼吸。
        /// </summary>
        static List<StageLayer> Pool()
        {
            var list = new List<StageLayer>
            {
                new StageLayer
                {
                    type = "radial", channel = "glow", group = "pool", scope = "all",
                    center_x = 0f, center_y = 0f, radius = 1.9f,
                    amplitude = 0.85f, falloff = "smooth",
                    start = 0f, fade_in = 2.5f, hold = 0f, fade_out = 0f, easing = "smooth",
                },
                // 水面微澜：从中心慢慢推出去的波
                new StageLayer
                {
                    type = "ring", channel = "glow", group = "pool", scope = "all",
                    center_x = 0f, center_y = 0f, radius = 0f,
                    wavelength = 1.8f, amplitude = 0.06f, speed = 0.42f,
                    start = 1f, fade_in = 2.5f, hold = 0f, fade_out = 0f, easing = "smooth",
                },
            };

            // 呼吸：让水面看起来是活的，而不是一盏常亮的灯
            for (int i = 0; i < 4; i++)
            {
                list.Add(new StageLayer
                {
                    type = "radial", channel = "glow", group = "pool", scope = "all",
                    center_x = (i % 2 == 0 ? 1f : -1f) * 0.55f,
                    center_y = (i < 2 ? 1f : -1f) * 0.45f,
                    radius = 1.0f, amplitude = 0.30f, falloff = "smooth",
                    start = 2.5f + i * 3.4f, fade_in = 1.2f, hold = 1.3f, fade_out = 1.5f, easing = "smooth",
                });
            }

            return list;
        }
    }
}
