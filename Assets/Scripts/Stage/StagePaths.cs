using System.Collections.Generic;
using UnityEngine;

namespace DigitalPerformance
{
    /// <summary>
    /// 露珠轨迹的预烘焙。
    ///
    /// 珠子的方向不写死，而是每一步沿高度场的负梯度走：所以它会顺着浅碗的坡往下滑，
    /// 顺着风压出来的斜面偏，遇到被手动抬起来的台面还会绕开，最后从最低的那处叶缘滚出去。
    ///
    /// 但"每帧独立由 t 算出"这条性质不能破。所以轨迹在这里一次算完存成一张表，
    /// 播放时只做查表，绝不在运行时积分。
    /// </summary>
    public static class StagePaths
    {
        public const float Step = 0.05f;
        const int MaxSteps = 4000;
        const float SlopeGain = 4f;   // 坡度对方向的偏转权重

        static readonly Dictionary<StageLayer, Vector2[]> Paths = new Dictionary<StageLayer, Vector2[]>();

        public static int Count { get { return Paths.Count; } }

        public static void Bake(StageProgram prog, StageGrid grid)
        {
            Paths.Clear();
            if (prog == null || prog.layers == null || grid == null) return;

            for (int i = 0; i < prog.layers.Count; i++)
            {
                var L = prog.layers[i];
                if (L == null || L.type != "drop") continue;
                Paths[L] = Trace(prog, grid, L);
            }
        }

        static Vector2[] Trace(StageProgram prog, StageGrid grid, StageLayer L)
        {
            int steps = Mathf.Clamp(Mathf.CeilToInt(prog.duration / Step) + 2, 2, MaxSteps);
            var path = new Vector2[steps];

            var pos = new Vector2(L.center_x, L.center_y);
            path[0] = pos;

            float a = L.direction_deg * Mathf.Deg2Rad;
            var intent = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            float limit = Mathf.Max(0.2f, grid.radius - 0.06f);

            for (int i = 1; i < steps; i++)
            {
                float elapsed = (i - 1) * Step;
                var downhill = -StageEvaluator.Gradient(prog, grid, pos, elapsed);

                // 坡度只负责偏转，不负责决定去哪。
                //
                // 这里踩过一个坑：风是"绕着茎旋转的倾斜"，如果让坡度的方向主导，
                // 珠子会被旋转的方向带着绕圈，轨道半径正好是 速度/角速度（实测约 1.3 米），
                // 永远滚不出去。改成"坡度 × 增益 + 既定方向"再归一化之后，
                // 珠子既能顺坡偏转，又能一路走到叶缘。
                var dir = (downhill * SlopeGain + intent).normalized;

                // 下坡会加速
                pos += dir * (L.speed * (1f + L.accel * elapsed) * Step);

                // 滚动时左右轻摆，不是走直线
                if (L.jitter > 0.0001f)
                    pos += new Vector2(-dir.y, dir.x) * (L.jitter * 0.10f * Mathf.Sin(elapsed * 3.1f + L.seed));

                if (pos.magnitude > limit) pos = pos.normalized * limit;   // 到叶缘就停住
                path[i] = pos;
            }

            return path;
        }

        public static bool TryGetPosition(StageLayer layer, float t, out Vector2 pos)
        {
            pos = Vector2.zero;

            Vector2[] path;
            if (layer == null || !Paths.TryGetValue(layer, out path) || path == null || path.Length == 0)
                return false;

            float local = Mathf.Max(0f, t - layer.start - layer.fade_in) / Step;
            int i0 = Mathf.Clamp(Mathf.FloorToInt(local), 0, path.Length - 1);
            int i1 = Mathf.Min(i0 + 1, path.Length - 1);
            pos = Vector2.Lerp(path[i0], path[i1], Mathf.Clamp01(local - i0));
            return true;
        }
    }
}
