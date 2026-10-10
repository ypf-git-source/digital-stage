using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DigitalPerformance
{
    /// <summary>
    /// 升降台舞台的主循环。和表演那套一样：每帧只由**绝对时间**重算全场高度，
    /// 不做逐帧累加，所以拖进度条、离线预览和现场执行结果一致。
    ///
    /// 语言层不碰这里。它只改写 stage_program.json，由这个脚本读进来求值。
    /// </summary>
    [AddComponentMenu("Digital Performance/Stage Director")]
    public class StageDirector : MonoBehaviour
    {
        [Header("台面几何")]
        public int columns = 6;
        public int rows = 6;
        public float cellSize = 1f;
        public float gap = 0.02f;
        public float radius = 3f;

        [Tooltip("舞台基准面离地面的高度。台体是长柱，顶面在这个高度之上再叠加行程。")]
        public float elevation = 5f;

        [Header("程序")]
        public string programFileName = "stage_program.json";

        [Header("播放")]
        public bool playing = true;
        public bool loop = true;
        public float timeScale = 1f;

        [Tooltip("自动抬高基准面，让程序整体落进行程内。升降台不能往基准面以下走，这一步是必须的。")]
        public bool autoRebase = true;

        [Tooltip("固定求值步长。舞台只在这个步长的整数倍时刻取值。")]
        public float quantum = 1f / 60f;

        [Header("舞台对象（顺序必须和 grid.lifts 一致）")]
        public Transform[] liftTransforms;

        [Header("叶面表皮：铺在台面上的一层连续曲面")]
        public MeshFilter skin;

        [Tooltip("默认关掉：表皮是一张完整圆盘，会盖住台面之间的缝，俯视就看不到方块了。按 T 可以随时开")]
        public bool showSkin = false;
        [Tooltip("表皮比台面顶面抬高多少米，避免台面边角从表皮里戳出来")]
        public float skinOffset = 0.12f;

        [Header("显示")]
        public bool showHud = true;

        [Header("单独控制单块台面")]
        public bool allowDirectControl = true;
        [Tooltip("滚轮推一格抬多少米")]
        public float directStep = 0.05f;
        [Tooltip("手动调整的过渡时长，0.3 秒大约是一个不突兀的跟手速度")]
        public float directDuration = 0.3f;

        [Tooltip("换程序时的交叉淡入时长，0 表示直接切换")]
        public float transitionDuration = 0.5f;

        StageProgram _prevProgram;
        float _blend = 1f;
        float[] _prevHeights;
        float[] _prevGlow;

        StageGrid _grid;
        StageProgram _program;
        StageLimits _limits = new StageLimits();
        float[] _heights;
        float[] _glow;
        MeshRenderer[] _rends;
        MaterialPropertyBlock _block;
        bool[] _blockOn;
        StageViews _views;
        Mesh _skinMesh;
        MeshRenderer _skinRenderer;
        Vector3[] _skinVerts;
        Vector2[] _skinPlan;
        int _selected = -1;
        float _clock;
        string _status = "未加载";
        string _issueText = "未校验";
        List<StageIssue> _issues = new List<StageIssue>();

        public StageGrid Grid { get { return _grid; } }
        public StageProgram Program { get { return _program; } }
        public StageLimits Limits { get { return _limits; } }
        public List<StageIssue> Issues { get { return _issues; } }
        public float CurrentTime { get { return _clock; } }
        public string Status { get { return _status; } }
        public string IssueText { get { return _issueText; } }
        public int SelectedIndex { get { return _selected; } }

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");
        static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

        public float Duration
        {
            get { return _program != null && _program.duration > 0f ? _program.duration : 12f; }
        }

        void Awake()
        {
            Application.targetFrameRate = 60;

            _grid = StageGrid.Create(columns, rows, cellSize, gap, radius);
            _heights = new float[_grid.Count];
            _glow = new float[_grid.Count];
            _prevHeights = new float[_grid.Count];
            _prevGlow = new float[_grid.Count];
            _blockOn = new bool[_grid.Count];
            _limits.minWavelength = cellSize * 2f;
            // MaterialPropertyBlock 是原生对象，只能在 Awake 之后创建
            _block = new MaterialPropertyBlock();
        }

        void Start()
        {
            _rends = new MeshRenderer[_grid.Count];
            if (liftTransforms != null)
            {
                for (int i = 0; i < _grid.Count && i < liftTransforms.Length; i++)
                    if (liftTransforms[i] != null) _rends[i] = liftTransforms[i].GetComponent<MeshRenderer>();
            }

            _views = GetComponent<StageViews>();

            if (skin != null && skin.sharedMesh != null)
            {
                _skinMesh = skin.sharedMesh;
                _skinRenderer = skin.GetComponent<MeshRenderer>();
                _skinVerts = _skinMesh.vertices;
                _skinPlan = new Vector2[_skinVerts.Length];
                for (int i = 0; i < _skinVerts.Length; i++)
                    _skinPlan[i] = new Vector2(_skinVerts[i].x, _skinVerts[i].z);
            }

            LoadProgram();
        }

        public string ProgramPath
        {
            get { return Path.Combine(Application.streamingAssetsPath, programFileName); }
        }

        public void LoadProgram()
        {
            string path = ProgramPath;
            if (!File.Exists(path))
            {
                _status = "没有找到 " + path + "，用预设顶替";
                Debug.LogWarning("[StageDirector] " + _status);
                ApplyProgram(StagePresets.Create(2), false);
                return;
            }

            try
            {
                var prog = StageProgram.LoadFromFile(path);
                ApplyProgram(prog, false);
                _status = "已加载 " + path;
            }
            catch (System.Exception e)
            {
                _status = "程序解析失败：" + e.Message;
                Debug.LogError("[StageDirector] " + _status);
            }
        }

        /// <summary>接受一个程序并立刻校验。语言层和预设都走这里。</summary>
        public void ApplyProgram(StageProgram prog, bool restart)
        {
            if (prog == null) return;

            // 换程序时把旧的留一会儿，做交叉淡入，避免画面硬切
            if (_program != null && _program != prog && transitionDuration > 0.01f && Application.isPlaying)
            {
                _prevProgram = _program;
                _blend = 0f;
            }

            if (prog.layers == null) prog.layers = new List<StageLayer>();
            if (prog.moves == null) prog.moves = new List<StageMove>();
            if (prog.duration <= 0f) prog.duration = 12f;

            _program = prog;
            if (restart) _clock = 0f;

            if (autoRebase)
            {
                float shift = StageEvaluator.AutoRebase(_program, _grid, _limits);
                if (shift > 0f)
                {
                    Debug.Log(string.Format(
                        "[StageDirector] 基准面自动抬高 {0:F2} m，让「{1}」落进 0~{2:F1} m 的行程",
                        shift, _program.title, _limits.maxHeight));
                }
            }

            Revalidate();
            StagePaths.Bake(_program, _grid);

            for (int i = 0; i < _issues.Count; i++)
                Debug.Log("[StageDirector] " + _issues[i]);
        }

        void Revalidate()
        {
            if (_program == null || _grid == null) return;

            _issues = StageValidator.Validate(_program, _grid, _limits);
            int errors = StageValidator.CountErrors(_issues);
            _issueText = _issues.Count == 0
                ? "校验通过，没有超限"
                : string.Format("{0} 个错误 / {1} 个警告", errors, _issues.Count - errors);
        }

        // ---------------------------------------------------------- 单块台面控制

        /// <summary>把某块台面钉在指定高度。写成一条 source=manual 的 set 指令，所以可复现、可写回文件。</summary>
        public void SetManualHeight(LiftInfo li, float height)
        {
            if (_program == null || li == null) return;

            PinLift(_program, li.id, Mathf.Clamp(height, _limits.minHeight, _limits.maxHeight), directDuration);
            Revalidate();
        }

        public void ClearManualFor(string liftId)
        {
            if (_program == null || _program.moves == null) return;
            if (_program.moves.RemoveAll(m => m != null && m.source == "manual" && m.lift == liftId) > 0)
                Revalidate();
        }

        public int ClearAllManual()
        {
            int n = ClearManualMoves(_program);
            if (n > 0) Revalidate();
            return n;
        }

        public void SaveProgram()
        {
            if (_program == null) return;

            try
            {
                _program.SaveToFile(ProgramPath);
                _status = "已写回 " + programFileName + "（含手动改动）";
            }
            catch (System.Exception e)
            {
                _status = "写回失败：" + e.Message;
            }

            Debug.Log("[StageDirector] " + _status);
        }

        public static StageMove FindManualMove(StageProgram prog, string liftId)
        {
            if (prog == null || prog.moves == null) return null;

            for (int i = 0; i < prog.moves.Count; i++)
            {
                var m = prog.moves[i];
                if (m != null && m.source == "manual" && m.lift == liftId) return m;
            }
            return null;
        }

        public static StageMove PinLift(StageProgram prog, string liftId, float height, float duration)
        {
            if (prog == null) return null;
            if (prog.moves == null) prog.moves = new List<StageMove>();

            var m = FindManualMove(prog, liftId);
            if (m == null)
            {
                m = new StageMove
                {
                    lift = liftId,
                    mode = "set",
                    source = "manual",
                    start = 0f,
                    duration = Mathf.Max(0.01f, duration),
                    easing = "smooth",
                };
                prog.moves.Add(m);
            }

            m.height = height;
            return m;
        }

        public static int ClearManualMoves(StageProgram prog)
        {
            if (prog == null || prog.moves == null) return 0;
            return prog.moves.RemoveAll(m => m != null && m.source == "manual");
        }

        public static int CountManualMoves(StageProgram prog)
        {
            if (prog == null || prog.moves == null) return 0;

            int n = 0;
            for (int i = 0; i < prog.moves.Count; i++)
                if (prog.moves[i] != null && prog.moves[i].source == "manual") n++;
            return n;
        }

        void HandleDirect()
        {
            var cam = _views != null ? _views.CameraAt(Input.mousePosition) : Camera.main;
            if (cam == null) return;

            Vector3 hit;
            var hovered = StagePicker.Pick(_grid, _heights, elevation, cam.ScreenPointToRay(Input.mousePosition), out hit);

            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            float scroll = Input.GetAxis("Mouse ScrollWheel");

            if (hovered != null && Input.GetMouseButtonDown(0))
            {
                if (alt) ClearManualFor(hovered.id);
                else _selected = hovered.index;
            }

            if (hovered != null && shift && Mathf.Abs(scroll) > 0.0001f)
            {
                _selected = hovered.index;
                SetManualHeight(hovered, _heights[hovered.index] + scroll * directStep * 10f);
            }
        }

        /// <summary>台面外观：露珠的发光和选中高亮合成到一个 PropertyBlock 里下发。</summary>
        void ApplySurface()
        {
            if (_rends == null) return;

            for (int i = 0; i < _rends.Length; i++)
            {
                var r = _rends[i];
                if (r == null) continue;

                float g = _glow != null && i < _glow.Length ? _glow[i] : 0f;
                bool selected = i == _selected;

                if (g <= 0.01f && !selected)
                {
                    if (_blockOn[i]) { r.SetPropertyBlock(null); _blockOn[i] = false; }
                    continue;
                }

                var mat = r.sharedMaterial;
                if (mat == null) continue;

                _block.Clear();

                if (selected)
                    _block.SetColor(mat.HasProperty(BaseColorId) ? BaseColorId : ColorId,
                                    new Color(1f, 0.78f, 0.25f));

                if (g > 0.01f)
                    _block.SetColor(EmissionId, new Color(0.95f, 1f, 0.86f) * (g * 1.15f));

                r.SetPropertyBlock(_block);
                _blockOn[i] = true;
            }
        }

        public void LoadPreset(int index)
        {
            ApplyProgram(PreserveEffects(StagePresets.Create(index)), true);
            _status = "预设：" + StagePresets.Name(index) + "（效果保留）";
        }

        /// <summary>
        /// 把当前打开的效果层搬到新程序上。演出可以随便换，积水这类效果不会因此丢失。
        /// 如果本来就没有程序，就把全套效果带上，免得切过去是一片空白。
        /// </summary>
        public StageProgram PreserveEffects(StageProgram incoming)
        {
            if (incoming == null) return null;
            if (incoming.layers == null) incoming.layers = new List<StageLayer>();

            for (int i = 0; i < incoming.layers.Count; i++)
            {
                var L = incoming.layers[i];
                if (L != null && string.IsNullOrEmpty(L.group)) L.group = "show";
            }

            if (_program != null && _program.layers != null)
            {
                for (int i = 0; i < _program.layers.Count; i++)
                {
                    var L = _program.layers[i];
                    if (L != null && StageEffects.IsEffect(L.group)) incoming.layers.Add(L);
                }
            }

            return incoming;
        }

        /// <summary>外部（自然语言那一侧）送来的程序只替换演出层，已开的效果保留。</summary>
        public void ApplyShowLayers(StageProgram incoming, bool restart)
        {
            ApplyProgram(PreserveEffects(incoming), restart);
        }

        /// <summary>开/关一个效果。效果定义在代码里，所以关掉之后随时能再打开。</summary>
        public void ToggleEffect(int index)
        {
            if (_program == null || index < 0 || index >= StageEffects.Count) return;

            string id = StageEffects.Ids[index];
            if (StageEffects.Has(_program, id)) StageEffects.Remove(_program, id);
            else StageEffects.Apply(_program, id);

            Revalidate();
            StagePaths.Bake(_program, _grid);

            _status = StageEffects.NameOf(id) + (StageEffects.Has(_program, id) ? " 打开" : " 关闭");
            Debug.Log("[StageDirector] " + _status);
        }

        public void SetAllEffects(bool on)
        {
            if (_program == null) return;

            for (int i = 0; i < StageEffects.Count; i++)
            {
                if (on) StageEffects.Apply(_program, StageEffects.Ids[i]);
                else StageEffects.Remove(_program, StageEffects.Ids[i]);
            }

            Revalidate();
            StagePaths.Bake(_program, _grid);
            _status = on ? "已打开全部效果" : "已关闭全部效果";
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.Space)) playing = !playing;
            if (Input.GetKeyDown(KeyCode.R)) { _clock = 0f; playing = true; }
            if (Input.GetKeyDown(KeyCode.F5)) LoadProgram();
            if (Input.GetKeyDown(KeyCode.T)) showSkin = !showSkin;
            if (Input.GetKeyDown(KeyCode.LeftArrow)) { _clock = Mathf.Max(0f, _clock - 0.25f); playing = false; }
            if (Input.GetKeyDown(KeyCode.RightArrow)) { _clock = Mathf.Min(Duration, _clock + 0.25f); playing = false; }

            for (int i = 0; i < StagePresets.Count; i++)
            {
                if (Input.GetKeyDown(KeyCode.Alpha1 + i)) LoadPreset(i);
            }

            float step = Mathf.Max(quantum, 0.0001f);

            if (playing)
            {
                _clock += Time.deltaTime * timeScale;
                if (_clock >= Duration)
                {
                    _clock = loop ? Mathf.Repeat(_clock, Duration) : Duration;
                    if (!loop) playing = false;
                }
            }

            if (_program == null) return;

            float t = playing ? Mathf.Floor(_clock / step) * step : _clock;
            StageEvaluator.Evaluate(_program, _grid, _limits, t, _heights);
            StageEvaluator.EvaluateGlow(_program, _grid, t, _glow);

            if (_prevProgram != null)
            {
                _blend = Mathf.Min(1f, _blend + Time.deltaTime / Mathf.Max(0.01f, transitionDuration));

                StageEvaluator.Evaluate(_prevProgram, _grid, _limits, t, _prevHeights);
                StageEvaluator.EvaluateGlow(_prevProgram, _grid, t, _prevGlow);

                for (int i = 0; i < _heights.Length; i++)
                {
                    _heights[i] = Mathf.Lerp(_prevHeights[i], _heights[i], _blend);
                    _glow[i] = Mathf.Lerp(_prevGlow[i], _glow[i], _blend);
                }

                if (_blend >= 1f) _prevProgram = null;
            }

            ApplyHeights();
            UpdateSkin(t);
            ApplySurface();

            if (allowDirectControl && Application.isPlaying) HandleDirect();
        }

        void ApplyHeights()
        {
            if (liftTransforms == null) return;

            for (int i = 0; i < _grid.Count && i < liftTransforms.Length; i++)
            {
                var tr = liftTransforms[i];
                if (tr == null) continue;
                var p = _grid.lifts[i].plan;
                tr.localPosition = new Vector3(p.x, elevation + _heights[i], p.y);
            }
        }

        /// <summary>
        /// 把连续高度场写进表皮网格。台面只采样了它中心那一点，这里采的是它们之间的连续曲面，
        /// 所以缝没了、法线平滑了，看起来才是一整片在弯曲的叶子。
        /// </summary>
        void UpdateSkin(float t)
        {
            if (_skinMesh == null || _skinVerts == null) return;

            if (_skinRenderer != null && _skinRenderer.enabled != showSkin)
                _skinRenderer.enabled = showSkin;

            if (!showSkin || _program == null) return;

            for (int i = 0; i < _skinVerts.Length; i++)
            {
                float h = StageEvaluator.HeightAt(_program, _grid, _skinPlan[i], t, true);
                h = Mathf.Clamp(h, _limits.minHeight, _limits.maxHeight);
                _skinVerts[i].y = h + elevation + skinOffset;
            }

            _skinMesh.vertices = _skinVerts;
            _skinMesh.RecalculateNormals();
            _skinMesh.RecalculateBounds();
        }

        void OnGUI()
        {
            if (!showHud || !StageViews.HudVisible) return;

            float w = Mathf.Min(Screen.width - 24f, 430f);
            GUILayout.BeginArea(new Rect(12f, 12f, w, 258f), GUI.skin.box);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(playing ? "Pause" : "Play", GUILayout.Width(64f))) playing = !playing;
            if (GUILayout.Button("Replay", GUILayout.Width(64f))) { _clock = 0f; playing = true; }
            if (GUILayout.Button("Reload", GUILayout.Width(72f))) LoadProgram();
            GUILayout.Label(string.Format("t = {0:F2} / {1:F1} s   {2}", _clock, Duration,
                _program != null ? _program.title : "-"));
            GUILayout.EndHorizontal();

            float s = GUILayout.HorizontalSlider(_clock, 0f, Duration);
            if (Mathf.Abs(s - _clock) > 0.001f)
            {
                _clock = Mathf.Clamp(s, 0f, Duration);
                playing = false;
            }

            string pair;
            float stepNow = StageEvaluator.MaxAdjacentStep(_grid, _heights, out pair);
            GUILayout.Label(string.Format("相邻高差最大 {0:F2} m ({1})   约束 {2}",
                stepNow, pair, _issueText));
            GUILayout.Label(string.Format(
                "[Space] 播放  [R] 重播  [<- ->] 拖动  1-7 预设  [V] 视角：{0}  [T] 表皮：{1}  [H] 收面板",
                _views != null ? _views.ModeName : "无", showSkin ? "开" : "关"));

            string sel = _selected >= 0 && _selected < _grid.Count
                ? string.Format("{0}  高度 {1:F2} m", _grid.lifts[_selected].id, _heights[_selected])
                : "未选中";
            GUILayout.Label(string.Format("单独控制：{0}    手动锁定 {1} 块",
                sel, CountManualMoves(_program)));
            GUILayout.Label("[点击] 选中  [Shift+滚轮] 抬升/下降  [Alt+点击] 解除这块");

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("清除全部手动", GUILayout.Width(110f)))
            {
                int n = ClearAllManual();
                _status = n > 0 ? "已清除 " + n + " 处手动改动" : "没有手动改动";
            }
            if (GUILayout.Button("写回 json", GUILayout.Width(88f))) SaveProgram();
            if (GUILayout.Button("效果全开", GUILayout.Width(84f))) SetAllEffects(true);
            if (GUILayout.Button("效果全关", GUILayout.Width(84f))) SetAllEffects(false);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("效果", GUILayout.Width(34f));
            for (int i = 0; i < StageEffects.Count; i++)
            {
                bool on = StageEffects.Has(_program, StageEffects.Ids[i]);
                var prev = GUI.backgroundColor;
                if (on) GUI.backgroundColor = new Color(1f, 0.78f, 0.25f);
                if (GUILayout.Button((on ? "● " : "○ ") + StageEffects.Names[i], GUILayout.Width(70f)))
                    ToggleEffect(i);
                GUI.backgroundColor = prev;
            }
            GUILayout.EndHorizontal();

            GUILayout.EndArea();
        }
    }
}
