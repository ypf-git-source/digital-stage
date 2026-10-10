using UnityEngine;

namespace DigitalPerformance
{
    /// <summary>
    /// 几个预设程序。它们同时也是"什么叫合法程序"的样板——
    /// 参数都落在波长、行程、相邻高差的约束之内。
    /// </summary>
    public static class StagePresets
    {
        public const int Count = 7;

        static readonly string[] names =
        {
            "静止基准",
            "整体倾斜",
            "行波起伏",
            "中心圆顶",
            "同心涟漪",
            "单向台阶",
            "随机涌动",
        };

        public static string Name(int i)
        {
            return names[Mathf.Clamp(i, 0, names.Length - 1)];
        }

        public static StageProgram Create(int i)
        {
            var p = new StageProgram { title = Name(i), duration = 12f, loop = true, base_height = 0f };

            switch (Mathf.Clamp(i, 0, names.Length - 1))
            {
                case 1:
                    // 超长波长的行波 = 整体缓慢跷跷板
                    p.layers.Add(new StageLayer
                    {
                        type = "wave", direction_deg = 0f, wavelength = 24f,
                        amplitude = 0.40f, speed = 0.9f,
                        start = 0f, fade_in = 2.5f, hold = 6f, fade_out = 2.5f, easing = "smooth",
                    });
                    break;

                case 2:
                    p.layers.Add(new StageLayer
                    {
                        type = "wave", direction_deg = 0f, wavelength = 4f,
                        amplitude = 0.35f, speed = 0.55f,
                        start = 0.5f, fade_in = 2.5f, hold = 6f, fade_out = 2.5f, easing = "smooth",
                    });
                    break;

                case 3:
                    p.layers.Add(new StageLayer
                    {
                        type = "radial", center_x = 0f, center_y = 0f, radius = 2.6f,
                        amplitude = 0.75f, falloff = "smooth",
                        start = 1f, fade_in = 2.5f, hold = 4f, fade_out = 3f, easing = "smooth",
                    });
                    break;

                case 4:
                    p.layers.Add(new StageLayer
                    {
                        type = "ring", radius = 0f, wavelength = 3f,
                        amplitude = 0.25f, speed = 0.7f,
                        start = 0.5f, fade_in = 2f, hold = 7f, fade_out = 2.5f, easing = "smooth",
                    });
                    break;

                case 5:
                    p.layers.Add(new StageLayer
                    {
                        type = "steps", direction_deg = 0f, step = 1f,
                        amplitude = 0.22f,
                        start = 0.5f, fade_in = 2f, hold = 7f, fade_out = 2.5f, easing = "smooth",
                    });
                    break;

                case 6:
                    p.layers.Add(new StageLayer
                    {
                        type = "noise", amplitude = 0.25f, speed = 0.5f, seed = 20261009,
                        start = 0.5f, fade_in = 2f, hold = 7f, fade_out = 2.5f, easing = "smooth",
                    });
                    break;
            }

            return p;
        }
    }
}
