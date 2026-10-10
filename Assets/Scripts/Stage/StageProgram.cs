using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DigitalPerformance
{
    /// <summary>
    /// 一个图层。多个图层按高度**相加**——这是升降台舞台和 cue 表最大的区别：
    /// cue 是顺序覆盖，图层是同时叠加。所以"缓慢整体倾斜 + 一道行波 + 一点随机涌动"
    /// 三件事可以同时成立，而不会互相把对方覆盖掉。
    /// </summary>
    [Serializable]
    public class StageLayer
    {
        public string type = "plane";     // plane | wave | ring | radial | steps | noise | drop | ruffle | twist
        public string channel = "height"; // height = 影响台面高度；glow = 只让台面发光
        public string group = "show";      // show = 演出层；其余是效果层的标签（pool / wind / drops …）
        public string scope = "all";      // all / ring:2 / disk:2.5 / row:3 / col:4 / quadrant:1 / corner:3
        public float start = 0f;
        public float fade_in = 1f;
        public float hold = 4f;
        public float fade_out = 1f;       // <= 0 表示一直保持
        public string easing = "smooth";

        public float tilt_x;              // plane：每米抬高多少
        public float tilt_y;
        public float offset;              // plane：整体抬高

        public float direction_deg;       // wave / steps：传播方向
        public float wavelength = 4f;     // wave / ring：空间波长（米）
        public float amplitude = 0.3f;    // 幅度（米）
        public float speed = 1.0f;        // wave / ring：相位速度（米/秒）

        public float center_x;            // radial：中心
        public float center_y;
        public float radius = 1.5f;       // radial / ring：影响半径
        public string falloff = "smooth"; // radial：smooth | linear

        public float step = 1f;           // steps：每级高差对应的水平距离
        public int seed = 1;              // noise：随机种子，必须固定才能复现

        public int veins = 12;            // ruffle / twist：一圈有多少个波瓣
        public float band = 0.6f;         // ruffle：褶皱沿半径的宽度；drop：尾迹的衰减长度
        public float wake = 0f;           // drop：尾迹涟漪幅度
        public float sharp = 1f;          // drop：核心锐度，越大越像一颗硬珠子
        public float flicker = 0f;        // drop：闪光频率（赫兹）。只建议用在 glow 通道上
        public float jitter = 0f;         // 相位抖动（弧度）：让相邻位置不同步，运动才不像机械波
        public float gust = 0f;           // 阵风：幅度随时间缓慢起伏
        public float accel = 0f;          // drop：沿轨迹的加速度
    }

    /// <summary>对单块台子的直接指令，用来覆盖或微调。</summary>
    [Serializable]
    public class StageMove
    {
        public string lift = "r3c3";
        public string mode = "set";       // set = 覆盖到该高度；add = 在图层之上再叠加
        public float height = 0.5f;
        public float start = 0f;
        public float duration = 1f;
        public string easing = "smooth";

        // "show" = 编排里写好的， "manual" = 运行时手动点选调出来的。
        // 有了它，"清掉所有手动改动"才有一条干净的界线。
        public string source = "show";
    }

    /// <summary>整场舞台的中间表示。语言层唯一允许产出的东西。</summary>
    [Serializable]
    public class StageProgram
    {
        public int version = 1;
        public string title = "untitled";
        public float duration = 12f;
        public bool loop = true;
        public float base_height = 0f;
        public List<StageLayer> layers = new List<StageLayer>();
        public List<StageMove> moves = new List<StageMove>();

        public static StageProgram LoadFromFile(string path)
        {
            return JsonUtility.FromJson<StageProgram>(File.ReadAllText(path));
        }

        public void SaveToFile(string path)
        {
            File.WriteAllText(path, JsonUtility.ToJson(this, true));
        }
    }

    /// <summary>机械行程与安全上限。语言层可以随便提要求，最后都要被这组数字夹住。</summary>
    public class StageLimits
    {
        public float minHeight = 0f;          // 台面最低，0 = 基准面
        public float maxHeight = 2f;          // 行程上限
        public float maxSpeed = 0.8f;         // 单台最大速度 m/s，按真实机型的数据表填
        public float maxAdjacentStep = 0.6f;  // 相邻台面最大高差，超过人就迈不上去了
        public float minWavelength = 2f;      // 空间波长下限（米），短于此会混叠成噪点
    }
}
