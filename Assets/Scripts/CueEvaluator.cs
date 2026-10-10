using UnityEngine;

namespace DigitalPerformance
{
    /// <summary>
    /// 时间轴解释器（纯函数，不持有任何状态）。
    ///
    /// 给定 baseline、目标名和绝对时间 t，算出这个目标此刻应有的全部参数。
    /// 同一组 (baseline, t) 永远得到同一组结果，所以正常播放、拖动进度条、
    /// 离线渲染走的是同一条路径，不会各算各的。
    ///
    /// 一条 cue 的语义是：从 t 时刻起，用 duration 秒，把该通道从"当时的值"
    /// 过渡到目标值。所以 cue 是叠加的，写新动作不需要知道之前的姿态。
    /// </summary>
    public static class CueEvaluator
    {
        public static ParamState Evaluate(CueTable table, string target, ParamState baseline, float t)
        {
            ParamState st = baseline;
            if (table == null || table.cues == null) return st;

            for (int i = 0; i < table.cues.Count; i++)
            {
                var c = table.cues[i];
                if (c == null || c.target != target || t < c.t) continue;

                float u = c.duration <= 0f ? 1f : Mathf.Clamp01((t - c.t) / c.duration);
                u = Easing.Apply(c.easing, u);

                switch (c.channel)
                {
                    case "position":
                        st.position = Vector3.Lerp(st.position, new Vector3(c.v0, c.v1, c.v2), u);
                        break;

                    case "rotation":
                        st.rotation = new Vector3(
                            Mathf.LerpAngle(st.rotation.x, c.v0, u),
                            Mathf.LerpAngle(st.rotation.y, c.v1, u),
                            Mathf.LerpAngle(st.rotation.z, c.v2, u));
                        break;

                    case "scale":
                        st.scale = Vector3.Lerp(st.scale, new Vector3(c.v0, c.v1, c.v2), u);
                        break;

                    case "color":
                        st.color = Color.Lerp(st.color, new Color(c.v0, c.v1, c.v2, c.v3 <= 0f ? 1f : c.v3), u);
                        break;

                    case "intensity":
                        st.intensity = Mathf.Lerp(st.intensity, c.v0, u);
                        break;
                }
            }

            return st;
        }
    }
}
