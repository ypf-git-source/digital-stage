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
    /// 生成方格升降台舞台场景，并带命令行自检。
    ///
    ///   Unity -batchmode -nographics -projectPath &lt;proj&gt;
    ///         -executeMethod DigitalPerformance.EditorTools.StageBuilder.BuildFromCommandLine -quit
    ///   Unity -batchmode -nographics -projectPath &lt;proj&gt;
    ///         -executeMethod DigitalPerformance.EditorTools.StageBuilder.VerifyFromCommandLine -quit
    /// </summary>
    public static class StageBuilder
    {
        const string ScenePath = "Assets/Scenes/StageGrid.unity";
        const string LegacyScenePath = "Assets/Scenes/Stage36.unity";
        const string MeshDir = "Assets/Meshes";
        const string ProgramPath = "Assets/StreamingAssets/stage_program.json";

        // 台面阵列。半径保持内切值，舞台直径就一直是 2×半径；想更细腻就加密，想更大就放大半径。
        const int Columns = 10;
        const int Rows = 10;
        const float CellSize = 0.8f;   // 10 × 0.8 = 8 米，和上一版同尺寸，但台面小了 20%
        const float CellGap = 0.008f;   // 缝宽：8 毫米。越小接缝占比越低（占比 ≈ 2×缝宽÷格距）
        const float StageRadius = 4f;

        // 荷叶要"浮在水上"：柱体做高一点、绝大部分沉在水面以下，露出来的只有一小截
        // 这两个数是"叶片厚度"的来源——叶子离水面越高，看着就越像一块厚板。
        // 顶面几乎贴着水面，柱体绝大部分沉在水下。
        const float Elevation = 0.10f;      // 顶面离水面的基准高度
        const float LiftBodyHeight = 1.2f;  // 柱体总高，够在所有行程下都伸进水里
        const float WaterLevel = 0f;        // 水面高度

        static string ProjectRoot { get { return Directory.GetParent(Application.dataPath).FullName; } }

        [MenuItem("Digital Performance/Build Stage Grid Scene")]
        public static void Build()
        {
            EnsureFolder("Assets/Scenes");
            EnsureFolder("Assets/Meshes");
            EnsureFolder("Assets/Materials");
            EnsureFolder("Assets/Textures");
            EnsureFolder("Assets/StreamingAssets");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            SetupLighting();

            var grid = StageGrid.Create(Columns, Rows, CellSize, CellGap, StageRadius);
            var root = new GameObject("Stage");
            var transforms = new Transform[grid.Count];

            // 荷叶贴图：颜色和法线来自同一个高度场；一张图铺满整个圆，叶脉跨台面连续
            LotusTexture.Generate("Assets/Textures/LotusLeaf.png", "Assets/Textures/LotusLeafNormal.png",
                2048, 18, new Color(0.66f, 0.80f, 0.42f), new Color(0.17f, 0.38f, 0.16f), 20261009, 3.2f);

            var lotusColor = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Textures/LotusLeaf.png");
            var lotusNormal = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Textures/LotusLeafNormal.png");
            var matLotus = CreateLotusMaterial("Assets/Materials/Mat_LotusLeaf.mat", lotusColor, lotusNormal);

            Mesh squareMesh = null;

            for (int i = 0; i < grid.Count; i++)
            {
                var li = grid.lifts[i];
                Mesh mesh;

                if (li.clipped)
                {
                    mesh = StageMesh.BuildPrism(li.polygon, li.plan, LiftBodyHeight, StageRadius, "Lift_" + li.id);
                    SaveMesh(mesh, MeshDir + "/Lift_" + li.id + ".asset");
                }
                else
                {
                    if (squareMesh == null)
                    {
                        // 以格心为原点之后，所有整块的相对轮廓都长一样，可以共用同一个网格
                        squareMesh = StageMesh.BuildPrism(li.polygon, li.plan, LiftBodyHeight, StageRadius, "Lift_Square");
                        SaveMesh(squareMesh, MeshDir + "/Lift_Square.asset");
                    }
                    mesh = squareMesh;
                }

                var go = new GameObject(li.id);
                go.transform.SetParent(root.transform, false);
                // 编辑态就要落在基准高度上，否则场景视图里台体会埋进地板，
                // 一按 Play 又整场跳起来。
                go.transform.localPosition = new Vector3(li.plan.x, Elevation, li.plan.y);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;

                // 台面之间只有 2 厘米缝，如果每块都互相投影，缝里就是一条条暗线。
                // 台面不投影、也不接收投影，缝里就干净了；叶片表皮仍然照常投影/接收。
                var liftRenderer = go.AddComponent<MeshRenderer>();
                liftRenderer.sharedMaterial = matLotus;
                liftRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                liftRenderer.receiveShadows = false;

                transforms[i] = go.transform;
            }

            // 表皮：一张连续圆盘铺在台面上，把离散的台面抹成一整片叶面
            var skinMesh = BuildSkinMesh(24, 96, StageRadius, "LeafSkin");
            SaveMesh(skinMesh, MeshDir + "/LeafSkin.asset");

            var skinGo = new GameObject("LeafSkin");
            skinGo.transform.SetParent(root.transform, false);
            var skinFilter = skinGo.AddComponent<MeshFilter>();
            skinFilter.sharedMesh = skinMesh;
            skinGo.AddComponent<MeshRenderer>().sharedMaterial = matLotus;

            // 台面之间垫一层叶色的底：缝里看到的是叶子而不是水面，
            // 四条缝交叉的那个黑角就没了。
            var bedMesh = BuildSkinMesh(2, 96, StageRadius, "LeafBed");
            SaveMesh(bedMesh, MeshDir + "/LeafBed.asset");

            var bedGo = new GameObject("LeafBed");
            bedGo.transform.SetParent(root.transform, false);
            // 几乎贴着台面（3 毫米），缝里没有纵深，就没地方藏出黑角
            bedGo.transform.localPosition = new Vector3(0f, Elevation - 0.003f, 0f);
            bedGo.transform.localScale = new Vector3(1.01f, 1f, 1.01f);   // 略微外扩，叶缘不留深色细缝
            bedGo.AddComponent<MeshFilter>().sharedMesh = bedMesh;
            var bedRenderer = bedGo.AddComponent<MeshRenderer>();
            bedRenderer.sharedMaterial = CreateBedMaterial("Assets/Materials/Mat_LeafBed.mat", lotusColor);
            bedRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            bedRenderer.receiveShadows = false;

            // 水面按需求去掉了：它的底色近乎全黑，是"黑色拐角"的最终来源。
            // 现在缝里由叶色底填充，台面下方直接是背景。
            // 需要恢复"浮在池塘上"的效果时，把下面这行接回来即可。
            // BuildWater();
            BuildFillLight();
            var focus = BuildFocus();
            var cam = BuildCamera(focus.position);
            var side = BuildExtraCamera("Side Camera",
                focus.position + new Vector3(StageRadius * 3.2f, 1.3f, 0f), focus.position, false);
            var top = BuildExtraCamera("Top Camera",
                focus.position + new Vector3(0f, StageRadius * 4.8f, 0f), focus.position, true);

            var directorGo = new GameObject("StageDirector");
            var director = directorGo.AddComponent<StageDirector>();
            director.columns = Columns;
            director.rows = Rows;
            director.cellSize = CellSize;
            director.gap = CellGap;
            director.radius = StageRadius;
            director.liftTransforms = transforms;
            director.elevation = Elevation;
            director.skin = skinFilter;

            var views = directorGo.AddComponent<StageViews>();
            views.front = cam;
            views.side = side;
            views.top = top;
            views.focus = focus;
            views.distance = StageRadius * 3.2f;
            views.rise = 1.3f;
            views.topSize = StageRadius * 1.15f;

            var bridge = directorGo.AddComponent<NLStageBridge>();
            bridge.director = director;

            var program = BuildDefaultProgram();
            StageEvaluator.AutoRebase(program, grid, director.Limits);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(ProjectRoot, ProgramPath)));
            program.SaveToFile(Path.Combine(ProjectRoot, ProgramPath));

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                throw new System.Exception("保存场景失败: " + ScenePath);

            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(ScenePath, true),
                new EditorBuildSettingsScene("Assets/Scenes/Performance.unity", true),
            };

            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(LegacyScenePath) != null)
            {
                AssetDatabase.DeleteAsset(LegacyScenePath);
                Debug.Log("[StageBuilder] 旧的 6×6 场景 " + LegacyScenePath + " 已移走（用 6×6 参数重新生成即可恢复）");
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log(string.Format("[StageBuilder] 场景已生成 {0}：{1}×{2} 阵列，圆内 {3} 块台面（弧裁 {4} 块），基准面 {5:F2} m，离地 {6:F1} m，柱高 {7:F1} m",
                ScenePath, Columns, Rows, grid.Count, CountClipped(grid), program.base_height, Elevation, LiftBodyHeight));
        }

        public static void BuildFromCommandLine()
        {
            try
            {
                Build();
                EditorApplication.Exit(0);
            }
            catch (System.Exception e)
            {
                Debug.LogError("[StageBuilder] 生成失败: " + e);
                EditorApplication.Exit(1);
            }
        }

        static int CountClipped(StageGrid grid)
        {
            int n = 0;
            for (int i = 0; i < grid.Count; i++) if (grid.lifts[i].clipped) n++;
            return n;
        }

        /// <summary>
        /// 默认程序：空白。只有台面本身，不带任何效果，也不做任何动作。
        ///
        /// 效果定义仍在 StageEffects 里，HUD 上的效果按钮默认全关；
        /// 想加回哪一项，点一下按钮或按对应逻辑调用 ToggleEffect 即可。
        /// </summary>
        static StageProgram BuildDefaultProgram()
        {
            return new StageProgram { title = "空白舞台", duration = 16f, loop = true };
        }

        static void SetupLighting()
        {
            RenderSettings.ambientMode = AmbientMode.Flat;
            // 环境光要够，否则台面侧壁这种背光面会压成黑色，看着像一条条黑边
            RenderSettings.ambientLight = new Color(0.21f, 0.23f, 0.26f);
            RenderSettings.fog = false;

            // 细缝和叶缘在没开抗锯齿时会闪
            QualitySettings.antiAliasing = 4;
            QualitySettings.shadows = ShadowQuality.All;

            var light = Object.FindAnyObjectByType<Light>();
            if (light == null)
            {
                var go = new GameObject("Directional Light");
                light = go.AddComponent<Light>();
            }

            light.type = LightType.Directional;
            light.color = new Color(1f, 0.96f, 0.9f);
            light.intensity = 1.05f;
            light.shadows = LightShadows.Soft;
            light.transform.localEulerAngles = new Vector3(52f, -34f, 0f);
        }

        static Transform BuildFocus()
        {
            var go = new GameObject("Focus");
            go.transform.localPosition = new Vector3(0f, Elevation + 0.35f, 0f);
            return go.transform;
        }

        static Camera BuildCamera(Vector3 focus)
        {
            var cam = Camera.main;
            if (cam == null)
            {
                var go = new GameObject("Main Camera");
                go.tag = "MainCamera";
                cam = go.AddComponent<Camera>();
                go.AddComponent<AudioListener>();
            }

            cam.transform.position = focus + new Vector3(0f, 1.3f, -StageRadius * 3.2f);
            cam.transform.LookAt(focus);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.035f, 0.04f, 0.055f);
            cam.fieldOfView = 45f;
            cam.depth = 0f;
            return cam;
        }

        static Camera BuildExtraCamera(string name, Vector3 position, Vector3 lookAt, bool straightDown)
        {
            var go = new GameObject(name);
            go.transform.position = position;

            if (straightDown) go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            else go.transform.LookAt(lookAt);

            var cam = go.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.03f, 0.035f, 0.05f);
            cam.fieldOfView = 45f;
            cam.depth = 1f;
            cam.enabled = false;   // 由 StageViews 按当前模式打开
            return cam;
        }

        /// <summary>水面：把台面之间那 2 厘米的缝读成叶脉分格，整片叶子才像浮在水上。</summary>
        static void BuildWater()
        {
            var water = GameObject.CreatePrimitive(PrimitiveType.Plane);
            water.name = "Water";
            water.transform.localPosition = new Vector3(0f, WaterLevel, 0f);
            water.transform.localScale = new Vector3(6f, 1f, 6f);
            water.GetComponent<MeshRenderer>().sharedMaterial =
                CreateWaterMaterial("Assets/Materials/Mat_Water.mat");
        }

        /// <summary>侧后方补光：让叶片轮廓从暗背景里跳出来。</summary>
        static void BuildFillLight()
        {
            var go = new GameObject("Fill Light");
            go.transform.localEulerAngles = new Vector3(22f, 152f, 0f);

            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(0.55f, 0.78f, 0.88f);
            light.intensity = 0.55f;
            light.shadows = LightShadows.None;
        }

        /// <summary>叶面表皮：一张极坐标圆盘，顶点位置固定，高度每帧由连续高度场写入。</summary>
        static Mesh BuildSkinMesh(int rings, int sectors, float radius, string name)
        {
            var verts = new List<Vector3>(1 + rings * sectors);
            var uvs = new List<Vector2>(1 + rings * sectors);
            var tris = new List<int>();

            verts.Add(Vector3.zero);
            uvs.Add(new Vector2(0.5f, 0.5f));

            for (int ri = 1; ri <= rings; ri++)
            {
                float r = radius * ri / rings;
                for (int si = 0; si < sectors; si++)
                {
                    float a = 2f * Mathf.PI * si / sectors;
                    var p = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
                    verts.Add(new Vector3(p.x, 0f, p.y));
                    uvs.Add(new Vector2((p.x + radius) / (2f * radius), (p.y + radius) / (2f * radius)));
                }
            }

            // 中心扇面，绕序要让法线朝上
            for (int si = 0; si < sectors; si++)
            {
                tris.Add(0);
                tris.Add(1 + (si + 1) % sectors);
                tris.Add(1 + si);
            }

            for (int ri = 1; ri < rings; ri++)
            {
                int inner = 1 + (ri - 1) * sectors;
                int outer = 1 + ri * sectors;

                for (int si = 0; si < sectors; si++)
                {
                    int s0 = si, s1 = (si + 1) % sectors;
                    int a = inner + s0, b = inner + s1, c = outer + s1, d = outer + s0;
                    tris.Add(a); tris.Add(d); tris.Add(c);
                    tris.Add(a); tris.Add(c); tris.Add(b);
                }
            }

            var mesh = new Mesh { name = name };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // ------------------------------------------------------------ 自检

        public static void VerifyFromCommandLine()
        {
            int failures = 0;

            try
            {
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

                var director = Object.FindAnyObjectByType<StageDirector>();
                failures += Check("场景里有 StageDirector", director != null);
                if (director == null) { EditorApplication.Exit(2); return; }

                var bridge = Object.FindAnyObjectByType<NLStageBridge>();
                failures += Check("场景里有 NLStageBridge 且指向导演", bridge != null && bridge.director == director);

                int expectedAlive, expectedGone;
                CountExpected(Columns, Rows, CellSize, StageRadius, out expectedAlive, out expectedGone);

                failures += Check(string.Format("台面已绑定 {0} 块（{1}×{2} 减去圆外 {3} 块）",
                    expectedAlive, Columns, Rows, expectedGone),
                    director.liftTransforms != null && director.liftTransforms.Length == expectedAlive);
                failures += Check(string.Format("导演的阵列参数是 {0}×{1}、半径 {2:F1}",
                    Columns, Rows, StageRadius),
                    director.columns == Columns && director.rows == Rows &&
                    Mathf.Abs(director.radius - StageRadius) < 1e-4f);

                failures += Check("叶面表皮已绑定且网格够密",
                    director.skin != null && director.skin.sharedMesh != null &&
                    director.skin.sharedMesh.vertexCount > 1000);

                // 默认不铺表皮：铺上就看不到台面之间的缝了，俯视会变成一个绿盘子
                failures += Check("默认不铺表皮（俯视能看到台面分格）", !director.showSkin);

                var bed = GameObject.Find("LeafBed");
                var bedRenderer = bed != null ? bed.GetComponent<MeshRenderer>() : null;
                failures += Check("台面之间垫了叶色底（缝里不会露出深色水面）",
                    bed != null && bedRenderer != null && bedRenderer.sharedMaterial != null &&
                    bedRenderer.sharedMaterial.mainTexture != null);

                float bedDepth = bed != null ? director.elevation - bed.transform.localPosition.y : 99f;
                failures += Check(string.Format("底贴着台面（台下 {0:F3} m，槽浅接缝才细）", bedDepth),
                    bedDepth > 0f && bedDepth <= 0.02f);

                float gapRatio = 2f * director.gap / director.cellSize;
                failures += Check(string.Format("接缝占比 {0:F2}%（缝 {1:F0} mm / 格距 {2:F2} m）",
                    gapRatio * 100f, director.gap * 1000f, director.cellSize), gapRatio < 0.025f);

                // ---------------- 几何 ----------------

                var g0 = StageGrid.Create(Columns, Rows, CellSize, 0f, StageRadius);   // 不带缝，面积才对得上圆
                failures += Check(string.Format("台面数 {0}（实际 {1}）", expectedAlive, g0.Count),
                    g0.Count == expectedAlive);

                float area = 0f, minArea = float.MaxValue;
                string minWho = "";
                int clipped = 0;
                bool allInsideDisk = true;
                bool allCentroidInside = true;
                for (int i = 0; i < g0.Count; i++)
                {
                    var li = g0.lifts[i];
                    area += li.area;
                    if (li.area < minArea) { minArea = li.area; minWho = li.id; }
                    if (li.clipped) clipped++;

                    for (int k = 0; k < li.polygon.Length; k++)
                        if (li.polygon[k].magnitude > StageRadius + 1e-3f) allInsideDisk = false;

                    var c = StageMesh.Centroid(li.polygon);
                    if (!StageMesh.ContainsConvex(li.polygon, c)) allCentroidInside = false;
                }

                float expect = Mathf.PI * StageRadius * StageRadius;
                failures += Check(string.Format("{0} 块面积之和 = 圆面积（{1:F3} vs {2:F3}）", g0.Count, area, expect),
                    Mathf.Abs(area - expect) < 0.05f);
                failures += Check(string.Format("有台面被弧裁（{0} 块）", clipped), clipped > 0);
                failures += Check(string.Format("没有近乎消失的台面（最小 {0:F3}，在 {1}）", minArea, minWho),
                    minArea > 0.05f);
                failures += Check(string.Format("所有台面顶点都在半径 {0:F1} 的圆内", StageRadius), allInsideDisk);
                failures += Check("每块质心都在自己轮廓内（扇面三角化前提）", allCentroidInside);

                int missingCorners = 0;
                if (g0.At(0, 0) == null) missingCorners++;
                if (g0.At(0, Columns - 1) == null) missingCorners++;
                if (g0.At(Rows - 1, 0) == null) missingCorners++;
                if (g0.At(Rows - 1, Columns - 1) == null) missingCorners++;
                failures += Check(string.Format("四个角块都在圆外（缺失 {0} 块，圆外合计 {1} 块）",
                    missingCorners, expectedGone), missingCorners == 4 && expectedGone >= 4);

                var center = g0.At(Rows / 2 - 1, Columns / 2 - 1);
                failures += Check(string.Format("中间块完整（{0:F3}，标称 {1:F3}）", center.area, CellSize * CellSize),
                    Mathf.Abs(center.area - CellSize * CellSize) < 1e-3f);

                // ---------------- 摆位对齐 ----------------
                // 多边形用的是舞台中心坐标，物体本身又要摆到格心上，两边都算偏移就会出现
                // "整块整体平移、被弧裁的向外散开"。下面三条就是专门盯这一类错误的。

                float worstExtent = 0f, worstReach = 0f, worstGap = 0f;
                string extentWho = "", reachWho = "", gapWho = "";
                float halfBox = director.cellSize * 0.5f + 0.005f;

                for (int i = 0; i < director.liftTransforms.Length; i++)
                {
                    var tr = director.liftTransforms[i];
                    if (tr == null) continue;
                    var mf = tr.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null)
                    {
                        extentWho = tr.name + "（缺网格）";
                        worstExtent = float.MaxValue;
                        continue;
                    }

                    var b = mf.sharedMesh.bounds;

                    // 1) 网格必须以自身格心为原点，顶点不许超过半格
                    float extent = Mathf.Max(
                        Mathf.Max(Mathf.Abs(b.max.x), Mathf.Abs(b.min.x)),
                        Mathf.Max(Mathf.Abs(b.max.z), Mathf.Abs(b.min.z)));
                    if (extent > worstExtent) { worstExtent = extent; extentWho = tr.name; }

                    // 2) 摆到格心之后，整块不许伸出圆的包围盒
                    float reach = Mathf.Max(
                        Mathf.Max(Mathf.Abs(tr.localPosition.x + b.max.x), Mathf.Abs(tr.localPosition.x + b.min.x)),
                        Mathf.Max(Mathf.Abs(tr.localPosition.z + b.max.z), Mathf.Abs(tr.localPosition.z + b.min.z)));
                    if (reach > worstReach) { worstReach = reach; reachWho = tr.name; }
                }

                failures += Check(string.Format("每块网格以自身格心为原点（最远顶点 {0:F3} ≤ {1:F3}，{2}）",
                    worstExtent, halfBox, extentWho), worstExtent <= halfBox);

                failures += Check(string.Format("所有台面落在半径 {0:F1} 的轮廓内（最远 {1:F3}，{2}）",
                    director.radius, worstReach, reachWho), worstReach <= director.radius + 0.01f);

                // 3) 相邻整块之间的缝必须正好等于设定值
                for (int i = 0; i < g0.Count; i++)
                {
                    var a = g0.lifts[i];
                    if (a.clipped) continue;
                    var right = g0.At(a.row, a.col + 1);
                    if (right == null || right.clipped) continue;

                    var ta = director.liftTransforms[a.index];
                    var tb = director.liftTransforms[right.index];
                    var ma = ta.GetComponent<MeshFilter>().sharedMesh;
                    var mb = tb.GetComponent<MeshFilter>().sharedMesh;

                    float leftMax = ta.localPosition.x + ma.bounds.max.x;
                    float rightMin = tb.localPosition.x + mb.bounds.min.x;
                    float err = Mathf.Abs((rightMin - leftMax) - director.gap);
                    if (err > worstGap) { worstGap = err; gapWho = a.id + "/" + right.id; }
                }

                failures += Check(string.Format("相邻整块之间的缝等于 {0:F2} m（最差偏差 {1:F4} m，{2}）",
                    director.gap, worstGap, gapWho), worstGap < 0.002f);

                // 4) 编辑态的摆放高度要和运行时的基准高度一致
                float worstY = 0f;
                string yWho = "";
                for (int i = 0; i < director.liftTransforms.Length; i++)
                {
                    var tr = director.liftTransforms[i];
                    if (tr == null) continue;
                    float d = Mathf.Abs(tr.localPosition.y - director.elevation);
                    if (d > worstY) { worstY = d; yWho = tr.name; }
                }
                failures += Check(string.Format("台面在编辑态就位于基准高度 {0:F1} m（最差偏差 {1:F4} m，{2}）",
                    director.elevation, worstY, yWho), worstY < 0.001f);

                // ---------------- 求值 ----------------

                var grid = StageGrid.Create(Columns, Rows, CellSize, CellGap, StageRadius);
                var limits = new StageLimits { minWavelength = CellSize * 2f };

                // 取 x 最接近 +2.5 的那一块当测试点，和阵列规模无关
                LiftInfo cell = null;
                for (int i = 0; i < grid.Count; i++)
                {
                    var li = grid.lifts[i];
                    if (cell == null || Mathf.Abs(li.plan.x - 2.5f) < Mathf.Abs(cell.plan.x - 2.5f)) cell = li;
                }
                failures += Check(string.Format("测试台面 {0}（x={1:F1}）", cell.id, cell.plan.x), cell != null);

                float planeExpected = 0.5f * cell.plan.x;

                var planeProg = new StageProgram { duration = 6f };
                planeProg.layers.Add(new StageLayer
                {
                    type = "plane", tilt_x = 0.5f, start = 0f, fade_in = 0f, hold = 999f, fade_out = 0f,
                });
                float planeH = StageEvaluator.Height(planeProg, grid, cell, 1f);
                failures += Check(string.Format("倾斜平面：x={0:F1} 处应为 {1:F3}，实际 {2:F3}",
                    cell.plan.x, planeExpected, planeH),
                    Mathf.Abs(planeH - planeExpected) < 0.01f);

                var waveProg = new StageProgram { duration = 20f };
                waveProg.layers.Add(new StageLayer
                {
                    type = "wave", direction_deg = 0f, wavelength = 4f, amplitude = 0.3f, speed = 1f,
                    start = 0f, fade_in = 0f, hold = 999f, fade_out = 0f,
                });
                float h0 = StageEvaluator.Height(waveProg, grid, cell, 0f);
                float hHalf = StageEvaluator.Height(waveProg, grid, cell, 2f);   // 半周期 = 波长 / (2*速度)
                failures += Check(string.Format("行波半周期后反相（{0:F3} / {1:F3}）", h0, hHalf),
                    Mathf.Abs(h0 + hHalf) < 0.004f && Mathf.Abs(h0) > 0.05f);

                var domeProg = new StageProgram { duration = 6f };
                domeProg.layers.Add(new StageLayer
                {
                    type = "radial", center_x = 0f, center_y = 0f, radius = 2f, amplitude = 0.8f,
                    falloff = "smooth", start = 0f, fade_in = 0f, hold = 999f, fade_out = 0f,
                });
                LiftInfo centerCell = null, farCell = null;
                for (int i = 0; i < grid.Count; i++)
                {
                    var li = grid.lifts[i];
                    if (centerCell == null || li.plan.sqrMagnitude < centerCell.plan.sqrMagnitude) centerCell = li;
                    if (farCell == null || li.plan.sqrMagnitude > farCell.plan.sqrMagnitude) farCell = li;
                }
                float near = StageEvaluator.Height(domeProg, grid, centerCell, 1f);
                float far = StageEvaluator.Height(domeProg, grid, farCell, 1f);
                failures += Check(string.Format("圆顶：最内 {0:F3} 高、最外 {1:F3} 为零", near, far),
                    near > 0.4f && Mathf.Abs(far) < 0.01f);

                var over = new StageProgram { duration = 4f };
                over.layers.Add(new StageLayer
                {
                    type = "plane", tilt_x = 5f, start = 0f, fade_in = 0f, hold = 999f, fade_out = 0f,
                });
                var heights = new float[grid.Count];
                StageEvaluator.Evaluate(over, grid, limits, 1f, heights);
                float maxSeen = 0f;
                for (int i = 0; i < heights.Length; i++) maxSeen = Mathf.Max(maxSeen, heights[i]);
                failures += Check(string.Format("求值结果被夹在行程内（最高 {0:F2}）", maxSeen),
                    Mathf.Abs(maxSeen - limits.maxHeight) < 1e-3f);

                // ---------------- 单块台面控制 ----------------

                var singleProg = new StageProgram { duration = 6f };
                singleProg.layers.Add(new StageLayer
                {
                    type = "plane", tilt_x = 0.2f, start = 0f, fade_in = 0f, hold = 999f, fade_out = 0f,
                });

                var pinned = centerCell;
                var untouched = farCell;

                StageDirector.PinLift(singleProg, pinned.id, 1.4f, 0.3f);
                float pinnedH = StageEvaluator.Height(singleProg, grid, pinned, 3f);
                float untouchedH = StageEvaluator.Height(singleProg, grid, untouched, 3f);
                float untouchedLayer = 0.2f * untouched.plan.x;

                failures += Check(string.Format("单台 set 只改那一块（{0}={1:F2}，{2} 仍是图层值 {3:F2}）",
                    pinned.id, pinnedH, untouched.id, untouchedH),
                    Mathf.Abs(pinnedH - 1.4f) < 0.01f && Mathf.Abs(untouchedH - untouchedLayer) < 0.01f);

                singleProg.moves[0].mode = "add";
                float addedH = StageEvaluator.Height(singleProg, grid, pinned, 3f);
                failures += Check(string.Format("改成 add 后在图层之上叠加（{0:F2}）", addedH),
                    Mathf.Abs(addedH - (0.2f * pinned.plan.x + 1.4f)) < 0.01f);

                singleProg.moves[0].mode = "set";
                failures += Check("手动锁定计数正确", StageDirector.CountManualMoves(singleProg) == 1);
                failures += Check("手动锁定能整体清除",
                    StageDirector.ClearManualMoves(singleProg) == 1 &&
                    StageDirector.CountManualMoves(singleProg) == 0);
                failures += Check("清除后回到图层值",
                    Mathf.Abs(StageEvaluator.Height(singleProg, grid, pinned, 3f) - 0.2f * pinned.plan.x) < 0.01f);

                // 点选：从某块台面正上方垂直往下打一条射线，应当命中那一块
                var pickTarget = grid.At(Rows / 2 - 1, Columns / 2);
                var heightsNow = new float[grid.Count];
                StageEvaluator.Evaluate(singleProg, grid, limits, 0f, heightsNow);

                Vector3 hitAt;
                var downRay = new Ray(new Vector3(pickTarget.plan.x, 30f, pickTarget.plan.y), Vector3.down);
                var picked = StagePicker.Pick(grid, heightsNow, Elevation, downRay, out hitAt);
                failures += Check(string.Format("垂直射线点中 {0}（期望 {1}）",
                    picked != null ? picked.id : "空", pickTarget.id),
                    picked != null && picked.id == pickTarget.id);

                // ---------------- 自动抬高基准面 ----------------

                var below = new StageProgram { duration = 6f };
                below.layers.Add(new StageLayer
                {
                    type = "wave", wavelength = 4f, amplitude = 0.4f, speed = 1f,
                    start = 0f, fade_in = 0f, hold = 999f, fade_out = 0f,
                });
                float shift = StageEvaluator.AutoRebase(below, grid, limits);
                failures += Check(string.Format("基准面自动抬高 {0:F2} m", shift), Mathf.Abs(shift - 0.4f) < 0.02f);
                failures += Check("抬高后不再触底",
                    StageValidator.CountErrors(StageValidator.Validate(below, grid, limits)) == 0);

                // ---------------- 校验器 ----------------

                var shortWave = new StageProgram { duration = 4f };
                shortWave.layers.Add(new StageLayer
                {
                    type = "wave", wavelength = 0.8f, amplitude = 0.2f, speed = 1f,
                    start = 0f, fade_in = 0f, hold = 999f, fade_out = 0f,
                });
                failures += Check("校验器能发现波长过短",
                    HasIssue(StageValidator.Validate(shortWave, grid, limits), "波长"));

                var tooBig = new StageProgram { duration = 4f };
                tooBig.layers.Add(new StageLayer
                {
                    type = "radial", radius = 2f, amplitude = 5f,
                    start = 0f, fade_in = 0f, hold = 999f, fade_out = 0f,
                });
                failures += Check("校验器能发现行程超限",
                    StageValidator.CountErrors(StageValidator.Validate(tooBig, grid, limits)) > 0);

                var badLift = new StageProgram { duration = 4f, base_height = 0.5f };
                badLift.moves.Add(new StageMove { lift = "r99c99", height = 1f });
                failures += Check("校验器能发现不存在的台面编号",
                    StageValidator.CountErrors(StageValidator.Validate(badLift, grid, limits)) > 0);

                // ---------------- 预设与本地规则 ----------------

                int presetErrors = 0;
                for (int i = 0; i < StagePresets.Count; i++)
                {
                    var p = StagePresets.Create(i);
                    StageEvaluator.AutoRebase(p, grid, limits);
                    var iss = StageValidator.Validate(p, grid, limits);
                    presetErrors += iss.Count;
                    for (int k = 0; k < iss.Count; k++)
                        Debug.Log("[Verify] 预设「" + StagePresets.Name(i) + "」→ " + iss[k]);
                }
                failures += Check(string.Format("{0} 个预设都没有越界（含警告）", StagePresets.Count), presetErrors == 0);

                string[] phrases =
                {
                    "整个舞台来一道波浪，从左到右",
                    "舞台整体向左倾斜一点",
                    "中间慢慢鼓起一个圆顶",
                    "外圈做一圈同心涟漪",
                    "做成台阶的样子",
                    "随机涌动，像水沸腾一样",
                    "全部放平复位",
                };

                int ruleErrors = 0;
                for (int i = 0; i < phrases.Length; i++)
                {
                    var p = NLStageBridge.StageFromRules(phrases[i]);
                    StageEvaluator.AutoRebase(p, grid, limits);
                    var issues = StageValidator.Validate(p, grid, limits);
                    ruleErrors += issues.Count;
                    for (int k = 0; k < issues.Count; k++)
                        Debug.Log("[Verify] 规则句「" + phrases[i] + "」→ " + issues[k]);
                }
                failures += Check("本地规则解析出的程序都没有越界（含警告）", ruleErrors == 0);

                failures += Check("「波浪」解析为 wave",
                    TypeOf(NLStageBridge.StageFromRules("整个舞台来一道波浪")) == "wave");
                failures += Check("「倾斜」解析为 plane",
                    TypeOf(NLStageBridge.StageFromRules("舞台整体向左倾斜")) == "plane");
                failures += Check("「圆顶」解析为 radial",
                    TypeOf(NLStageBridge.StageFromRules("中间鼓起一个圆顶")) == "radial");
                failures += Check("「复位」解析为空程序",
                    NLStageBridge.StageFromRules("全部放平复位").layers.Count == 0);
                failures += Check("「外圈」解析出 ring:2 范围",
                    NLStageBridge.StageFromRules("外圈做一圈涟漪").layers[0].scope == "ring:2");

                // ---------------- 荷叶贴图与顶面 UV ----------------

                var lotusMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/Mat_LotusLeaf.mat");
                failures += Check("荷叶材质存在并挂了贴图", lotusMat != null && lotusMat.mainTexture != null);
                failures += Check("荷叶贴图资源存在",
                    AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Textures/LotusLeaf.png") != null);

                float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
                bool uvInRange = true;
                int uvCount = 0;
                for (int i = 0; i < director.liftTransforms.Length; i++)
                {
                    var tr = director.liftTransforms[i];
                    var mf = tr != null ? tr.GetComponent<MeshFilter>() : null;
                    if (mf == null || mf.sharedMesh == null) continue;

                    var uvsAll = mf.sharedMesh.uv;
                    if (uvsAll == null || uvsAll.Length == 0) { uvInRange = false; continue; }

                    uvCount++;
                    for (int k = 0; k < uvsAll.Length; k++)
                    {
                        var uv = uvsAll[k];
                        if (uv.x < -0.001f || uv.x > 1.001f || uv.y < -0.001f || uv.y > 1.001f) uvInRange = false;
                        if (uv.x < minU) minU = uv.x;
                        if (uv.x > maxU) maxU = uv.x;
                        if (uv.y < minV) minV = uv.y;
                        if (uv.y > maxV) maxV = uv.y;
                    }
                }

                failures += Check(string.Format("每块台面都有 UV（{0} 块）", uvCount),
                    uvCount == director.liftTransforms.Length);
                failures += Check(string.Format("顶面 UV 归一化在 0..1（U {0:F3}..{1:F3}，V {2:F3}..{3:F3}）",
                    minU, maxU, minV, maxV), uvInRange);
                failures += Check(string.Format("UV 铺满整个圆，叶脉才跨台面连续（跨度 {0:F2} × {1:F2}）",
                    maxU - minU, maxV - minV), maxU - minU > 0.9f && maxV - minV > 0.9f);

                // ---------------- 露珠与发光通道 ----------------

                var dropProg = new StageProgram { duration = 10f };
                dropProg.layers.Add(new StageLayer
                {
                    type = "drop", channel = "height", center_x = 0f, center_y = 0f,
                    direction_deg = 0f, speed = 1f, radius = 1.5f, amplitude = 0.2f,
                    start = 0f, fade_in = 0f, hold = 99f, fade_out = 0f, easing = "linear",
                });

                float c0 = StageEvaluator.Height(dropProg, grid, centerCell, 0f);
                float c2 = StageEvaluator.Height(dropProg, grid, centerCell, 2f);
                var aheadCell = Nearest(grid, new Vector2(2f, 0f));     // 珠子走了 2 秒之后的位置
                float a0 = StageEvaluator.Height(dropProg, grid, aheadCell, 0f);
                float a2 = StageEvaluator.Height(dropProg, grid, aheadCell, 2f);
                failures += Check(string.Format("露珠沿方向前进（叶心 {0:F3}→{1:F3}，x≈2.5 处 {2:F3}→{3:F3}）",
                    c0, c2, a0, a2), c0 > c2 * 2f && a2 > a0 * 2f && a2 > 0.05f);

                var glowProg = new StageProgram { duration = 10f };
                glowProg.layers.Add(new StageLayer
                {
                    type = "drop", channel = "glow", center_x = 0f, center_y = 0f,
                    direction_deg = 0f, speed = 1f, radius = 1.5f, amplitude = 1f,
                    start = 0f, fade_in = 0f, hold = 99f, fade_out = 0f,
                });
                float glowAtCenter = StageEvaluator.Glow(glowProg, grid, centerCell, 0f);
                float heightFromGlow = StageEvaluator.Height(glowProg, grid, centerCell, 0f);
                failures += Check(string.Format("glow 图层只发光不形变（发光 {0:F2}，高度 {1:F4}）",
                    glowAtCenter, heightFromGlow),
                    glowAtCenter > 0.4f && Mathf.Abs(heightFromGlow) < 1e-4f);

                // 默认荷叶程序本身必须满足行程 / 速度 / 相邻高差
                var lotusProg = BuildDefaultProgram();
                StageEvaluator.AutoRebase(lotusProg, grid, limits);
                StagePaths.Bake(lotusProg, grid);          // 和运行时一样：先烘焙轨迹，再校验

                var lotusIssues = StageValidator.Validate(lotusProg, grid, limits);
                for (int k = 0; k < lotusIssues.Count; k++)
                    Debug.Log("[Verify] 荷叶程序 → " + lotusIssues[k]);
                failures += Check(string.Format("默认程序（空白舞台）满足全部约束（{0} 条图层）",
                    lotusProg.layers.Count), lotusIssues.Count == 0);
                failures += Check("默认舞台上没有任何效果图层", lotusProg.layers.Count == 0);

                // 表皮与台面在同一点必须算出同一个高度
                float skinDiff = 0f;
                for (int i = 0; i < grid.Count; i++)
                {
                    var li = grid.lifts[i];
                    float plate = StageEvaluator.Height(lotusProg, grid, li, 5.3f);
                    float skinH = StageEvaluator.HeightAt(lotusProg, grid, li.plan, 5.3f, true);
                    skinDiff = Mathf.Max(skinDiff, Mathf.Abs(plate - skinH));
                }
                failures += Check(string.Format("表皮与台面在格心处高度一致（最大差 {0:F4} m）", skinDiff),
                    skinDiff < 1e-3f);

                // 叶缘褶皱
                var ruffleProg = new StageProgram { duration = 6f };
                ruffleProg.layers.Add(new StageLayer
                {
                    type = "ruffle", radius = 3f, band = 0.5f, veins = 8, amplitude = 0.2f, speed = 0f,
                    start = 0f, fade_in = 0f, hold = 999f, fade_out = 0f, easing = "linear",
                });

                float angA = Mathf.PI / 16f, angB = angA + Mathf.PI / 8f;
                float rufA = StageEvaluator.HeightAt(ruffleProg, grid,
                    new Vector2(Mathf.Cos(angA), Mathf.Sin(angA)) * 3f, 0f, true);
                float rufB = StageEvaluator.HeightAt(ruffleProg, grid,
                    new Vector2(Mathf.Cos(angB), Mathf.Sin(angB)) * 3f, 0f, true);
                float rufCore = StageEvaluator.HeightAt(ruffleProg, grid, new Vector2(0.5f, 0.5f), 0f, true);
                failures += Check(string.Format("叶缘褶皱一正一负、叶心不受影响（{0:F3} / {1:F3} / {2:F3}）",
                    rufA, rufB, rufCore), rufA > 0.15f && rufB < -0.15f && Mathf.Abs(rufCore) < 0.01f);

                // 扭转
                var twistProg = new StageProgram { duration = 12f };
                twistProg.layers.Add(new StageLayer
                {
                    type = "twist", radius = 4f, veins = 1, amplitude = 0.3f, speed = 1f,
                    start = 0f, fade_in = 0f, hold = 999f, fade_out = 0f, easing = "linear",
                });

                var twistPt = new Vector2(0f, 3f);
                float tw0 = StageEvaluator.HeightAt(twistProg, grid, twistPt, 0f, true);
                float twHalf = StageEvaluator.HeightAt(twistProg, grid, twistPt, Mathf.PI, true);
                failures += Check(string.Format("扭转半周期后反相（{0:F3} → {1:F3}）", tw0, twHalf),
                    Mathf.Abs(tw0 + twHalf) < 0.01f && Mathf.Abs(tw0) > 0.1f);

                // 露珠顺坡滚：斜面朝 +x 抬高，请求方向也是 +x，实际应当往 -x 去
                var slopeProg = new StageProgram { duration = 8f };
                slopeProg.layers.Add(new StageLayer
                {
                    type = "plane", tilt_x = 0.5f, start = 0f, fade_in = 0f, hold = 999f, fade_out = 0f,
                });
                var slopeDrop = new StageLayer
                {
                    type = "drop", direction_deg = 0f, speed = 1f, radius = 1.2f, amplitude = -0.05f,
                    start = 0f, fade_in = 0f, hold = 6f, fade_out = 0f, easing = "linear",
                };
                slopeProg.layers.Add(slopeDrop);
                StagePaths.Bake(slopeProg, grid);

                Vector2 pathPos;
                bool traced = StagePaths.TryGetPosition(slopeDrop, 2f, out pathPos);
                failures += Check(string.Format("露珠顺坡滚：要求朝 +x，实际走到 x={0:F2}", pathPos.x),
                    traced && pathPos.x < -0.8f);

                // 露珠在叶面上是微凹，不是凸起
                var dipProg = new StageProgram { duration = 6f };
                dipProg.layers.Add(new StageLayer
                {
                    type = "drop", direction_deg = 0f, speed = 0f, radius = 1.5f, amplitude = -0.1f,
                    start = 0f, fade_in = 0f, hold = 999f, fade_out = 0f,
                });
                float dipAtCenter = StageEvaluator.Height(dipProg, grid, centerCell, 0f);
                failures += Check(string.Format("露珠在叶面上是微凹（{0:F3} m）", dipAtCenter), dipAtCenter < -0.02f);

                // 表面上不该再有任何发光：叶心积水、露珠闪光、光晕都已去掉
                StagePaths.Bake(lotusProg, grid);
                float glowT1 = 0f, glowT2 = 0f;
                for (int i = 0; i < grid.Count; i++)
                {
                    glowT1 = Mathf.Max(glowT1, StageEvaluator.Glow(lotusProg, grid, grid.lifts[i], 3.6f));
                    glowT2 = Mathf.Max(glowT2, StageEvaluator.Glow(lotusProg, grid, grid.lifts[i], 8.2f));
                }
                failures += Check(string.Format("表面没有任何发光（两个时刻的最大值 {0:F3} / {1:F3}）", glowT1, glowT2),
                    glowT1 < 0.001f && glowT2 < 0.001f);

                // 露珠必须真的离开叶心滚到叶缘，不能被碗底困死
                // （默认舞台是空的，所以用一份只带效果的程序来验）
                var dropScene = new StageProgram { duration = 16f };
                StageEffects.ApplyAll(dropScene);
                StagePaths.Bake(dropScene, grid);
                failures += Check(string.Format("打开露珠效果后轨迹已烘焙（{0} 条）", StagePaths.Count),
                    StagePaths.Count >= 3);

                float farOut = 0f;
                bool escaped = false;
                for (int i = 0; i < dropScene.layers.Count; i++)
                {
                    var L = dropScene.layers[i];
                    if (L == null || L.type != "drop" || L.channel != "height") continue;

                    Vector2 endPos;
                    if (!StagePaths.TryGetPosition(L, L.start + L.fade_in + 3.2f, out endPos)) continue;
                    if (endPos.magnitude > farOut) farOut = endPos.magnitude;
                    if (endPos.magnitude > 2.2f) escaped = true;
                }
                failures += Check(string.Format("露珠能从积水滚到叶缘（最远 {0:F2} m，叶半径 {1:F1} m）",
                    farOut, StageRadius), escaped);

                // ---------------- 效果开关（换演出不能把效果冲掉） ----------------

                var fxProg = new StageProgram { duration = 12f, title = "效果测试" };
                StageEffects.ApplyAll(fxProg);
                failures += Check(string.Format("效果库全开后有 {0} 条图层", fxProg.layers.Count),
                    fxProg.layers.Count >= 6);

                int windLayers = CountGroup(fxProg, "wind");
                StageEffects.Remove(fxProg, "wind");
                bool windGone = !StageEffects.Has(fxProg, "wind");
                StageEffects.Apply(fxProg, "wind");
                failures += Check(string.Format("效果可以关掉再打开（风：{0} 层 → 关 → {1} 层）",
                    windLayers, CountGroup(fxProg, "wind")),
                    windGone && CountGroup(fxProg, "wind") == windLayers);

                // 模拟"换一套演出"：只替换 show 层，效果原样保留
                var showProg = StagePresets.Create(6);
                var kept = new List<StageLayer>();
                for (int i = 0; i < fxProg.layers.Count; i++)
                {
                    var L = fxProg.layers[i];
                    if (L != null && StageEffects.IsEffect(L.group)) kept.Add(L);
                }
                showProg.layers.AddRange(kept);

                bool allKept = true;
                for (int i = 0; i < StageEffects.Count; i++)
                    if (!StageEffects.Has(showProg, StageEffects.Ids[i])) allKept = false;

                failures += Check(string.Format("换演出后五组效果全都还在（保留 {0} 层）", kept.Count), allKept);

                // ---------------- 材质与画面细节 ----------------

                // 这一版按需求去掉了全部叶脉：沿圆周不该再有成串的波峰
                int veinPeaks = VeinAngles(0.55f).Count;
                failures += Check(string.Format("纹理里已经没有叶脉（沿圆周波峰 {0} 个）", veinPeaks),
                    veinPeaks <= 2);

                // 按需求去掉了叶面上的所有噪声：不该再有波浪状起伏
                float hLo = float.MaxValue, hHi = float.MinValue;
                for (int i = 0; i < 600; i++)
                {
                    float a = i * 0.41f;
                    float rr = 0.08f + 0.82f * ((i % 60) / 60f);   // 只看内侧 90%，叶缘上翘不算
                    float hh = LotusTexture.SampleHeight(rr * Mathf.Cos(a), rr * Mathf.Sin(a), 18, 20261009);
                    if (hh < hLo) hLo = hh;
                    if (hh > hHi) hHi = hh;
                }
                failures += Check(string.Format("叶面是平的、没有波浪状起伏（内侧跨度 {0:F4}）", hHi - hLo),
                    hHi - hLo < 0.002f);

                failures += Check("荷叶用的是自定义着色器（双面 / 透光 / 蜡质高光）",
                    lotusMat != null && lotusMat.shader != null &&
                    lotusMat.shader.name == "DigitalPerformance/LotusLeaf");
                failures += Check("叶脉法线贴图已挂上，且按法线类型导入",
                    lotusMat != null && lotusMat.GetTexture("_BumpMap") != null &&
                    AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Textures/LotusLeafNormal.png") != null);

                failures += Check("水面已去掉（下面不再有近乎全黑的平面）", GameObject.Find("Water") == null);
                failures += Check(string.Format("抗锯齿已开（{0}x）", QualitySettings.antiAliasing),
                    QualitySettings.antiAliasing >= 4);
                failures += Check("换程序会做交叉过渡", director.transitionDuration > 0.1f);

                // 台面之间不该互相投影（否则 2 厘米缝里全是暗线）
                int casters = 0, receivers = 0;
                for (int i = 0; i < director.liftTransforms.Length; i++)
                {
                    var tr = director.liftTransforms[i];
                    if (tr == null) continue;
                    var mr = tr.GetComponent<MeshRenderer>();
                    if (mr == null) continue;
                    if (mr.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off) casters++;
                    if (mr.receiveShadows) receivers++;
                }
                failures += Check(string.Format("台面不再互相投影（投影 {0} 个 / 接收 {1} 个）", casters, receivers),
                    casters == 0 && receivers == 0);

                // 叶缘渐隐要窄，否则看着像一圈黑描边
                float rimFade = lotusMat != null && lotusMat.HasProperty("_RimFade")
                    ? lotusMat.GetFloat("_RimFade") : 1f;
                failures += Check(string.Format("叶缘渐隐较窄（{0:F2}）", rimFade), rimFade <= 0.10f);

                // 相位抖动：相隔一个波长的两点本来同相，加了抖动就不该再一样
                var jitProg = new StageProgram { duration = 6f };
                jitProg.layers.Add(new StageLayer
                {
                    type = "wave", direction_deg = 0f, wavelength = 2f, amplitude = 0.3f, speed = 0f,
                    jitter = 1.5f, seed = 5,
                    start = 0f, fade_in = 0f, hold = 999f, fade_out = 0f, easing = "linear",
                });
                float jitA = StageEvaluator.HeightAt(jitProg, grid, new Vector2(0.5f, 0.5f), 0f, true);
                float jitB = StageEvaluator.HeightAt(jitProg, grid, new Vector2(2.5f, 0.5f), 0f, true);
                failures += Check(string.Format("相位抖动让同相位置不再同步（{0:F3} vs {1:F3}）", jitA, jitB),
                    Mathf.Abs(jitA - jitB) > 0.02f);

                // 露珠下坡加速
                var accProg = new StageProgram { duration = 6f };
                var accDrop = new StageLayer
                {
                    type = "drop", direction_deg = 0f, speed = 0.6f, radius = 1f, amplitude = -0.05f,
                    accel = 0.8f,
                    start = 0f, fade_in = 0f, hold = 6f, fade_out = 0f, easing = "linear",
                };
                accProg.layers.Add(accDrop);
                StagePaths.Bake(accProg, grid);

                Vector2 seg1, seg2;
                StagePaths.TryGetPosition(accDrop, 1f, out seg1);
                StagePaths.TryGetPosition(accDrop, 2f, out seg2);
                float first = seg1.magnitude;
                float second = seg2.magnitude - first;
                failures += Check(string.Format("露珠下坡会加速（第一秒 {0:F2} m，第二秒 {1:F2} m）", first, second),
                    second > first * 1.15f);

                // ---------------- 多视角 ----------------

                var views = Object.FindAnyObjectByType<StageViews>();
                failures += Check("场景里有 StageViews", views != null);
                failures += Check("正面 / 侧面 / 俯视三台相机都已绑定",
                    views != null && views.front != null && views.side != null && views.top != null);

                if (views != null && views.front != null && views.side != null && views.top != null)
                {
                    failures += Check("只有正面相机带 AudioListener",
                        views.front.GetComponent<AudioListener>() != null &&
                        views.side.GetComponent<AudioListener>() == null &&
                        views.top.GetComponent<AudioListener>() == null);

                    failures += Check("俯视相机垂直向下",
                        Vector3.Dot(views.top.transform.forward, Vector3.down) > 0.999f);

                    failures += Check(string.Format("俯视是正交相机、取景半高 {0:F1} m", views.topSize),
                        views.topOrthographic && views.topSize >= StageRadius);

                    float frontSide = views.front.transform.position.x - views.side.transform.position.x;
                    failures += Check(string.Format("正面在 -z 侧、侧面在 +x 侧（水平间距 {0:F1} m）", Mathf.Abs(frontSide)),
                        views.front.transform.position.z < -1f && views.side.transform.position.x > 1f);

                    failures += Check("三台相机都看向同一个焦点",
                        views.focus != null && views.focus.position.y > director.elevation);
                }

                // ---------------- 默认程序 ----------------

                string progPath = Path.Combine(ProjectRoot, ProgramPath);
                failures += Check("默认程序文件存在", File.Exists(progPath));
                if (File.Exists(progPath))
                {
                    var loaded = StageProgram.LoadFromFile(progPath);
                    failures += Check(string.Format("默认程序可解析（{0} 条图层，空白舞台）",
                        loaded != null && loaded.layers != null ? loaded.layers.Count : -1),
                        loaded != null && loaded.layers != null);
                }

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

        static string TypeOf(StageProgram p)
        {
            return p != null && p.layers.Count > 0 ? p.layers[0].type : "";
        }

        static int CountGroup(StageProgram p, string group)
        {
            if (p == null || p.layers == null) return 0;
            int n = 0;
            for (int i = 0; i < p.layers.Count; i++)
                if (p.layers[i] != null && p.layers[i].group == group) n++;
            return n;
        }

        /// <summary>沿半径为 r 的圆采一圈叶脉高度，返回所有波峰的角位置。</summary>
        static List<float> VeinAngles(float r)
        {
            const int samples = 2048;
            var h = new float[samples];
            float max = 0f;

            for (int i = 0; i < samples; i++)
            {
                float a = 2f * Mathf.PI * i / samples;
                h[i] = LotusTexture.SampleHeight(r * Mathf.Cos(a), r * Mathf.Sin(a), 18, 20261009);
                if (h[i] > max) max = h[i];
            }

            // 阈值取这一圈峰值的相对比例：叶脉宽度随半径变化，用固定阈值会把
            // 宽脉上的细小起伏也数成脉。
            float cut = Mathf.Max(0.10f, max * 0.55f);
            var peaks = new List<float>();

            for (int i = 0; i < samples; i++)
            {
                int p = (i - 1 + samples) % samples;
                int n = (i + 1) % samples;
                if (h[i] >= h[p] && h[i] > h[n] && h[i] > cut)
                    peaks.Add(2f * Mathf.PI * i / samples);
            }
            return peaks;
        }

        /// <summary>波峰相对"每 2π/18 一条均匀辐条"的最大角偏差，用来证明叶脉是弯的。</summary>
        static float MaxVeinDeviation(float r)
        {
            var peaks = VeinAngles(r);
            float worst = 0f;

            for (int i = 0; i < peaks.Count; i++)
            {
                // 折回到最近的均匀格点，量角度差
                float k = peaks[i] * 18f / (2f * Mathf.PI);
                float err = Mathf.Abs(Mathf.Repeat(k + 0.5f, 1f) - 0.5f) * (2f * Mathf.PI / 18f);
                if (err > worst) worst = err;
            }
            return worst;
        }

        /// <summary>找离某个平面坐标最近的一块台面。</summary>
        static LiftInfo Nearest(StageGrid grid, Vector2 plan)
        {
            LiftInfo best = null;
            float bestD = float.MaxValue;

            for (int i = 0; i < grid.Count; i++)
            {
                float d = (grid.lifts[i].plan - plan).sqrMagnitude;
                if (d < bestD) { bestD = d; best = grid.lifts[i]; }
            }
            return best;
        }

        static bool HasIssue(List<StageIssue> issues, string keyword)
        {
            for (int i = 0; i < issues.Count; i++)
                if (issues[i].message.IndexOf(keyword, System.StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        static int Check(string label, bool ok)
        {
            if (ok) Debug.Log("[Verify] ok   " + label);
            else Debug.LogError("[Verify] FAIL " + label);
            return ok ? 0 : 1;
        }

        /// <summary>
        /// 独立算一遍"哪些格子在圆外、应该不存在"。
        /// 判据是格子离原点最近的点是否超出半径——这和裁剪实现是两套算法，可以互相印证。
        /// </summary>
        static void CountExpected(int columns, int rows, float cellSize, float radius,
                                  out int alive, out int gone)
        {
            alive = 0;
            gone = 0;
            float half = cellSize * 0.5f;

            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < columns; c++)
                {
                    float cx = (c - (columns - 1) * 0.5f) * cellSize;
                    float cy = ((rows - 1) * 0.5f - r) * cellSize;
                    float nx = Mathf.Max(0f, Mathf.Abs(cx) - half);
                    float ny = Mathf.Max(0f, Mathf.Abs(cy) - half);

                    // 用 >= ：格子离圆最近的点正好落在圆上时，实际面积是零，等于不存在
                    if (Mathf.Sqrt(nx * nx + ny * ny) >= radius) gone++;
                    else alive++;
                }
            }
        }

        // ------------------------------------------------------------ 资源

        static void SaveMesh(Mesh mesh, string path)
        {
            if (AssetDatabase.LoadAssetAtPath<Mesh>(path) != null) AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(mesh, path);
        }

        static Material CreateMaterial(string path, Color color, float glossiness = 0.22f)
        {
            var shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
            if (shader == null) throw new System.Exception("找不到可用的内置着色器");

            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null) AssetDatabase.DeleteAsset(path);

            var mat = new Material(shader);
            mat.color = color;
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", glossiness);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0.35f);

            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        /// <summary>荷叶材质：用自定义着色器，拿到双面、蜡质高光、逆光透光和叶缘渐隐。</summary>
        static Material CreateLotusMaterial(string path, Texture2D color, Texture2D normal)
        {
            var shader = Shader.Find("DigitalPerformance/LotusLeaf");
            if (shader == null)
            {
                Debug.LogWarning("[StageBuilder] 没找到 DigitalPerformance/LotusLeaf，退回 Standard");
                return CreateMaterial(path, Color.white);
            }

            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null) AssetDatabase.DeleteAsset(path);

            var mat = new Material(shader);
            mat.SetTexture("_MainTex", color);
            if (normal != null) mat.SetTexture("_BumpMap", normal);
            if (mat.HasProperty("_RimFade")) mat.SetFloat("_RimFade", 0.08f);

            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        /// <summary>
        /// 台面之间那层底用的材质：和台面同一张叶色贴图，但用不透明着色器，
        /// 这样它能可靠地把缝填上（叶面着色器是半透明的，不适合做底）。
        /// </summary>
        static Material CreateBedMaterial(string path, Texture2D color)
        {
            var shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
            if (shader == null) throw new System.Exception("找不到可用的内置着色器");

            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null) AssetDatabase.DeleteAsset(path);

            var mat = new Material(shader);
            mat.mainTexture = color;
            mat.color = Color.white;
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0.10f);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);

            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        /// <summary>水面材质：涟漪由着色器里的正弦合成，不需要贴图。</summary>
        static Material CreateWaterMaterial(string path)
        {
            var shader = Shader.Find("DigitalPerformance/PondWater");
            if (shader == null)
            {
                Debug.LogWarning("[StageBuilder] 没找到 DigitalPerformance/PondWater，退回 Standard");
                return CreateMaterial(path, new Color(0.035f, 0.075f, 0.085f), 0.85f);
            }

            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null) AssetDatabase.DeleteAsset(path);

            var mat = new Material(shader);
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
