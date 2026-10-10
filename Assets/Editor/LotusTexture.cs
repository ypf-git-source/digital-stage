using System.IO;
using UnityEditor;
using UnityEngine;

namespace DigitalPerformance.EditorTools
{
    /// <summary>
    /// 程序生成荷叶的两张贴图：颜色和法线。
    ///
    /// 两张图来自**同一个高度场**——极坐标下的主脉、次级脉、边缘起伏。
    /// 法线只要对高度求一次梯度就有，所以叶脉在颜色上是暗线、在光照下也是真的凹下去的。
    /// 叶脉在极坐标里算，配合"顶面 UV 用舞台坐标归一化"，叶脉跨台面也是连续的。
    /// </summary>
    public static class LotusTexture
    {
        public static void Generate(string colorPath, string normalPath, int size, int veins,
                                    Color center, Color edge, int seed, float normalStrength)
        {
            int count = size * size;
            var height = new float[count];
            var color = new Color32[count];
            // ---------------- 高度场 + 颜色 ----------------

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int i = y * size + x;
                    float u = (x + 0.5f) / size * 2f - 1f;
                    float v = (y + 0.5f) / size * 2f - 1f;
                    float r = Mathf.Sqrt(u * u + v * v);

                    if (r > 1f)
                    {
                        height[i] = 0f;
                        color[i] = new Color32(0, 0, 0, 0);
                        continue;
                    }

                    float vein, blade, mottle;
                    float h = Sample(u, v, veins, seed, out vein, out blade, out mottle);
                    height[i] = h;

                    // 叶面上不留任何噪点：斑驳在法线里会形成柔和的波纹状明暗
                    Color c = Color.Lerp(center, edge, Edge(0f, 1f, r) * 0.50f);
                    c *= 1f - 0.10f * Edge(0.955f, 1f, r);   // 叶缘压深要窄而轻，不然像一圈黑描边

                    c.a = 1f;
                    color[i] = c;
                }
            }

            WriteTexture(colorPath, size, color);

            // ---------------- 由高度梯度求法线 ----------------

            var normal = new Color32[count];
            float scale = size * 0.5f;   // 把高度梯度换算到贴图尺度

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int i = y * size + x;
                    if (color[i].a == 0)
                    {
                        normal[i] = new Color32(128, 128, 255, 255);
                        continue;
                    }

                    int xl = (x - 1 + size) % size;
                    int xr = (x + 1) % size;
                    int yd = (y - 1 + size) % size;
                    int yu = (y + 1) % size;

                    float dx = (height[y * size + xr] - height[y * size + xl]) * 0.5f * scale;
                    float dy = (height[yu * size + x] - height[yd * size + x]) * 0.5f * scale;

                    var n = new Vector3(-dx * normalStrength, -dy * normalStrength, 1f).normalized;
                    normal[i] = new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f);
                }
            }

            WriteTexture(normalPath, size, normal);
        }

        /// <summary>
        /// 一圈等间距的细棱。count 是这一圈有多少条，offset 用来把子级分支挪到父级之间，
        /// exponent 越大线越细。
        /// </summary>
        static float Ridge(float phase, int count, float offset, float exponent)
        {
            float t = Mathf.Repeat(phase * count / (2f * Mathf.PI) + offset, 1f);
            return Mathf.Pow(Mathf.Abs(t - 0.5f) * 2f, exponent);
        }

        /// <summary>供自检用的采样入口：只要高度。</summary>
        public static float SampleHeight(float u, float v, int veins, int seed)
        {
            float vein, blade, mottle;
            return Sample(u, v, veins, seed, out vein, out blade, out mottle);
        }

        /// <summary>
        /// 叶面高度场。u、v 取 -1..1，原点在叶心。
        ///
        /// 按需求已去掉全部叶脉，只留下叶肉自身的两档起伏（大尺度缓坡 + 细颗粒）和叶缘抬升。
        /// 保留这点起伏是为了不让叶面变成一块纯色塑料板——完全平坦反而更假。
        /// 叶脉那套（蜿蜒、逐级分叉）的实现留在注释里备查：需要时可以再开。
        /// </summary>
        static float Sample(float u, float v, int veins, int seed,
                            out float vein, out float blade, out float mottle)
        {
            float r = Mathf.Sqrt(u * u + v * v);

            mottle = StageEvaluator.ValueNoise(new Vector2(u * 5f, v * 5f), seed + 7);
            float broad = StageEvaluator.ValueNoise(new Vector2(u * 2.2f, v * 2.2f), seed + 31);

            vein = 0f;
            blade = 1f;

            // 叶面是平的：之前这里有大尺度缓坡和细颗粒两层噪声，
            // 它们在法线上形成柔和的波浪状明暗，按需求全部去掉，只留叶缘轻微上翘。
            float h = 0f;
            h += 0.10f * Edge(0.94f, 1f, r);
            return h;

            // 需要恢复叶脉时，把下面这段接回 h 里即可（更早的版本就是这么做的）：
            //   swirl = ang + 0.35r² + 0.10·sin(3ang + 2.6r) + 0.05·sin(6ang − 1.9r)
            //   envA = 1 − Edge(0.55, 0.98, r)     主脉，18 条，从叶心出发
            //   envB = Edge(0.26, 0.52, r)         一级分支，36 条，r≈0.3 淡入
            //   envC = Edge(0.62, 0.92, r)         二级分支，72 条，r≈0.7 淡入
            //   vA = Ridge(swirl, veins,   0.0, Lerp(12,16,r)) · envA
            //   vB = Ridge(swirl, veins*2, 0.5, 13) · envB
            //   vC = Ridge(swirl, veins*4, 0.5, 15) · envC
            //   h += 0.55·vA + 0.30·vB + 0.26·vC
            //   h −= 0.10 · (1 − 2.4(vA+vB)) · Edge(0.12, 0.85, r)   叶肉绗缝
            // 角向扰动必须是慢调制（角向导数之和明显小于 1），否则叶脉会自我纠缠。
        }

        /// <summary>
        /// 边缘平滑（GLSL 的 smoothstep 语义）。
        ///
        /// 注意别用 Unity 的 Mathf.SmoothStep：那是在 from 和 to 之间按 t 插值，
        /// 拿它当边缘平滑用会让包络完全变样——我在这里踩过一次，
        /// 结果是主脉在叶心就被削弱、边缘抬升从叶心就开始生效，看上去就像西瓜的径向条纹。
        /// </summary>
        static float Edge(float a, float b, float x)
        {
            float t = Mathf.Clamp01((x - a) / Mathf.Max(1e-5f, b - a));
            return t * t * (3f - 2f * t);
        }

        static void WriteTexture(string assetPath, int size, Color32[] pixels)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true);
            tex.SetPixels32(pixels);
            tex.Apply();

            byte[] png = tex.EncodeToPNG();
            Object.DestroyImmediate(tex);

            string absolute = Path.Combine(Directory.GetParent(Application.dataPath).FullName, assetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute));
            File.WriteAllBytes(absolute, png);

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null) return;

            importer.wrapMode = TextureWrapMode.Clamp;
            importer.mipmapEnabled = true;
            importer.anisoLevel = 4;
            importer.alphaIsTransparency = true;

            // 法线贴图要按法线类型导入，否则颜色空间会被当成 sRGB 处理
            importer.textureType = assetPath.Contains("Normal")
                ? TextureImporterType.NormalMap
                : TextureImporterType.Default;

            importer.SaveAndReimport();
        }
    }
}
