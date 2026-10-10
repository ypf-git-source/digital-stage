using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

namespace DigitalPerformance
{
    /// <summary>
    /// 自然语言 → 舞台程序。
    ///
    /// 两条路，产物是同一个 StageProgram，走同一套校验：
    ///   1. 配了 API key 和模型名：调大模型，让它按 JSON 模式直接吐出高度场程序；
    ///   2. 没配：走本地规则解析，保证演示和自检不依赖外部服务。
    ///
    /// 这里最关键的一条设计：**模型只能产出中间表示，不能直接驱动机械**。
    /// 它写出来的程序一律要过 StageValidator——行程、速度、相邻高差超限会被拦下来。
    /// 所以 prompt 里不是"随便画个好看的形状"，而是把机械约束一起写进去了。
    /// </summary>
    [AddComponentMenu("Digital Performance/NL Stage Bridge")]
    public class NLStageBridge : MonoBehaviour
    {
        [Header("引用")]
        public StageDirector director;

        [Header("API")]
        public string endpoint = "https://api.openai.com/v1/chat/completions";
        public string model = "";
        public string apiKeyEnvVar = "OPENAI_API_KEY";
        [Tooltip("也可以直接填，但不要提交进版本库")]
        public string apiKey = "";
        public int timeoutSeconds = 30;
        public float temperature = 0.2f;
        public bool preferApi = true;

        [Header("状态")]
        public string lastInput = "";
        public string lastResult = "";

        [Header("界面")]
        public bool showPanel = true;

        string _input = "";
        static readonly string[] QuickPhrases =
        {
            "波浪", "倾斜", "圆顶", "涟漪", "台阶", "涌动", "复位",
        };

        // ------------------------------------------------------------ 对外接口

        public bool HasApiKey { get { return !string.IsNullOrEmpty(ResolveKey()); } }
        public bool Ready { get { return preferApi && HasApiKey && !string.IsNullOrEmpty(model); } }

        public void Submit(string text)
        {
            StopAllCoroutines();
            StartCoroutine(SubmitRoutine(text));
        }

        // ------------------------------------------------------------ 主流程

        IEnumerator SubmitRoutine(string text)
        {
            lastInput = text ?? "";
            lastResult = "处理中…";

            if (Ready)
            {
                yield return StartCoroutine(SubmitViaApi(lastInput));
                yield break;
            }

            Apply(StageFromRules(lastInput), HasApiKey ? "本地规则（未设置模型名）" : "本地规则（未配置 API key）");
        }

        IEnumerator SubmitViaApi(string text)
        {
            string body = BuildRequestBody(text);

            var req = new UnityWebRequest(endpoint, UnityWebRequest.kHttpVerbPOST);
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("Authorization", "Bearer " + ResolveKey());
            req.timeout = Mathf.Max(5, timeoutSeconds);

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                string detail = req.downloadHandler != null ? Short(req.downloadHandler.text) : "";
                Debug.LogWarning("[NLStageBridge] API 失败：" + req.error + " " + detail);
                Apply(StageFromRules(text), "本地规则（API 失败：" + req.error + "）");
                yield break;
            }

            string content = null;
            try
            {
                var resp = JsonUtility.FromJson<ChatResponse>(req.downloadHandler.text);
                if (resp != null && resp.error != null && !string.IsNullOrEmpty(resp.error.message))
                {
                    Debug.LogWarning("[NLStageBridge] 服务返回错误：" + resp.error.message);
                }
                else if (resp != null && resp.choices != null && resp.choices.Count > 0 &&
                         resp.choices[0].message != null)
                {
                    content = resp.choices[0].message.content;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[NLStageBridge] 响应解析失败：" + e.Message);
            }

            if (string.IsNullOrEmpty(content))
            {
                Apply(StageFromRules(text), "本地规则（响应里没有可用的 JSON）");
                yield break;
            }

            StageProgram prog = null;
            try
            {
                prog = JsonUtility.FromJson<StageProgram>(ExtractJson(content));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[NLStageBridge] 模型输出不是合法程序：" + e.Message);
            }

            if (prog == null)
            {
                Debug.LogWarning("[NLStageBridge] 模型原始输出：" + Short(content));
                Apply(StageFromRules(text), "本地规则（模型输出无法解析）");
                yield break;
            }

            Apply(prog, "API 返回");
        }

        void Apply(StageProgram prog, string source)
        {
            if (prog == null)
            {
                lastResult = source + "：没有拿到程序";
                return;
            }

            // 只替换演出层：已经打开的积水、风、露珠这类效果不会被冲掉
            if (director != null) director.ApplyShowLayers(prog, true);

            List<StageIssue> issues = director != null && director.Grid != null
                ? StageValidator.Validate(prog, director.Grid, director.Limits)
                : new List<StageIssue>();

            int errors = StageValidator.CountErrors(issues);

            try
            {
                prog.SaveToFile(Path.Combine(Application.streamingAssetsPath, "stage_program.json"));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[NLStageBridge] 程序落盘失败：" + e.Message);
            }

            lastResult = string.Format("{0}：{1}｜图层 {2}，单台指令 {3}，{4}",
                source, prog.title, prog.layers.Count, prog.moves.Count,
                errors == 0 ? "校验通过" : errors + " 项超限");

            Debug.Log("[NLStageBridge] " + lastResult);
            for (int i = 0; i < issues.Count; i++)
                Debug.Log("[NLStageBridge] " + issues[i]);
        }

        // ------------------------------------------------------------ 请求构造

        string ResolveKey()
        {
            if (!string.IsNullOrEmpty(apiKey)) return apiKey;
            return Environment.GetEnvironmentVariable(apiKeyEnvVar);
        }

        static string ExtractJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            int a = s.IndexOf('{');
            int b = s.LastIndexOf('}');
            return a >= 0 && b > a ? s.Substring(a, b - a + 1) : s;
        }

        static string Short(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= 400 ? s : s.Substring(0, 400) + "…";
        }

        void OnGUI()
        {
            if (!showPanel || !StageViews.HudVisible) return;

            float w = Mathf.Min(Screen.width - 24f, 430f);
            GUILayout.BeginArea(new Rect(12f, 282f, w, 140f), GUI.skin.box);

            GUILayout.Label(Ready
                ? "自然语言（已配置 API）"
                : "自然语言（本地规则兜底，没有配置 API key 或模型名）");

            GUILayout.BeginHorizontal();
            _input = GUILayout.TextField(_input ?? "");
            if (GUILayout.Button("下发", GUILayout.Width(56f)) && !string.IsNullOrEmpty(_input))
                Submit(_input);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            for (int i = 0; i < QuickPhrases.Length; i++)
            {
                if (GUILayout.Button(QuickPhrases[i], GUILayout.Width(64f)))
                {
                    _input = QuickPhrases[i];
                    Submit(_input);
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.Label(string.IsNullOrEmpty(lastResult) ? "等待输入…" : lastResult);
            GUILayout.EndArea();
        }

        string BuildRequestBody(string text)
        {
            var req = new ChatRequest
            {
                model = model,
                temperature = temperature,
                messages = new List<ChatTurn>
                {
                    new ChatTurn { role = "system", content = SystemPrompt },
                    new ChatTurn { role = "user", content = text },
                },
            };
            return JsonUtility.ToJson(req);
        }

        /// <summary>提示词里的阵列参数直接从运行时网格读，换 6×6 / 8×8 不用改这里。</summary>
        string SystemPrompt
        {
            get
            {
                int cols = 8, rows = 8;
                float radius = 4f, maxH = 2f, minWave = 2f, maxStep = 0.6f, maxSpeed = 0.8f;

                if (director != null && director.Grid != null)
                {
                    cols = director.Grid.columns;
                    rows = director.Grid.rows;
                    radius = director.Grid.radius;
                    maxH = director.Limits.maxHeight;
                    minWave = director.Limits.minWavelength;
                    maxStep = director.Limits.maxAdjacentStep;
                    maxSpeed = director.Limits.maxSpeed;
                }

                return PromptTemplate
                    .Replace("__COLS__", cols.ToString())
                    .Replace("__ROWS__", rows.ToString())
                    .Replace("__COUNT__", (cols * rows).ToString())
                    .Replace("__DIAM__", (radius * 2f).ToString("0.#"))
                    .Replace("__RADIUS__", radius.ToString("0.#"))
                    .Replace("__MAXH__", maxH.ToString("0.#"))
                    .Replace("__MINWAVE__", minWave.ToString("0.#"))
                    .Replace("__MAXSTEP__", maxStep.ToString("0.#"))
                    .Replace("__MAXSPEED__", maxSpeed.ToString("0.#"));
            }
        }

        const string PromptTemplate = @"你是舞台机械指令编译器。用户用中文描述想要的舞台效果，你把它翻译成一个 JSON 高度场程序。

【舞台】
__COLS__x__ROWS__ 的方格阵列，拼成直径 __DIAM__ 米的圆形台面（半径 __RADIUS__ 米；完全落在圆外的角块不存在，所以实际台面数少于 __COUNT__ 块）。
坐标原点在舞台中心，x 向右、y 向后，单位米。
台面编号 r{行}c{列}：行 1 在后排、行 __ROWS__ 在前排；列 1 在最左、列 __COLS__ 在最右。
每块行程 0 到 __MAXH__ 米，0 是基准面。
台面是离散的，不是连续曲面：空间波长小于 __MINWAVE__ 米、或者在 1 米内做出两级台阶，看起来都会像噪点，不要这样写。

【输出】
只输出一个 JSON 对象，不要解释、不要代码块标记。字段：
title(字符串) / duration(秒) / loop(布尔) / base_height(米) / layers(数组) / moves(数组)

【图层 layers】多个图层的高度是相加的，可以同时叠加：
plane   倾斜平面   tilt_x, tilt_y（每米抬高多少米）, offset（整体抬高，米）
wave    行波       direction_deg（传播方向，0 表示沿 +x）, wavelength（波长，米，必须大于等于 2）, amplitude（米）, speed（米每秒）
ring    同心涟漪   radius（起始半径，米）, wavelength, amplitude, speed
radial  圆顶或碗   center_x, center_y, radius（影响半径，米）, amplitude（正数凸起、负数下凹）, falloff（smooth 或 linear）
steps   台阶       direction_deg, step（每级水平距离，米，建议大于等于 1）, amplitude（每级高差，米）
noise   随机涌动   amplitude, speed, seed（整数，必须写死）
drop    露珠滚过   center_x, center_y（起点，米）, direction_deg（滚落方向）, speed（米每秒）, radius（水珠影响半径，米）, amplitude（米）
ruffle  叶缘褶皱   radius（褶皱所在的半径，米）, band（沿半径的宽度，米）, veins（一圈几个波瓣）, amplitude（米）, speed（转得多快）
twist   绕茎扭转   radius（归一化半径，米）, veins（波瓣数，1 就是整体扭转）, amplitude（米）, speed（弧度每秒）

每个图层还可以指定 channel：height 影响台面高度（默认），glow 只让台面亮起来、不改变高度。
做露珠滚落时典型的写法是同一轨迹配两条 drop：一条 channel=height 抬起一点，一条 channel=glow 做高光。

每个图层还要给时间包络：start（开始秒）、fade_in、hold、fade_out（秒，0 表示一直保持）、easing（linear/smooth/in/out/inout）
以及作用范围 scope：all / ring:0 或 1 或 2 / disk:半径 / row:1..__ROWS__ / col:1..__COLS__ / quadrant:1..4 / corner:1..4

【单台指令 moves】可选，用来强调某几块：
[{""lift"":""r3c3"",""mode"":""set"",""height"":1.2,""start"":2.0,""duration"":1.5,""easing"":""smooth""}]
mode 用 set 表示覆盖到该高度，add 表示在图层之上再叠加。

【硬约束，必须满足】
所有图层叠加后，任意时刻任意台面都必须在 0 到 __MAXH__ 米之间
相邻台面高差不超过 __MAXSTEP__ 米
单台速度不超过 __MAXSPEED__ 米每秒
空间波长不小于 __MINWAVE__ 米
所有长度参数用米，不要用百分比

【示例】用户说：让舞台从中间慢慢鼓起一个圆顶，然后散开成涟漪
{""title"":""中心隆起后扩散成涟漪"",""duration"":12,""loop"":true,""base_height"":0,""layers"":[{""type"":""radial"",""scope"":""all"",""center_x"":0,""center_y"":0,""radius"":2.6,""amplitude"":0.8,""falloff"":""smooth"",""start"":0.5,""fade_in"":1.5,""hold"":3,""fade_out"":2,""easing"":""smooth""},{""type"":""ring"",""scope"":""all"",""radius"":0,""wavelength"":2.4,""amplitude"":0.3,""speed"":2.2,""start"":5,""fade_in"":1,""hold"":4,""fade_out"":2,""easing"":""smooth""}]}";

        // ------------------------------------------------------------ 本地规则

        /// <summary>
        /// 没配 API 时的兜底。覆盖常见说法，产物同样是 StageProgram。
        /// 它同时也是一份"语言 → 参数"的对照表，接大模型时可以直接当 few-shot 参考。
        /// </summary>
        public static StageProgram StageFromRules(string text)
        {
            string s = text ?? "";
            var p = new StageProgram { title = s, duration = 12f, loop = true, base_height = 0f };

            if (Has(s, "复位", "归零", "放平", "降下", "停下", "停止", "静止"))
            {
                p.title = "全部回到基准面";
                return p;
            }

            float amp = Mathf.Clamp(NumberAfter(s, new[] { "抬高", "幅度", "高", "起伏", "差" },
                                     NumberBeforeUnit(s, "米", 0.35f)), 0.08f, 0.85f);
            float wavelength = Mathf.Clamp(NumberAfter(s, new[] { "波长", "波距" }, 4f), 2f, 16f);
            float speed = Mathf.Clamp(NumberAfter(s, new[] { "速度", "每秒" }, 1.2f), 0.2f, 3f);
            float radius = Mathf.Clamp(NumberAfter(s, new[] { "半径", "范围" }, 2.5f), 0.6f, 3.5f);
            float dir = Direction(s);

            StageLayer L;

            if (Has(s, "整体抬高", "整体上升", "整体升起", "整体抬升"))
                L = new StageLayer { type = "plane", offset = amp };
            else if (Has(s, "涟漪", "同心", "一圈圈"))
                L = new StageLayer { type = "ring", radius = 0f, wavelength = wavelength, amplitude = amp, speed = speed };
            else if (Has(s, "圆顶", "穹顶", "鼓起", "隆起", "凸起", "中间升", "中心升", "小山", "拱"))
                L = new StageLayer { type = "radial", amplitude = Mathf.Abs(amp), radius = radius };
            else if (Has(s, "碗", "凹陷", "下沉", "凹下", "中心降"))
                L = new StageLayer { type = "radial", amplitude = -Mathf.Abs(amp), radius = radius };
            else if (Has(s, "台阶", "阶梯", "楼梯"))
                L = new StageLayer { type = "steps", direction_deg = dir, step = 1f, amplitude = amp * 0.6f };
            else if (Has(s, "随机", "沸腾", "翻滚", "涌动", "乱"))
                L = new StageLayer { type = "noise", amplitude = amp, speed = speed * 0.6f, seed = 20261009 };
            else if (Has(s, "跷跷板", "摇摆", "晃动", "摆动"))
                L = new StageLayer { type = "wave", direction_deg = dir, wavelength = 24f, amplitude = amp, speed = speed };
            else if (Has(s, "倾斜", "斜", "一边高"))
                L = new StageLayer
                {
                    type = "plane",
                    tilt_x = amp / 3f * Mathf.Cos(dir * Mathf.Deg2Rad),
                    tilt_y = amp / 3f * Mathf.Sin(dir * Mathf.Deg2Rad),
                };
            else
                L = new StageLayer
                {
                    type = "wave", direction_deg = dir,
                    wavelength = wavelength, amplitude = amp * 0.8f, speed = speed,
                };

            L.scope = Scope(s);
            L.easing = "smooth";
            L.start = 0.5f;
            L.hold = 7f;
            ClampForSpeed(L);
            p.layers.Add(L);

            return p;
        }

        /// <summary>
        /// 把参数拉回机械跟得上的范围。
        ///
        /// 这里有个容易忽略的耦合：行波"快不快"不看相位速度，而看**任意一块台面每秒要移动多少米**：
        ///     变化率 ≈ 幅度 × (2π / 波长) × 相位速度
        /// 波长越短、幅度越大，同一个看起来不快的相位速度也会让台面来不及跟。
        /// 淡入淡出同样算运动，平滑包络的峰值斜率是 1.5，也要算进预算。
        /// </summary>
        static void ClampForSpeed(StageLayer L)
        {
            const float budget = 0.60f;    // 单层速度预算 m/s
            const float envelope = 0.30f;  // 留给淡入淡出的份额
            const float maxFade = 3f;

            // 台阶的等效幅度是"每级高差 × 级数"：最远那一列能到 ±3 级，
            // 所以真正要付速度代价的是 3 倍每级高差，不是每级高差本身。
            float levelFactor = 1f;
            if (L.type == "steps")
                levelFactor = Mathf.Max(1f, Mathf.Ceil(3f / Mathf.Max(L.step, 0.05f)));

            float a = Mathf.Abs(L.amplitude) * levelFactor;

            // 幅度先限死，否则光靠淡入淡出就已经超速
            float maxA = maxFade * envelope / 1.5f;
            if (a > maxA)
            {
                L.amplitude *= maxA / a;
                a = maxA;
            }

            float room = Mathf.Max(0.05f, budget - envelope);

            if (L.type == "wave" || L.type == "ring")
            {
                float k = 2f * Mathf.PI / Mathf.Max(L.wavelength, 0.5f);
                float cap = room / Mathf.Max(1e-4f, a * k);
                L.speed = Mathf.Clamp(L.speed, 0.05f, Mathf.Max(0.05f, cap));
            }
            else if (L.type == "noise")
            {
                float cap = room / Mathf.Max(1e-4f, a * 3.4f);
                L.speed = Mathf.Clamp(L.speed, 0.05f, Mathf.Max(0.05f, cap));
            }

            float fade = Mathf.Clamp(1.5f * a / envelope, 1f, maxFade);
            L.fade_in = fade;
            L.fade_out = fade;
        }

        static bool Has(string s, params string[] keys)
        {
            for (int i = 0; i < keys.Length; i++)
                if (s.IndexOf(keys[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        static float NumberAfter(string s, string[] keys, float fallback)
        {
            for (int i = 0; i < keys.Length; i++)
            {
                int at = s.IndexOf(keys[i], StringComparison.Ordinal);
                if (at < 0) continue;

                var m = Regex.Match(s.Substring(at + keys[i].Length), @"^[^\d]{0,4}([0-9]+(?:\.[0-9]+)?)");
                if (m.Success)
                {
                    float v;
                    if (float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                        return v;
                }
            }
            return fallback;
        }

        static float NumberBeforeUnit(string s, string unit, float fallback)
        {
            var m = Regex.Match(s, @"([0-9]+(?:\.[0-9]+)?)\s*" + unit);
            if (m.Success)
            {
                float v;
                if (float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                    return v;
            }
            return fallback;
        }

        static float Direction(string s)
        {
            if (Has(s, "从右到左", "向左")) return 180f;
            if (Has(s, "从后到前", "向前", "往前")) return 270f;
            if (Has(s, "从前到后", "向后", "往后")) return 90f;
            return 0f;   // 默认从左到右
        }

        static string Scope(string s)
        {
            if (Has(s, "外圈", "边缘", "周边", "边上一圈")) return "ring:2";
            if (Has(s, "中间", "中央", "中心区域")) return "disk:2";
            if (Has(s, "左上角")) return "corner:1";
            if (Has(s, "右上角")) return "corner:2";
            if (Has(s, "左下角")) return "corner:3";
            if (Has(s, "右下角")) return "corner:4";
            return "all";
        }

        // ------------------------------------------------------------ 请求/响应模型

        [Serializable] public class ChatTurn
        {
            public string role;
            public string content;
        }

        [Serializable] public class ResponseFormat
        {
            public string type = "json_object";
        }

        [Serializable] public class ChatRequest
        {
            public string model;
            public float temperature = 0.2f;
            public bool stream = false;
            public ResponseFormat response_format = new ResponseFormat();
            public List<ChatTurn> messages = new List<ChatTurn>();
        }

        [Serializable] public class ChatMessage
        {
            public string role;
            public string content;
        }

        [Serializable] public class ChatChoice
        {
            public ChatMessage message;
        }

        [Serializable] public class ApiError
        {
            public string message;
            public string type;
        }

        [Serializable] public class ChatResponse
        {
            public List<ChatChoice> choices;
            public ApiError error;
        }
    }
}
