using UnityEngine;

namespace DigitalPerformance
{
    /// <summary>
    /// 高度场求值。和表演那边一样，全部由**绝对时间**算出，不做逐帧累加，
    /// 所以拖进度条和正常播放结果一致，也能离线渲染。
    ///
    /// 顺序是：基准高度 → 各图层相加 → 单台指令覆盖 → 行程夹紧。
    /// </summary>
    public static class StageEvaluator
    {
        /// <summary>图层的时间包络：淡入 → 保持 → 淡出。</summary>
        public static float Envelope(StageLayer L, float t)
        {
            float e = t - L.start;
            if (e < 0f) return 0f;

            if (L.fade_in > 0f && e < L.fade_in)
                return Easing.Apply(L.easing, e / L.fade_in);

            if (L.fade_out <= 0f) return 1f;      // 常驻图层

            float outStart = L.fade_in + Mathf.Max(0f, L.hold);
            if (e <= outStart) return 1f;

            float u = (e - outStart) / L.fade_out;
            return u >= 1f ? 0f : 1f - Easing.Apply(L.easing, u);
        }

        /// <summary>单个图层在某个平面位置、某个时刻贡献的高度。</summary>
        public static float LayerValue(StageLayer L, Vector2 p, float t)
        {
            float env = Envelope(L, t);
            if (env <= 0f) return 0f;

            // 阵风：幅度随时间缓慢起伏，避免"匀速机器"的感觉
            if (L.gust > 0.0001f)
                env *= 1f + L.gust * (ValueNoise(new Vector2(t * 0.17f, L.seed * 0.37f), L.seed) * 2f - 1f);

            switch (L.type)
            {
                case "wave":
                {
                    float a = L.direction_deg * Mathf.Deg2Rad;
                    float d = p.x * Mathf.Cos(a) + p.y * Mathf.Sin(a);
                    float k = 2f * Mathf.PI / Mathf.Max(L.wavelength, 0.05f);
                    return env * L.amplitude * Mathf.Sin(k * d - k * L.speed * t + PhaseJitter(L, p));
                }

                case "ring":
                {
                    float k = 2f * Mathf.PI / Mathf.Max(L.wavelength, 0.05f);
                    return env * L.amplitude
                         * Mathf.Sin(k * (p.magnitude - L.radius) - k * L.speed * t + PhaseJitter(L, p));
                }

                case "radial":
                {
                    var c = new Vector2(L.center_x, L.center_y);
                    float q = Mathf.Clamp01(1f - (p - c).magnitude / Mathf.Max(L.radius, 0.05f));
                    float shape = L.falloff == "linear" ? q : q * q * (3f - 2f * q);
                    return env * L.amplitude * shape;
                }

                case "steps":
                {
                    float a = L.direction_deg * Mathf.Deg2Rad;
                    float d = p.x * Mathf.Cos(a) + p.y * Mathf.Sin(a);
                    return env * L.amplitude * Mathf.Floor(d / Mathf.Max(L.step, 0.05f));
                }

                case "noise":
                {
                    var q = p * 0.5f + new Vector2(t * L.speed, -t * L.speed);
                    return env * L.amplitude * (ValueNoise(q, L.seed) * 2f - 1f);
                }

                case "drop":
                {
                    // 水珠：珠头位置优先用预烘焙的轨迹（沿坡度走），没烘焙就退回直线。
                    Vector2 head;
                    if (!StagePaths.TryGetPosition(L, t, out head))
                    {
                        float a = L.direction_deg * Mathf.Deg2Rad;
                        var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                        head = new Vector2(L.center_x, L.center_y)
                             + dir * (L.speed * Mathf.Max(0f, t - L.start - L.fade_in));
                    }

                    float d = (p - head).magnitude;
                    float q = Mathf.Clamp01(1f - d / Mathf.Max(L.radius, 0.05f));
                    float core = Mathf.Pow(q * q * (3f - 2f * q), Mathf.Max(1f, L.sharp));

                    // 闪光：沿轨迹高频明暗，读起来是水珠在反光，不是一团常亮的雾
                    if (L.flicker > 0.001f)
                    {
                        float ft = Mathf.Max(0f, t - L.start - L.fade_in);
                        core *= 0.55f + 0.45f * Mathf.Sin(2f * Mathf.PI * L.flicker * ft);
                    }

                    float trail = 0f;
                    if (L.wake > 0.0001f)
                    {
                        float elapsed = Mathf.Max(0f, t - L.start - L.fade_in);
                        float k = 2f * Mathf.PI / Mathf.Max(L.wavelength, 0.2f);
                        trail = L.wake * Mathf.Sin(k * (d - L.speed * elapsed))
                              * Mathf.Exp(-d / Mathf.Max(L.band, 0.1f));
                    }

                    return env * (L.amplitude * core + trail);
                }

                case "ruffle":
                {
                    // 叶缘褶皱：只在半径 band 附近的一圈起作用，沿角度方向起伏
                    float r = p.magnitude;
                    float ang = Mathf.Atan2(p.y, p.x);
                    float band = Mathf.Exp(-Mathf.Pow((r - L.radius) / Mathf.Max(L.band, 0.05f), 2f));
                    return env * L.amplitude * Mathf.Sin(ang * L.veins - L.speed * t + PhaseJitter(L, p)) * band;
                }

                case "twist":
                {
                    // 扭转：幅度随半径增大，沿角度方向有波瓣——读起来是叶片绕茎转动
                    float q = Mathf.Clamp01(p.magnitude / Mathf.Max(L.radius, 0.05f));
                    float ang = Mathf.Atan2(p.y, p.x);
                    return env * L.amplitude * q * Mathf.Sin(ang * L.veins - L.speed * t + PhaseJitter(L, p));
                }

                default:   // plane
                    return env * (L.tilt_x * p.x + L.tilt_y * p.y + L.offset);
            }
        }

        /// <summary>未夹紧的高度：基准 + 图层 + 单台指令。</summary>
        public static float Height(StageProgram prog, StageGrid grid, LiftInfo lift, float t)
        {
            if (prog == null || grid == null || lift == null) return 0f;
            float h = FieldAt(prog, grid, lift, lift.plan, t, true);
            return ApplyMoves(prog, lift.id, h, t);
        }

        /// <summary>
        /// 连续高度场：给表皮网格用。
        /// 图层是在任意平面位置上求值的，scope 用"包含该点的那块台面"判断，
        /// 所以同一套程序既能驱动 60 块离散台面，也能算出它们之间那张连续曲面。
        /// </summary>
        public static float HeightAt(StageProgram prog, StageGrid grid, Vector2 plan, float t, bool includeDrop)
        {
            var host = grid != null ? grid.LiftAt(plan) : null;
            if (host == null) return prog != null ? prog.base_height : 0f;

            float h = FieldAt(prog, grid, host, plan, t, includeDrop);
            return ApplyMoves(prog, host.id, h, t);
        }

        /// <summary>不含露珠自身的表面高度——露珠用它来导航。</summary>
        public static float SurfaceHeight(StageProgram prog, StageGrid grid, Vector2 plan, float t)
        {
            return HeightAt(prog, grid, plan, t, false);
        }

        /// <summary>高度场的梯度（有限差分）。露珠沿它的反方向滚。</summary>
        public static Vector2 Gradient(StageProgram prog, StageGrid grid, Vector2 at, float t, float probe = 0.25f)
        {
            float dx = SurfaceHeight(prog, grid, at + new Vector2(probe, 0f), t)
                     - SurfaceHeight(prog, grid, at - new Vector2(probe, 0f), t);
            float dy = SurfaceHeight(prog, grid, at + new Vector2(0f, probe), t)
                     - SurfaceHeight(prog, grid, at - new Vector2(0f, probe), t);
            return new Vector2(dx, dy) / (2f * probe);
        }

        static float FieldAt(StageProgram prog, StageGrid grid, LiftInfo host, Vector2 plan, float t, bool includeDrop)
        {
            float h = prog.base_height;
            if (prog.layers == null || grid == null) return h;

            for (int i = 0; i < prog.layers.Count; i++)
            {
                var L = prog.layers[i];
                if (L == null) continue;
                if (L.channel == "glow") continue;                   // 发光通道不参与形变
                if (!includeDrop && L.type == "drop") continue;      // 露珠不该给自己导航
                if (!grid.InScope(L.scope, host)) continue;
                h += LayerValue(L, plan, t);
            }
            return h;
        }

        /// <summary>
        /// 相位抖动：按空间位置给每个点加一点相位偏移。
        ///
        /// 全部同相位时，波浪看起来是"机械波"；给相位加一层平滑的空间噪声之后，
        /// 相邻位置就各走各的，整片叶子才有有机起伏的感觉。
        /// 噪声是确定性的，所以照样可复现；用平滑噪声而不是逐块随机，是为了让表皮保持连续。
        /// </summary>
        static float PhaseJitter(StageLayer L, Vector2 p)
        {
            if (L.jitter <= 0.0001f) return 0f;
            return L.jitter * (ValueNoise(p * 0.35f, L.seed) * 2f - 1f);
        }

        static float ApplyMoves(StageProgram prog, string liftId, float h, float t)
        {
            if (prog.moves == null || string.IsNullOrEmpty(liftId)) return h;

            for (int i = 0; i < prog.moves.Count; i++)
            {
                var m = prog.moves[i];
                if (m == null || m.lift != liftId) continue;

                float e = t - m.start;
                if (e < 0f) continue;

                float u = m.duration <= 0f ? 1f : Mathf.Clamp01(e / m.duration);
                u = Easing.Apply(m.easing, u);

                if (m.mode == "add") h += m.height * u;
                else h = Mathf.Lerp(h, m.height, u);   // set：覆盖图层
            }
            return h;
        }

        /// <summary>发光通道：0 到 1，只用来让台面亮起来，不进高度。</summary>
        public static float Glow(StageProgram prog, StageGrid grid, LiftInfo lift, float t)
        {
            float g = 0f;
            if (prog.layers == null) return 0f;

            for (int i = 0; i < prog.layers.Count; i++)
            {
                var L = prog.layers[i];
                if (L == null || L.channel != "glow") continue;
                if (!grid.InScope(L.scope, lift)) continue;
                g += LayerValue(L, lift.plan, t);
            }

            return Mathf.Clamp01(g);
        }

        public static void EvaluateGlow(StageProgram prog, StageGrid grid, float t, float[] outGlow)
        {
            for (int i = 0; i < grid.Count; i++)
                outGlow[i] = Glow(prog, grid, grid.lifts[i], t);
        }

        public static float ClampToLimits(float h, StageLimits limits)
        {
            return Mathf.Clamp(h, limits.minHeight, limits.maxHeight);
        }

        /// <summary>
        /// 把整体基准面抬高，让整个程序落进行程里，返回抬高的米数。
        ///
        /// 这条很关键：人说"波浪起伏""随机涌动"时，默认是围绕某个中间高度上下摆，
        /// 但升降台只能从基准面往上走，下半部分会被直接切平。所以编译器必须先自动抬高基准面。
        /// 如果幅度本身就超过行程，抬多少都救不回来，那就只抬到不越上限为止，
        /// 剩下的交给 StageValidator 报错。
        /// </summary>
        public static float AutoRebase(StageProgram prog, StageGrid grid, StageLimits limits)
        {
            if (prog == null || grid == null || grid.Count == 0) return 0f;

            const float dt = 1f / 30f;
            float lowest = float.MaxValue;
            float highest = float.MinValue;

            int steps = Mathf.Max(2, Mathf.CeilToInt(prog.duration / dt) + 1);
            for (int s = 0; s < steps; s++)
            {
                float t = s * dt;
                for (int i = 0; i < grid.Count; i++)
                {
                    float h = Height(prog, grid, grid.lifts[i], t);
                    if (h < lowest) lowest = h;
                    if (h > highest) highest = h;
                }
            }

            if (lowest == float.MaxValue) return 0f;

            float shift = Mathf.Clamp(limits.minHeight - lowest, 0f, limits.maxHeight - highest);
            if (shift <= 0.005f) return 0f;

            prog.base_height += shift;
            return shift;
        }

        /// <summary>一次算出全场 36 块的高度。</summary>
        public static void Evaluate(StageProgram prog, StageGrid grid, StageLimits limits,
                                    float t, float[] outHeights)
        {
            for (int i = 0; i < grid.lifts.Count; i++)
                outHeights[i] = ClampToLimits(Height(prog, grid, grid.lifts[i], t), limits);
        }

        /// <summary>相邻台面高差的最大值，用来判断人还能不能走上去。</summary>
        public static float MaxAdjacentStep(StageGrid grid, float[] heights, out string pair)
        {
            float worst = 0f;
            pair = "";

            for (int i = 0; i < grid.lifts.Count; i++)
            {
                var a = grid.lifts[i];
                CheckNeighbour(a, grid.At(a.row, a.col + 1), grid, heights, ref worst, ref pair);
                CheckNeighbour(a, grid.At(a.row + 1, a.col), grid, heights, ref worst, ref pair);
            }
            return worst;
        }

        static void CheckNeighbour(LiftInfo a, LiftInfo b, StageGrid grid, float[] heights,
                                   ref float worst, ref string pair)
        {
            if (b == null) return;
            float d = Mathf.Abs(heights[a.index] - heights[b.index]);
            if (d > worst)
            {
                worst = d;
                pair = a.id + " / " + b.id;
            }
        }

        // ------------------------------------------------------------ 确定性随机

        static float Hash(int x, int y, int seed)
        {
            unchecked
            {
                int h = x * 374761393 + y * 668265263 + seed * 1274126177;
                h = (h ^ (h >> 13)) * 1274126177;
                h ^= h >> 16;
                return (h & 0x7fffffff) / (float)0x7fffffff;
            }
        }

        /// <summary>值噪声。带种子的确定性实现——同一个种子每次跑出来一样，这是可复现的前提。</summary>
        public static float ValueNoise(Vector2 p, int seed)
        {
            int x0 = Mathf.FloorToInt(p.x), y0 = Mathf.FloorToInt(p.y);
            float fx = p.x - x0, fy = p.y - y0;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);

            float a = Hash(x0, y0, seed);
            float b = Hash(x0 + 1, y0, seed);
            float c = Hash(x0, y0 + 1, seed);
            float d = Hash(x0 + 1, y0 + 1, seed);

            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
        }
    }
}
