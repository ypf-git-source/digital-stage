using System.Collections.Generic;
using UnityEngine;

namespace DigitalPerformance
{
    public class StageIssue
    {
        public bool isError;
        public string message;

        public StageIssue(bool isError, string message)
        {
            this.isError = isError;
            this.message = message;
        }

        public override string ToString()
        {
            return (isError ? "[错误] " : "[警告] ") + message;
        }
    }

    /// <summary>
    /// 把语言层产出的程序过一遍物理与安全约束。
    ///
    /// 这是"语言不能直接驱动设备"这条原则的具体落点：模型可以随便提要求，
    /// 但落不到机械行程里、或者会让人迈不上去的形状，必须在这里被拦下来或者标出来。
    /// 校验是纯计算，不碰任何设备，所以可以在动用真舞台之前反复跑。
    /// </summary>
    public static class StageValidator
    {
        public static List<StageIssue> Validate(StageProgram prog, StageGrid grid, StageLimits lim)
        {
            var issues = new List<StageIssue>();
            if (prog == null)
            {
                issues.Add(new StageIssue(true, "程序为空"));
                return issues;
            }

            if (prog.moves != null)
            {
                for (int i = 0; i < prog.moves.Count; i++)
                {
                    var m = prog.moves[i];
                    if (m == null) continue;
                    if (grid.Find(m.lift) == null)
                        issues.Add(new StageIssue(true, "单台指令引用了不存在的台面：" + m.lift));
                }
            }

            if (prog.layers != null)
            {
                for (int i = 0; i < prog.layers.Count; i++)
                {
                    var L = prog.layers[i];
                    if (L == null) continue;

                    // 只检查会形变的图层：纯发光图层不改变高度，不存在采样混叠的问题
                    if ((L.type == "wave" || L.type == "ring") && L.channel == "height" &&
                        L.wavelength < lim.minWavelength)
                        issues.Add(new StageIssue(false, string.Format(
                            "{0} 图层波长 {1:F2} m 小于两格（{2:F1} m），6 格采样后会退化成噪点",
                            L.type, L.wavelength, lim.minWavelength)));

                    if (L.type == "steps" && L.step < grid.cellSize * 0.9f)
                        issues.Add(new StageIssue(false, string.Format(
                            "台阶级距 {0:F2} m 小于一格（{1:F1} m），台面离散度高，看不出阶梯",
                            L.step, grid.cellSize)));
                }
            }

            // ---------------- 逐帧采样，检查行程 / 速度 / 相邻高差 ----------------

            const float dt = 1f / 30f;
            int steps = Mathf.Max(2, Mathf.CeilToInt(prog.duration / dt) + 1);
            int n = grid.lifts.Count;

            var raw = new float[n];
            var clamped = new float[n];
            var prev = new float[n];

            float worstOver = 0f, worstSpeed = 0f, worstStep = 0f;
            string overWho = "", speedWho = "", stepWho = "";
            float overAt = 0f, speedAt = 0f, stepAt = 0f;

            for (int s = 0; s < steps; s++)
            {
                float t = s * dt;

                for (int i = 0; i < n; i++)
                {
                    raw[i] = StageEvaluator.Height(prog, grid, grid.lifts[i], t);

                    float over = Mathf.Max(lim.minHeight - raw[i], raw[i] - lim.maxHeight);
                    if (over > worstOver)
                    {
                        worstOver = over;
                        overWho = grid.lifts[i].id;
                        overAt = t;
                    }

                    clamped[i] = Mathf.Clamp(raw[i], lim.minHeight, lim.maxHeight);
                }

                if (s > 0)
                {
                    for (int i = 0; i < n; i++)
                    {
                        float v = Mathf.Abs(clamped[i] - prev[i]) / dt;
                        if (v > worstSpeed)
                        {
                            worstSpeed = v;
                            speedWho = grid.lifts[i].id;
                            speedAt = t;
                        }
                    }

                    string pair;
                    float st = StageEvaluator.MaxAdjacentStep(grid, clamped, out pair);
                    if (st > worstStep)
                    {
                        worstStep = st;
                        stepWho = pair;
                        stepAt = t;
                    }
                }

                System.Array.Copy(clamped, prev, n);
            }

            if (worstOver > 0.01f)
                issues.Add(new StageIssue(true, string.Format(
                    "行程超限：{0} 在 t={1:F2}s 超出行程 {2:F2} m（上限 {3:F1} m / 下限 {4:F1} m）",
                    overWho, overAt, worstOver, lim.maxHeight, lim.minHeight)));

            if (worstSpeed > lim.maxSpeed)
            {
                bool hard = worstSpeed > lim.maxSpeed * 1.3f;
                issues.Add(new StageIssue(hard, string.Format(
                    "速度超限：{0} 在 t={1:F2}s 达到 {2:F2} m/s（上限 {3:F1} m/s）",
                    speedWho, speedAt, worstSpeed, lim.maxSpeed)));
            }

            if (worstStep > lim.maxAdjacentStep)
            {
                bool hard = worstStep > lim.maxAdjacentStep * 1.5f;
                issues.Add(new StageIssue(hard, string.Format(
                    "相邻高差过大：{0} 在 t={1:F2}s 相差 {2:F2} m（上限 {3:F1} m，超了人就走不上去）",
                    stepWho, stepAt, worstStep, lim.maxAdjacentStep)));
            }

            return issues;
        }

        public static int CountErrors(List<StageIssue> issues)
        {
            int n = 0;
            for (int i = 0; i < issues.Count; i++) if (issues[i].isError) n++;
            return n;
        }
    }
}
