using System.Collections.Generic;
using System.IO;
using DigitalPerformance;
using UnityEditor;
using UnityEngine;

namespace DigitalPerformance.EditorTools
{
    /// <summary>
    /// 离线音频分析 -> cue 表。
    ///
    /// 为什么必须离线烘焙，而不是运行时读频谱：
    ///   1. 运行时读频谱拿到的是"这一帧听到什么"，同一个 t 重播一次结果可能不同，
    ///      整场演出就不可复现了；
    ///   2. 离线分析可以用全局统计量定阈值，比实时因果检测稳得多
    ///      （周期性的鼓点会把滑动平均阈值顶上去，反而检测不到）；
    ///   3. 结果落成 cue 之后，音频模块就只是"往 cues.json 追加内容"的又一个来源，
    ///      播放侧一行都不用改。
    ///
    /// 分频段驱动：
    ///   低频 -> 主灯强度脉冲（运动）
    ///   中频 -> 主灯颜色推进（色彩）
    ///   高频 -> 细节灯微闪（细节）
    /// </summary>
    public static class AudioCueBaker
    {
        // 必须和 PerformanceBuilder 建灯时的初始值一致，否则"回到基准"会跑偏
        public const float BaseIntensity = 1.1f;
        public const float KickPeak = 6.0f;
        public const float AccentBase = 0.5f;
        public const float AccentPeak = 2.6f;

        public const string WavAssetPath = "Assets/Audio/show.wav";
        public const string CueAssetPath = "Assets/StreamingAssets/cues.json";

        static readonly string[] MainLights = { "L1", "L2", "L3", "L4" };
        const string AccentLight = "L5";

        static readonly Color[] BarColors =
        {
            new Color(0.20f, 0.55f, 1.00f),
            new Color(1.00f, 0.45f, 0.15f),
            new Color(0.25f, 0.85f, 0.55f),
            new Color(0.95f, 0.30f, 0.65f),
            new Color(0.60f, 0.45f, 1.00f),
            new Color(0.95f, 0.90f, 0.55f),
        };

        // ---------------------------------------------------------------- 二阶滤波

        struct Biquad
        {
            float b0, b1, b2, a1, a2;
            float x1, x2, y1, y2;

            public static Biquad LowPass(float f0, int sampleRate)
            {
                float w0 = 2f * Mathf.PI * f0 / sampleRate;
                float c = Mathf.Cos(w0);
                return Make((1f - c) * 0.5f, 1f - c, (1f - c) * 0.5f, c, Mathf.Sin(w0) / (2f * 0.70710678f));
            }

            public static Biquad HighPass(float f0, int sampleRate)
            {
                float w0 = 2f * Mathf.PI * f0 / sampleRate;
                float c = Mathf.Cos(w0);
                return Make((1f + c) * 0.5f, -(1f + c), (1f + c) * 0.5f, c, Mathf.Sin(w0) / (2f * 0.70710678f));
            }

            static Biquad Make(float b0, float b1, float b2, float cosW0, float alpha)
            {
                float a0 = 1f + alpha;
                var q = new Biquad();
                q.b0 = b0 / a0;
                q.b1 = b1 / a0;
                q.b2 = b2 / a0;
                q.a1 = -2f * cosW0 / a0;
                q.a2 = (1f - alpha) / a0;
                return q;
            }

            public float Process(float x)
            {
                float y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
                x2 = x1;
                x1 = x;
                y2 = y1;
                y1 = y;
                return y;
            }
        }

        static float[] ApplyBiquad(float[] x, Biquad f)
        {
            var y = new float[x.Length];
            for (int i = 0; i < x.Length; i++) y[i] = f.Process(x[i]);
            return y;
        }

        static float[] BandLow(float[] x, int sr)
        {
            return ApplyBiquad(x, Biquad.LowPass(150f, sr));
        }

        static float[] BandMid(float[] x, int sr)
        {
            var stage = ApplyBiquad(x, Biquad.LowPass(3500f, sr));
            return ApplyBiquad(stage, Biquad.HighPass(1200f, sr));
        }

        static float[] BandHigh(float[] x, int sr)
        {
            return ApplyBiquad(x, Biquad.HighPass(7000f, sr));
        }

        // ---------------------------------------------------------------- WAV 解码

        class AudioData
        {
            public float[] samples;
            public int sampleRate;
        }

        /// <summary>
        /// 直接解析 WAV 字节，不依赖 Unity 的导入设置。
        /// 走 AudioClip.GetData 的话，导入选项（压缩方式、是否常驻）会决定拿不拿得到样本，
        /// 批量脚本里出问题很难查。只支持 16-bit PCM。
        /// </summary>
        static AudioData LoadWav(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 44) throw new System.Exception("WAV 文件过短: " + path);

            int pos = 12;
            int channels = 1, sampleRate = 44100, bits = 16, dataOffset = -1, dataLength = 0;

            while (pos + 8 <= bytes.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
                int size = System.BitConverter.ToInt32(bytes, pos + 4);
                int body = pos + 8;

                if (id == "fmt ")
                {
                    channels = System.BitConverter.ToInt16(bytes, body + 2);
                    sampleRate = System.BitConverter.ToInt32(bytes, body + 4);
                    bits = System.BitConverter.ToInt16(bytes, body + 14);
                }
                else if (id == "data")
                {
                    dataOffset = body;
                    dataLength = Mathf.Min(size, bytes.Length - body);
                    break;
                }

                pos = body + size + (size & 1);
            }

            if (dataOffset < 0) throw new System.Exception("WAV 缺少 data 块: " + path);
            if (bits != 16) throw new System.Exception("只支持 16-bit PCM，实际是 " + bits + " bit");
            if (channels < 1) throw new System.Exception("WAV 声道数异常");

            int frames = dataLength / (channels * 2);
            var mono = new float[frames];

            for (int i = 0; i < frames; i++)
            {
                float sum = 0f;
                for (int c = 0; c < channels; c++)
                {
                    int idx = dataOffset + (i * channels + c) * 2;
                    short v = (short)(bytes[idx] | (bytes[idx + 1] << 8));
                    sum += v / 32768f;
                }
                mono[i] = sum / channels;
            }

            return new AudioData { samples = mono, sampleRate = sampleRate };
        }

        // ---------------------------------------------------------------- 起振检测

        /// <summary>
        /// 用全局阈值找起振点。离线分析才有这个条件：阈值取整段信号 RMS 的 99 分位乘系数，
        /// 而不是滑动平均——周期性鼓点会让滑动平均被自身顶高，那是实时检测器的经典陷阱。
        /// </summary>
        public static List<float> DetectOnsets(float[] band, int sampleRate)
        {
            const float hop = 0.005f;     // 5 ms 时间分辨率
            const float frac = 0.40f;     // 阈值 = 99 分位 × 0.40
            const float reset = 0.30f;    // 回落到阈值的 30% 以下才算离开事件
            const float gap = 0.12f;      // 两个事件最小间隔
            const float prom = 0.55f;     // 事件峰值至少要达到 99 分位的 55%，滤掉擦边噪声

            int h = Mathf.Max(1, Mathf.RoundToInt(hop * sampleRate));
            int frames = band.Length / h;
            var onsets = new List<float>();
            if (frames < 4) return onsets;

            var rms = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                double sum = 0.0;
                int start = i * h;
                for (int k = 0; k < h; k++)
                {
                    float v = band[start + k];
                    sum += v * v;
                }
                rms[i] = Mathf.Sqrt((float)(sum / h));
            }

            var sorted = (float[])rms.Clone();
            System.Array.Sort(sorted);
            float p99 = sorted[Mathf.Clamp(Mathf.RoundToInt(sorted.Length * 0.99f), 0, sorted.Length - 1)];
            if (p99 <= 1e-6f) return onsets;

            float threshold = p99 * frac;
            float release = threshold * reset;
            int lookahead = Mathf.Max(1, Mathf.RoundToInt(0.12f / hop));

            bool above = false;
            float last = -99f;

            for (int i = 0; i < frames; i++)
            {
                if (rms[i] > threshold && !above)
                {
                    float t = Mathf.Max(0f, (i - 2) * hop);
                    if (t - last >= gap)
                    {
                        float peak = 0f;
                        int end = Mathf.Min(frames, i + lookahead);
                        for (int k = i; k < end; k++) peak = Mathf.Max(peak, rms[k]);

                        if (peak >= p99 * prom)
                        {
                            onsets.Add(t);
                            last = t;
                        }
                    }
                    above = true;
                }
                else if (rms[i] < release)
                {
                    above = false;
                }
            }

            return onsets;
        }

        /// <summary>分析一个 WAV，返回三个频段的起振时刻（秒）。</summary>
        public static void Analyze(string wavPath, out List<float> low, out List<float> mid, out List<float> high)
        {
            var audio = LoadWav(wavPath);
            low = DetectOnsets(BandLow(audio.samples, audio.sampleRate), audio.sampleRate);
            mid = DetectOnsets(BandMid(audio.samples, audio.sampleRate), audio.sampleRate);
            high = DetectOnsets(BandHigh(audio.samples, audio.sampleRate), audio.sampleRate);
        }

        // ---------------------------------------------------------------- 生成 cue

        /// <summary>把音频分析结果写成 cue，追加进已有时间轴。已有的音频 cue 会先被清掉。</summary>
        public static int BakeInto(CueTable table, string wavPath, out string report)
        {
            List<float> low, mid, high;
            Analyze(wavPath, out low, out mid, out high);

            table.cues.RemoveAll(c => c != null && c.source == "audio");

            int added = 0;

            // 低频 / 底鼓：主灯打一下再回落到基准
            foreach (float t in low)
            {
                for (int i = 0; i < MainLights.Length; i++)
                {
                    Add(table, t, MainLights[i], "intensity", 0.05f, "out", KickPeak, 0f, 0f, 0f);
                    Add(table, t + 0.05f, MainLights[i], "intensity", 0.34f, "out", BaseIntensity, 0f, 0f, 0f);
                    added += 2;
                }
            }

            // 中频 / 军鼓：推进一轮颜色
            for (int i = 0; i < mid.Count; i++)
            {
                Color c = BarColors[i % BarColors.Length];
                for (int k = 0; k < MainLights.Length; k++)
                {
                    Add(table, mid[i], MainLights[k], "color", 0.35f, "smooth", c.r, c.g, c.b, 1f);
                    added++;
                }
            }

            // 高频 / 踩镲：细节灯微闪
            foreach (float t in high)
            {
                Add(table, t, AccentLight, "intensity", 0.04f, "out", AccentPeak, 0f, 0f, 0f);
                Add(table, t + 0.04f, AccentLight, "intensity", 0.14f, "out", AccentBase, 0f, 0f, 0f);
                added += 2;
            }

            table.cues.Sort((a, b) => a.t.CompareTo(b.t));

            report = string.Format(
                "低频 {0} 个→主灯强度，中频 {1} 个→主灯色彩，高频 {2} 个→细节灯，共写入 {3} 条 cue",
                low.Count, mid.Count, high.Count, added);
            return added;
        }

        static void Add(CueTable table, float t, string target, string channel,
                        float duration, string easing, float v0, float v1, float v2, float v3)
        {
            table.cues.Add(new Cue
            {
                t = t,
                target = target,
                channel = channel,
                duration = duration,
                easing = easing,
                v0 = v0,
                v1 = v1,
                v2 = v2,
                v3 = v3,
                source = "audio",
            });
        }

        /// <summary>只重新烘焙音频部分，保留已经编排好的 show cue。</summary>
        public static void BakeFromCommandLine()
        {
            try
            {
                string root = Directory.GetParent(Application.dataPath).FullName;
                string cuePath = Path.Combine(root, CueAssetPath);
                string wavPath = Path.Combine(root, WavAssetPath);

                if (!File.Exists(cuePath)) throw new System.Exception("找不到 cue 表: " + cuePath);
                if (!File.Exists(wavPath)) throw new System.Exception("找不到音频: " + wavPath);

                var table = CueTable.LoadFromFile(cuePath);
                if (table.cues == null) table.cues = new List<Cue>();

                string report;
                int n = BakeInto(table, wavPath, out report);
                table.SaveToFile(cuePath);

                Debug.Log("[AudioCueBaker] " + report);
                EditorApplication.Exit(n > 0 ? 0 : 1);
            }
            catch (System.Exception e)
            {
                Debug.LogError("[AudioCueBaker] 烘焙失败: " + e);
                EditorApplication.Exit(1);
            }
        }
    }
}
