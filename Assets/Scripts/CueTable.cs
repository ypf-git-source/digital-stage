using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DigitalPerformance
{
    /// <summary>
    /// 一条 cue：在 t 秒开始，用 duration 秒，把某个目标的某个通道
    /// 从"它当时的值"过渡到 v0..v3。
    ///
    /// 这是语言层唯一被允许写入的东西——它是规划回路与执行回路之间的契约。
    /// </summary>
    [Serializable]
    public class Cue
    {
        public float t;
        public string target;      // 舞台对象的名字，例如 P1 / Camera / Focus
        public string channel;     // position | rotation | scale | color
        public float duration;
        public string easing;      // linear | smooth | in | out | inout
        public float v0, v1, v2, v3;

        // "show" = 人工编排， "audio" = 音频分析自动生成。
        // 有了它，重新烘焙音频 cue 时可以先清掉旧的那一批，不会越叠越多。
        public string source = "show";
    }

    /// <summary>整场演出的脚本：一条时间轴，加上演出总时长。</summary>
    [Serializable]
    public class CueTable
    {
        public string title = "untitled";
        public float duration = 12f;
        public bool loop = true;
        public List<Cue> cues = new List<Cue>();

        public static CueTable LoadFromFile(string path)
        {
            return JsonUtility.FromJson<CueTable>(File.ReadAllText(path));
        }

        public void SaveToFile(string path)
        {
            File.WriteAllText(path, JsonUtility.ToJson(this, true));
        }
    }

    /// <summary>求值用的缓动函数。</summary>
    public static class Easing
    {
        public static float Apply(string name, float u)
        {
            u = Mathf.Clamp01(u);
            switch (string.IsNullOrEmpty(name) ? "linear" : name.ToLowerInvariant())
            {
                case "smooth": return u * u * (3f - 2f * u);
                case "in": return u * u;
                case "out": return 1f - (1f - u) * (1f - u);
                case "inout": return u < 0.5f ? 2f * u * u : 1f - 2f * (1f - u) * (1f - u);
                default: return u;
            }
        }
    }
}
