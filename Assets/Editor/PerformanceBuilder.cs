using System.Collections.Generic;
using System.IO;
using DigitalPerformance;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace DigitalPerformance.EditorTools
{
    /// <summary>
    /// 生成演示场景与 cue 表。
    ///
    /// 编辑器里可以从菜单 Digital Performance / Build Demo Scene 触发；
    /// 也可以在命令行批处理里跑：
    ///   Unity -batchmode -nographics -projectPath &lt;proj&gt;
    ///         -executeMethod DigitalPerformance.EditorTools.PerformanceBuilder.BuildFromCommandLine -quit
    ///
    /// 注意：这个脚本只是"把一场演出写进 cue 表"。演出本身是数据，
    /// 之后要改动作、换曲子、接自然语言，改的都是 cues.json，不是这里。
    /// </summary>
    public static class PerformanceBuilder
    {
        const string ScenePath = "Assets/Scenes/Performance.unity";
        const string CueAssetPath = "Assets/StreamingAssets/cues.json";
        const float ShowDuration = 12f;
        const int PerformerCount = 5;

        static readonly Color[] Palette =
        {
            new Color(0.20f, 0.61f, 1.00f),
            new Color(0.95f, 0.53f, 0.23f),
            new Color(0.36f, 0.79f, 0.47f),
            new Color(0.92f, 0.47f, 0.69f),
            new Color(0.61f, 0.47f, 0.93f),
        };

        [MenuItem("Digital Performance/Build Demo Scene")]
        public static void Build()
        {
            EnsureFolder("Assets/Scenes");
            EnsureFolder("Assets/Materials");
            EnsureFolder("Assets/StreamingAssets");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            SetupLighting();
            BuildStage();

            var actors = new StageActor[PerformerCount];
            for (int i = 0; i < PerformerCount; i++) actors[i] = BuildPerformer(i);

            var devices = BuildLightRig();

            CameraRig rig;
            var cam = BuildCamera(out rig);
            var focus = BuildFocus();
            rig.focus = focus;

            var directorGo = new GameObject("Performance");
            var director = directorGo.AddComponent<PerformanceDirector>();
            director.actors = actors;
            director.devices = devices;
            director.cameraAnchor = cam.transform;
            director.focus = focus;
            director.rig = rig;
            director.cueFileName = "cues.json";

            var audio = BuildAudio(directorGo);
            if (audio != null)
            {
                var sync = directorGo.AddComponent<AudioSync>();
                sync.director = director;
                sync.source = audio;
            }

            var table = BuildCueTable();
            BakeAudioCues(table);
            WriteCueTable(table);

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new System.Exception("保存场景失败: " + ScenePath);

            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log(string.Format(
                "[PerformanceBuilder] 场景已生成 {0}，cue {1} 条，时长 {2:F1}s",
                ScenePath, table.cues.Count, table.duration));
        }

        /// <summary>命令行入口。用显式退出码，方便脚本判断成败。</summary>
        public static void BuildFromCommandLine()
        {
            try
            {
                Build();
                EditorApplication.Exit(0);
            }
            catch (System.Exception e)
            {
                Debug.LogError("[PerformanceBuilder] 生成失败: " + e);
                EditorApplication.Exit(1);
            }
        }

        static void SetupLighting()
        {
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.10f, 0.11f, 0.14f);
            RenderSettings.fog = false;

            // 五盏灯都要有像素光照，否则超出每物体像素灯数量的会被降级成顶点光照
            QualitySettings.pixelLightCount = 8;

            var light = Object.FindAnyObjectByType<Light>();
            if (light == null)
            {
                var go = new GameObject("Directional Light");
                light = go.AddComponent<Light>();
            }

            light.type = LightType.Directional;
            light.color = new Color(1f, 0.96f, 0.9f);
            light.intensity = 1.15f;
            light.shadows = LightShadows.Soft;
            light.transform.localEulerAngles = new Vector3(48f, -28f, 0f);
        }

        static void BuildStage()
        {
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Stage";
            ground.transform.localPosition = Vector3.zero;
            ground.transform.localScale = new Vector3(2.4f, 1f, 2.4f);
            ground.GetComponent<MeshRenderer>().sharedMaterial =
                CreateMaterial("Assets/Materials/Mat_Stage.mat", new Color(0.105f, 0.115f, 0.135f));

            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = "Backdrop";
            wall.transform.localPosition = new Vector3(0f, 3f, 7f);
            wall.transform.localScale = new Vector3(16f, 6f, 0.2f);
            wall.GetComponent<MeshRenderer>().sharedMaterial =
                CreateMaterial("Assets/Materials/Mat_Backdrop.mat", new Color(0.062f, 0.068f, 0.085f));
        }

        static StageActor BuildPerformer(int i)
        {
            float x = -4f + 2f * i;

            // 演员根节点：cue 表驱动的是它。起始位置在舞台上方，开场做"降落入场"。
            var root = new GameObject("P" + (i + 1));
            root.transform.localPosition = new Vector3(x, 9f, 0f);
            root.transform.localScale = new Vector3(0.35f, 0.35f, 0.35f);

            PrimitiveType type;
            Vector3 shape;
            switch (i)
            {
                case 1: type = PrimitiveType.Sphere; shape = new Vector3(1f, 1f, 1f); break;
                case 2: type = PrimitiveType.Cylinder; shape = new Vector3(1.1f, 0.5f, 1.1f); break;
                case 3: type = PrimitiveType.Capsule; shape = new Vector3(0.9f, 0.5f, 0.9f); break;
                case 4: type = PrimitiveType.Cube; shape = new Vector3(1.2f, 1.2f, 1.2f); break;
                default: type = PrimitiveType.Cube; shape = new Vector3(1f, 1f, 1f); break;
            }

            // 网格体挂在子节点上：不同原始体的尺寸差异在这里就地归一化成 1 个单位高。
            var mesh = GameObject.CreatePrimitive(type);
            mesh.name = "Mesh";
            mesh.transform.SetParent(root.transform, false);
            mesh.transform.localPosition = Vector3.zero;
            mesh.transform.localScale = shape;
            if (i == 4) mesh.transform.localRotation = Quaternion.Euler(0f, 0f, 45f);

            var rend = mesh.GetComponent<MeshRenderer>();
            Color dim = Color.Lerp(Palette[i], new Color(0.05f, 0.05f, 0.06f), 0.82f);
            rend.sharedMaterial = CreateMaterial("Assets/Materials/Mat_P" + (i + 1) + ".mat", dim);

            var actor = root.AddComponent<StageActor>();
            actor.id = "P" + (i + 1);
            actor.rend = rend;
            return actor;
        }

        static Camera BuildCamera(out CameraRig rig)
        {
            var cam = Camera.main;
            if (cam == null)
            {
                var go = new GameObject("Main Camera");
                go.tag = "MainCamera";
                cam = go.AddComponent<Camera>();
                go.AddComponent<AudioListener>();
            }

            cam.transform.localPosition = new Vector3(0f, 2.4f, -9.5f);
            cam.transform.localRotation = Quaternion.identity;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.035f, 0.04f, 0.055f);
            cam.fieldOfView = 45f;

            rig = cam.gameObject.AddComponent<CameraRig>();
            return cam;
        }

        static Transform BuildFocus()
        {
            var go = new GameObject("Focus");
            go.transform.localPosition = new Vector3(0f, 0.6f, 0f);
            return go.transform;
        }

        /// <summary>灯组：四盏主灯（低频打强度、中频换颜色）+ 一盏细节灯（高频微闪）。</summary>
        static StageActor[] BuildLightRig()
        {
            var rigGo = new GameObject("LightRig");
            var list = new List<StageActor>();

            for (int i = 0; i < 4; i++)
            {
                var go = new GameObject("L" + (i + 1));
                go.transform.SetParent(rigGo.transform, false);
                go.transform.localPosition = new Vector3(-6f + 4f * i, 6.5f, 2f);
                go.transform.localEulerAngles = new Vector3(90f, 0f, 0f);

                var light = go.AddComponent<Light>();
                light.type = LightType.Spot;
                light.spotAngle = 72f;
                light.range = 26f;
                light.intensity = AudioCueBaker.BaseIntensity;
                light.color = new Color(1f, 0.95f, 0.88f);
                light.shadows = LightShadows.None;

                var actor = go.AddComponent<StageActor>();
                actor.id = go.name;
                actor.light = light;
                list.Add(actor);
            }

            var accentGo = new GameObject("L5");
            accentGo.transform.SetParent(rigGo.transform, false);
            accentGo.transform.localPosition = new Vector3(0f, 1.2f, 3.5f);

            var accent = accentGo.AddComponent<Light>();
            accent.type = LightType.Point;
            accent.range = 14f;
            accent.intensity = AudioCueBaker.AccentBase;
            accent.color = new Color(0.72f, 0.62f, 1f);
            accent.shadows = LightShadows.None;

            var accentActor = accentGo.AddComponent<StageActor>();
            accentActor.id = "L5";
            accentActor.light = accent;
            list.Add(accentActor);

            return list.ToArray();
        }

        static AudioSource BuildAudio(GameObject host)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(AudioCueBaker.WavAssetPath);
            if (clip == null)
            {
                Debug.LogWarning("[PerformanceBuilder] 找不到音频 " + AudioCueBaker.WavAssetPath +
                                 "，场景里不放 AudioSource。先跑 Tools/make_test_audio.py。");
                return null;
            }

            var source = host.AddComponent<AudioSource>();
            source.clip = clip;
            source.playOnAwake = false;
            source.loop = false;
            source.volume = 0.8f;
            source.spatialBlend = 0f;
            return source;
        }

        static void BakeAudioCues(CueTable table)
        {
            string wavPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, AudioCueBaker.WavAssetPath);
            if (!File.Exists(wavPath))
            {
                Debug.LogWarning("[PerformanceBuilder] 没有 " + wavPath + "，本次只生成编排 cue。");
                return;
            }

            string report;
            AudioCueBaker.BakeInto(table, wavPath, out report);
            Debug.Log("[PerformanceBuilder] 音频驱动: " + report);
        }

        /// <summary>
        /// 这里就是"语言层"在演示版里的替身：把一场 12 秒的演出写成 cue 表。
        /// 换演出 = 换这张表，不用改任何播放代码。
        /// </summary>
        static CueTable BuildCueTable()
        {
            var table = new CueTable { title = "简易动画表演", duration = ShowDuration, loop = true };
            var cues = table.cues;

            for (int i = 0; i < PerformerCount; i++)
            {
                string id = "P" + (i + 1);
                float x = -4f + 2f * i;
                Color c = Palette[i];

                // 1) 入场：从上方落到台口，同时由 0.35 倍展开到原尺寸
                AddCue(cues, 0.20f + i * 0.35f, id, "position", 1.6f, "smooth", x, 0.5f, 0f, 0f);
                AddCue(cues, 0.20f + i * 0.35f, id, "scale", 1.6f, "smooth", 1f, 1f, 1f, 0f);

                // 2) 上色
                AddCue(cues, 2.60f + i * 0.18f, id, "color", 0.9f, "smooth", c.r, c.g, c.b, 1f);

                // 3) 波浪：依次弹起再落回
                AddCue(cues, 4.40f + i * 0.16f, id, "position", 0.55f, "out", x, 2.0f, 0f, 0f);
                AddCue(cues, 4.95f + i * 0.16f, id, "position", 0.65f, "smooth", x, 0.5f, 0f, 0f);

                // 4) 转身半圈
                AddCue(cues, 6.30f + i * 0.10f, id, "rotation", 1.8f, "smooth", 0f, 180f, 0f, 0f);

                // 5) 向中心聚拢并放大
                AddCue(cues, 8.60f + i * 0.16f, id, "position", 1.6f, "smooth", x * 0.18f, 0.5f, 0f, 0f);
                AddCue(cues, 8.60f + i * 0.16f, id, "scale", 1.6f, "smooth", 1.45f, 1.45f, 1.45f, 0f);

                // 6) 定格：先闪白，再回到本色
                AddCue(cues, 10.50f + i * 0.08f, id, "color", 0.45f, "out", 1f, 1f, 1f, 1f);
                AddCue(cues, 11.10f + i * 0.06f, id, "color", 0.70f, "smooth", c.r, c.g, c.b, 1f);
            }

            // 机位：位置由 cue 驱动，朝向交给 CameraRig 看向 Focus
            AddCue(cues, 0.60f, "Camera", "position", 3.4f, "smooth", 2.6f, 2.1f, -7.6f, 0f);
            AddCue(cues, 6.00f, "Camera", "position", 3.0f, "smooth", -2.8f, 1.7f, -7.4f, 0f);
            AddCue(cues, 9.40f, "Camera", "position", 2.6f, "smooth", 0f, 4.8f, -12.8f, 0f);

            AddCue(cues, 4.40f, "Focus", "position", 1.0f, "smooth", 0f, 1.0f, 0f, 0f);
            AddCue(cues, 9.40f, "Focus", "position", 1.5f, "smooth", 0f, 0.5f, 0f, 0f);

            cues.Sort((a, b) => a.t.CompareTo(b.t));
            return table;
        }

        static void AddCue(List<Cue> list, float t, string target, string channel,
                           float duration, string easing, float v0, float v1, float v2, float v3)
        {
            list.Add(new Cue
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
            });
        }

        static void WriteCueTable(CueTable table)
        {
            string path = CueFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            table.SaveToFile(path);
            Debug.Log("[PerformanceBuilder] cue 表已写入 " + path);
        }

        static string CueFilePath()
        {
            return Path.Combine(Directory.GetParent(Application.dataPath).FullName, CueAssetPath);
        }

        /// <summary>
        /// 命令行自检。不需要人盯着画面，就能确认场景装配完整、时间轴求值符合预期。
        /// 它用的是运行时同一套 CueEvaluator，所以验证的是真东西，不是另写一份算式。
        /// </summary>
        public static void VerifyFromCommandLine()
        {
            int failures = 0;

            try
            {
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

                var dir = Object.FindAnyObjectByType<PerformanceDirector>();
                failures += Check("场景里存在 PerformanceDirector", dir != null);
                if (dir == null)
                {
                    EditorApplication.Exit(2);
                    return;
                }

                failures += Check("演员数量为 " + PerformerCount,
                    dir.actors != null && dir.actors.Length == PerformerCount);
                failures += Check("相机锚点已绑定", dir.cameraAnchor != null);
                failures += Check("焦点已绑定", dir.focus != null);
                failures += Check("相机云台已绑定", dir.rig != null);
                failures += Check("相机云台看向焦点", dir.rig != null && dir.rig.focus == dir.focus);

                var table = CueTable.LoadFromFile(CueFilePath());
                failures += Check("cue 表可解析且非空",
                    table != null && table.cues != null && table.cues.Count > 0);
                if (table == null || table.cues == null) { EditorApplication.Exit(2); return; }

                for (int i = 0; i < dir.actors.Length; i++)
                {
                    var actor = dir.actors[i];
                    if (actor == null) { failures += Check("演员 " + i + " 引用存在", false); continue; }

                    string id = actor.id;
                    float x = -4f + 2f * i;

                    var baseline = new ParamState
                    {
                        position = actor.transform.localPosition,
                        rotation = actor.transform.localEulerAngles,
                        scale = actor.transform.localScale,
                        color = StageApplier.ReadColor(actor.rend, Color.white),
                    };

                    var t0 = CueEvaluator.Evaluate(table, id, baseline, 0f);
                    failures += Check(id + " 开场悬在台上方 y=9", Mathf.Abs(t0.position.y - 9f) < 0.02f);

                    var t35 = CueEvaluator.Evaluate(table, id, baseline, 3.5f);
                    failures += Check(id + " 3.5s 已入场到位",
                        Near(t35.position, new Vector3(x, 0.5f, 0f)) && Mathf.Abs(t35.scale.x - 1f) < 0.02f);

                    var t6 = CueEvaluator.Evaluate(table, id, baseline, 6f);
                    failures += Check(id + " 6.0s 已上色", NearColor(t6.color, Palette[i]));

                    var tEnd = CueEvaluator.Evaluate(table, id, baseline, 12f);
                    failures += Check(id + " 终场聚拢到中心并放大",
                        Mathf.Abs(tEnd.position.x - x * 0.18f) < 0.02f &&
                        Mathf.Abs(tEnd.scale.x - 1.45f) < 0.02f);

                    // 无状态求值：先求别的时刻，再回头求 5s，结果必须完全一致
                    CueEvaluator.Evaluate(table, id, baseline, 9.7f);
                    var afterOther = CueEvaluator.Evaluate(table, id, baseline, 5f);
                    var fresh = CueEvaluator.Evaluate(table, id, baseline, 5f);
                    failures += Check(id + " 任意时刻可独立复现",
                        Near(afterOther.position, fresh.position) && NearColor(afterOther.color, fresh.color));
                }

                // 入场错峰：0.5s 时 P1 已经在动，P5 还没开始
                var p1 = dir.actors[0];
                var p5 = dir.actors[PerformerCount - 1];
                var p1Base = new ParamState { position = p1.transform.localPosition, scale = p1.transform.localScale };
                var p5Base = new ParamState { position = p5.transform.localPosition, scale = p5.transform.localScale };
                failures += Check("入场是错峰的（0.5s 时 P1 已在动、P5 未动）",
                    CueEvaluator.Evaluate(table, p1.id, p1Base, 0.5f).position.y < 8.9f &&
                    Mathf.Abs(CueEvaluator.Evaluate(table, p5.id, p5Base, 0.5f).position.y - 9f) < 0.02f);

                var camBase = new ParamState
                {
                    position = dir.cameraAnchor.localPosition,
                    rotation = dir.cameraAnchor.localEulerAngles,
                    scale = dir.cameraAnchor.localScale,
                };
                failures += Check("开场机位在 z=-9.5",
                    Mathf.Abs(CueEvaluator.Evaluate(table, "Camera", camBase, 0f).position.z + 9.5f) < 0.02f);
                failures += Check("11.5s 机位已升高拉远",
                    CueEvaluator.Evaluate(table, "Camera", camBase, 11.5f).position.y > 4f);

                // ---------------- 音频驱动 ----------------

                int showCues = 0, audioCues = 0;
                bool audioHitsPerformer = false;
                for (int i = 0; i < table.cues.Count; i++)
                {
                    var c = table.cues[i];
                    if (c == null) continue;
                    if (c.source == "audio") audioCues++;
                    else showCues++;
                    if (c.source == "audio" && !string.IsNullOrEmpty(c.target) && c.target[0] == 'P')
                        audioHitsPerformer = true;
                }

                failures += Check(string.Format("cue 表既有编排也有音频（编排 {0} / 音频 {1}）", showCues, audioCues),
                    showCues > 40 && audioCues > 200);
                failures += Check("音频 cue 只写给灯，没动演员", !audioHitsPerformer);

                string wavPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, AudioCueBaker.WavAssetPath);
                List<float> low, mid, high;
                AudioCueBaker.Analyze(wavPath, out low, out mid, out high);

                failures += Check("低频检出 24 个拍点（实际 " + low.Count + "）", low.Count == 24);
                failures += Check("中频检出 12 个拍点（实际 " + mid.Count + "）", mid.Count == 12);
                failures += Check("高频检出 48 个拍点（实际 " + high.Count + "）", high.Count == 48);
                failures += Check("低频步长约 0.5s", Mathf.Abs(MedianStep(low) - 0.5f) < 0.03f);
                failures += Check("中频步长约 1.0s", Mathf.Abs(MedianStep(mid) - 1.0f) < 0.03f);
                failures += Check("高频步长约 0.25s", Mathf.Abs(MedianStep(high) - 0.25f) < 0.02f);

                var lamp = dir.devices != null && dir.devices.Length > 0 ? dir.devices[0] : null;
                failures += Check("灯组已绑定到导演", lamp != null && lamp.light != null);

                if (lamp != null && lamp.light != null && low.Count > 0 && mid.Count > 0)
                {
                    var lampBase = new ParamState
                    {
                        position = lamp.transform.localPosition,
                        rotation = lamp.transform.localEulerAngles,
                        scale = lamp.transform.localScale,
                        color = lamp.light.color,
                        intensity = lamp.light.intensity,
                    };

                    var onBeat = CueEvaluator.Evaluate(table, lamp.id, lampBase, low[0] + 0.02f);
                    var between = CueEvaluator.Evaluate(table, lamp.id, lampBase, low[0] + 0.45f);
                    var colored = CueEvaluator.Evaluate(table, lamp.id, lampBase, mid[0] + 0.5f);

                    failures += Check("拍点上主灯变亮", onBeat.intensity > lampBase.intensity * 1.3f);
                    failures += Check("拍与拍之间主灯回落到基准",
                        Mathf.Abs(between.intensity - lampBase.intensity) < 0.05f);

                    float colorShift = Mathf.Abs(colored.color.r - lampBase.color.r)
                                     + Mathf.Abs(colored.color.g - lampBase.color.g)
                                     + Mathf.Abs(colored.color.b - lampBase.color.b);
                    failures += Check("中频拍点推进了灯色", colorShift > 0.3f);
                }

                var accent = dir.devices != null ? dir.devices[dir.devices.Length - 1] : null;
                if (accent != null && accent.light != null && high.Count > 1)
                {
                    var accentBase = new ParamState
                    {
                        color = accent.light.color,
                        intensity = accent.light.intensity,
                    };
                    var flash = CueEvaluator.Evaluate(table, accent.id, accentBase, high[1] + 0.015f);
                    var idled = CueEvaluator.Evaluate(table, accent.id, accentBase, high[1] + 0.20f);

                    failures += Check("高频拍点上细节灯闪动", flash.intensity > accentBase.intensity * 1.3f);
                    failures += Check("两次闪动之间细节灯回到基准",
                        Mathf.Abs(idled.intensity - accentBase.intensity) < 0.05f);
                }

                var sync = Object.FindAnyObjectByType<AudioSync>();
                failures += Check("场景里有 AudioSync", sync != null);
                failures += Check("AudioSource 已绑定音频",
                    sync != null && sync.source != null && sync.source.clip != null);
                failures += Check("音频时长约 12s",
                    sync != null && sync.source != null && sync.source.clip != null &&
                    Mathf.Abs(sync.source.clip.length - 12f) < 0.25f);
                failures += Check("AudioSync 指向 PerformanceDirector", sync != null && sync.director != null);

                if (failures == 0)
                {
                    Debug.Log("[Verify] PASS 全部通过");
                    EditorApplication.Exit(0);
                }
                else
                {
                    Debug.LogError(string.Format("[Verify] FAIL {0} 项未通过", failures));
                    EditorApplication.Exit(2);
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError("[Verify] 异常: " + e);
                EditorApplication.Exit(3);
            }
        }

        static int Check(string label, bool ok)
        {
            if (ok) Debug.Log("[Verify] ok   " + label);
            else Debug.LogError("[Verify] FAIL " + label);
            return ok ? 0 : 1;
        }

        static bool Near(Vector3 a, Vector3 b)
        {
            return (a - b).sqrMagnitude < 0.0004f;
        }

        static bool NearColor(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) < 0.05f && Mathf.Abs(a.g - b.g) < 0.05f && Mathf.Abs(a.b - b.b) < 0.05f;
        }

        static float MedianStep(List<float> times)
        {
            if (times == null || times.Count < 2) return 0f;
            var steps = new List<float>();
            for (int i = 1; i < times.Count; i++) steps.Add(times[i] - times[i - 1]);
            steps.Sort();
            return steps[steps.Count / 2];
        }

        static Material CreateMaterial(string path, Color color)
        {
            var shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
            if (shader == null) throw new System.Exception("找不到可用的内置着色器");

            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null) AssetDatabase.DeleteAsset(path);

            var mat = new Material(shader);
            mat.color = color;
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0.3f);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0.05f);

            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        static void EnsureFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || folder == "Assets") return;
            if (AssetDatabase.IsValidFolder(folder)) return;

            string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }
    }
}
