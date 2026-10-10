using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DigitalPerformance
{
    /// <summary>
    /// 时间轴解释器 + 主循环。
    ///
    /// 每一帧都只用「绝对时间 t」重新求值一遍全部参数，从不做"上一帧再加一点"的累加。
    /// 这样任意一帧都能独立复现，也才能拖动进度条回看。
    ///
    /// 语言侧（大模型解析、编排）不在这里。它只负责改 cues.json，
    /// 由这个脚本在下一帧读进来执行。
    /// </summary>
    [AddComponentMenu("Digital Performance/Performance Director")]
    public class PerformanceDirector : MonoBehaviour
    {
        [Header("演出脚本")]
        public string cueFileName = "cues.json";

        [Header("播放控制")]
        public bool playing = true;
        public bool loop = true;
        public float timeScale = 1f;

        [Tooltip("固定求值步长。表演只在这个步长的整数倍时刻取值，保证同一条 cue 每次跑出来一样。")]
        public float quantum = 1f / 60f;

        [Header("舞台引用")]
        public StageActor[] actors;
        public StageActor[] devices;      // 非演员的可驱动对象：灯组、机械、屏幕……
        public Transform cameraAnchor;
        public Transform focus;
        public CameraRig rig;

        [Header("显示")]
        public bool showHud = true;

        class TargetRef
        {
            public Transform tr;
            public Renderer rend;
            public Light light;
        }

        readonly Dictionary<string, TargetRef> _targets = new Dictionary<string, TargetRef>();
        readonly Dictionary<string, ParamState> _baseline = new Dictionary<string, ParamState>();

        StageApplier _applier;
        CueTable _table;
        float _clock;
        string _status = "not loaded";

        public float Duration
        {
            get { return _table != null && _table.duration > 0f ? _table.duration : 12f; }
        }

        public float CurrentTime { get { return _clock; } }

        void Awake()
        {
            // 执行器持有 MaterialPropertyBlock（原生对象），只能在 Awake 之后创建，
            // 不能放在字段初始化里——那会撞上 MonoBehaviour 构造期禁止创建原生对象的限制。
            _applier = new StageApplier();
        }

        void Start()
        {
            LoadTable();
            CaptureBaseline();
        }

        public void LoadTable()
        {
            string path = Path.Combine(Application.streamingAssetsPath, cueFileName);
            if (!File.Exists(path))
            {
                _table = null;
                _status = "cue table not found: " + path;
                Debug.LogError("[PerformanceDirector] " + _status);
                return;
            }

            _table = CueTable.LoadFromFile(path);
            if (_table.cues == null) _table.cues = new List<Cue>();
            loop = _table.loop;
            _status = string.Format("loaded {0} cues / {1:F1}s", _table.cues.Count, _table.duration);
            Debug.Log("[PerformanceDirector] " + _status + "  <- " + path);
        }

        void CaptureBaseline()
        {
            _targets.Clear();
            _baseline.Clear();

            if (actors != null)
            {
                for (int i = 0; i < actors.Length; i++)
                {
                    if (actors[i] == null) continue;
                    Register(actors[i].id, actors[i].transform, actors[i].rend, actors[i].light);
                }
            }

            if (devices != null)
            {
                for (int i = 0; i < devices.Length; i++)
                {
                    if (devices[i] == null) continue;
                    Register(devices[i].id, devices[i].transform, devices[i].rend, devices[i].light);
                }
            }

            Register("Camera", cameraAnchor, null, null);
            Register("Focus", focus, null, null);
        }

        void Register(string id, Transform tr, Renderer rend, Light light)
        {
            if (string.IsNullOrEmpty(id) || tr == null) return;

            _targets[id] = new TargetRef { tr = tr, rend = rend, light = light };
            _baseline[id] = new ParamState
            {
                position = tr.localPosition,
                rotation = tr.localEulerAngles,
                scale = tr.localScale,
                color = StageApplier.ReadColor(rend, Color.white),
                intensity = StageApplier.ReadIntensity(light, 1f),
            };
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.Space)) playing = !playing;
            if (Input.GetKeyDown(KeyCode.R)) { _clock = 0f; playing = true; }
            if (Input.GetKeyDown(KeyCode.F5)) LoadTable();
            if (Input.GetKeyDown(KeyCode.LeftArrow)) { _clock = Mathf.Max(0f, _clock - 0.25f); playing = false; }
            if (Input.GetKeyDown(KeyCode.RightArrow)) { _clock = Mathf.Min(Duration, _clock + 0.25f); playing = false; }

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

            // 关键：参与求值的时间只取固定步长的整数倍，画面因此可复现。
            Evaluate(playing ? Mathf.Floor(_clock / step) * step : _clock);
        }

        void Evaluate(float t)
        {
            if (_table == null || _table.cues == null) return;

            foreach (var pair in _targets)
            {
                // 每次都从出场状态重算，而不是基于上一帧，所以拖动进度条和正常播放结果一致。
                ParamState st = CueEvaluator.Evaluate(_table, pair.Key, _baseline[pair.Key], t);
                _applier.Apply(pair.Value.tr, pair.Value.rend, pair.Value.light, st);
            }
        }

        void OnGUI()
        {
            if (!showHud) return;

            float w = Mathf.Min(Screen.width - 24f, 460f);
            GUILayout.BeginArea(new Rect(12f, 12f, w, 104f), GUI.skin.box);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(playing ? "Pause" : "Play", GUILayout.Width(64f))) playing = !playing;
            if (GUILayout.Button("Replay", GUILayout.Width(64f))) { _clock = 0f; playing = true; }
            if (GUILayout.Button("Reload cues", GUILayout.Width(96f))) LoadTable();
            GUILayout.Label(string.Format("t = {0:F2} / {1:F1} s", _clock, Duration));
            GUILayout.EndHorizontal();

            float s = GUILayout.HorizontalSlider(_clock, 0f, Duration);
            if (Mathf.Abs(s - _clock) > 0.001f)
            {
                _clock = Mathf.Clamp(s, 0f, Duration);
                playing = false;
            }

            GUILayout.Label(_status + "   [Space] play  [R] replay  [<- ->] scrub  [F5] reload");
            GUILayout.EndArea();
        }
    }
}
