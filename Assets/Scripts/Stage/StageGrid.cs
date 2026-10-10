using System.Collections.Generic;
using UnityEngine;

namespace DigitalPerformance
{
    /// <summary>一块升降台的静态信息。</summary>
    public class LiftInfo
    {
        public int index;             // 在 StageGrid.lifts 里的下标
        public int row;               // 0..5，0 是最后排
        public int col;               // 0..5，0 是最左
        public string id;             // "r3c4"（1 基，方便人读写）
        public Vector2 plan;          // 台面中心的平面坐标（米），原点在舞台中心
        public Vector2[] polygon;     // 被圆裁切后的台面轮廓（逆时针，米）
        public float area;            // 台面面积，平方米
        public bool clipped;          // 是否被圆弧裁过
        public int ring;              // 切比雪夫环号：0 内圈 / 1 中圈 / 2 外圈
    }

    /// <summary>
    /// 方格升降台的几何。
    ///
    /// 整体外轮廓是半径 R 的圆，而台面本身是方格，所以边缘台面要按「圆 ∩ 方格」裁出弧形边。
    /// 这个交是凸的，于是有两个便宜可占：三角化可以直接从质心拉扇面，不用耳切；
    /// 顶点按到质心的极角排序就是正确的逆时针顺序，不用做边界行走。
    ///
    /// 半径取内切值时（6×6 取 R=3、8×8 取 R=4），台面拼出来正好是一个正圆；
    /// 代价是越靠外的台面被弧裁得越多。8×8 时四个角块整个落在圆外，直接不存在，
    /// 所以是 60 块而不是 64 块，但面积之和仍然等于 πR²。
    /// </summary>
    public class StageGrid
    {
        public int columns = 6;
        public int rows = 6;

        public float cellSize = 1f;   // 标称格距
        public float gap = 0.02f;     // 台面之间的缝，防止互相刮蹭
        public float radius = 3f;     // 圆半径

        public readonly List<LiftInfo> lifts = new List<LiftInfo>();

        public int Count { get { return lifts.Count; } }
        public float PlateSize { get { return cellSize - gap; } }

        public static StageGrid Create(int columns = 6, int rows = 6,
                                       float cellSize = 1f, float gap = 0.02f, float radius = 3f)
        {
            var grid = new StageGrid
            {
                columns = Mathf.Max(2, columns),
                rows = Mathf.Max(2, rows),
                cellSize = cellSize,
                gap = gap,
                radius = radius,
            };
            grid.Build();
            return grid;
        }

        void Build()
        {
            lifts.Clear();
            float half = PlateSize * 0.5f;

            for (int row = 0; row < rows; row++)
            {
                for (int col = 0; col < columns; col++)
                {
                    // 原点在舞台中心：col 向右为 +x，row 向后为 +y（映射到 Unity 的 +z）
                    float cx = (col - (columns - 1) * 0.5f) * cellSize;
                    float cy = ((rows - 1) * 0.5f - row) * cellSize;
                    var center = new Vector2(cx, cy);

                    var poly = ClipSquareToDisk(center, half, radius);
                    if (poly == null || poly.Length < 3) continue;   // 完全在圆外，这台不存在

                    float area = SignedArea(poly);

                    lifts.Add(new LiftInfo
                    {
                        index = lifts.Count,
                        row = row,
                        col = col,
                        id = "r" + (row + 1) + "c" + (col + 1),
                        plan = center,
                        polygon = poly,
                        area = Mathf.Abs(area),
                        clipped = poly.Length > 4,
                        ring = Mathf.Max(
                            Mathf.CeilToInt(Mathf.Abs(col - (columns - 1) * 0.5f) - 0.5f),
                            Mathf.CeilToInt(Mathf.Abs(row - (rows - 1) * 0.5f) - 0.5f)),
                    });
                }
            }
        }

        /// <summary>把一块方格裁成 圆 ∩ 方格，返回逆时针多边形。</summary>
        static Vector2[] ClipSquareToDisk(Vector2 c, float half, float r)
        {
            float x0 = c.x - half, x1 = c.x + half;
            float y0 = c.y - half, y1 = c.y + half;
            float r2 = r * r;

            var pts = new List<Vector2>(260);

            // 1) 落在圆内的角点
            if (new Vector2(x0, y0).sqrMagnitude <= r2) pts.Add(new Vector2(x0, y0));
            if (new Vector2(x1, y0).sqrMagnitude <= r2) pts.Add(new Vector2(x1, y0));
            if (new Vector2(x1, y1).sqrMagnitude <= r2) pts.Add(new Vector2(x1, y1));
            if (new Vector2(x0, y1).sqrMagnitude <= r2) pts.Add(new Vector2(x0, y1));

            // 2) 四条边与圆的交点
            if (Mathf.Abs(x0) < r)
            {
                float y = Mathf.Sqrt(r2 - x0 * x0);
                if (y >= y0 && y <= y1) pts.Add(new Vector2(x0, y));
                if (-y >= y0 && -y <= y1) pts.Add(new Vector2(x0, -y));
            }
            if (Mathf.Abs(x1) < r)
            {
                float y = Mathf.Sqrt(r2 - x1 * x1);
                if (y >= y0 && y <= y1) pts.Add(new Vector2(x1, y));
                if (-y >= y0 && -y <= y1) pts.Add(new Vector2(x1, -y));
            }
            if (Mathf.Abs(y0) < r)
            {
                float x = Mathf.Sqrt(r2 - y0 * y0);
                if (x >= x0 && x <= x1) pts.Add(new Vector2(x, y0));
                if (-x >= x0 && -x <= x1) pts.Add(new Vector2(-x, y0));
            }
            if (Mathf.Abs(y1) < r)
            {
                float x = Mathf.Sqrt(r2 - y1 * y1);
                if (x >= x0 && x <= x1) pts.Add(new Vector2(x, y1));
                if (-x >= x0 && -x <= x1) pts.Add(new Vector2(-x, y1));
            }

            // 3) 落在方格内的圆弧点
            const int arcSteps = 180;   // 2° 一段
            for (int i = 0; i < arcSteps; i++)
            {
                float a = i * 2f * Mathf.PI / arcSteps;
                float x = Mathf.Cos(a) * r;
                float y = Mathf.Sin(a) * r;
                if (x >= x0 && x <= x1 && y >= y0 && y <= y1) pts.Add(new Vector2(x, y));
            }

            if (pts.Count < 3) return null;

            // 4) 去重（交点和弧点会算出同一个位置）
            var uniq = new List<Vector2>(pts.Count);
            for (int i = 0; i < pts.Count; i++)
            {
                bool dup = false;
                for (int k = 0; k < uniq.Count; k++)
                {
                    if ((uniq[k] - pts[i]).sqrMagnitude < 1e-8f) { dup = true; break; }
                }
                if (!dup) uniq.Add(pts[i]);
            }
            if (uniq.Count < 3) return null;

            // 5) 形状是凸的，按到质心的极角排序即可得到正确的逆时针顺序
            var cen = Vector2.zero;
            for (int i = 0; i < uniq.Count; i++) cen += uniq[i];
            cen /= uniq.Count;

            uniq.Sort((a, b) =>
                Mathf.Atan2(a.y - cen.y, a.x - cen.x)
                    .CompareTo(Mathf.Atan2(b.y - cen.y, b.x - cen.x)));

            return uniq.ToArray();
        }

        /// <summary>带符号面积，逆时针为正。</summary>
        public static float SignedArea(Vector2[] p)
        {
            float sum = 0f;
            for (int i = 0, j = p.Length - 1; i < p.Length; j = i++)
                sum += p[j].x * p[i].y - p[i].x * p[j].y;
            return sum * 0.5f;
        }

        public LiftInfo Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            for (int i = 0; i < lifts.Count; i++)
                if (lifts[i].id == id) return lifts[i];
            return null;
        }

        public LiftInfo At(int row, int col)
        {
            for (int i = 0; i < lifts.Count; i++)
                if (lifts[i].row == row && lifts[i].col == col) return lifts[i];
            return null;
        }

        /// <summary>找出包含某个平面位置的那块台面；落在圆外（缝里）就取最近的一块。</summary>
        public LiftInfo LiftAt(Vector2 plan)
        {
            LiftInfo nearest = null;
            float best = float.MaxValue;

            for (int i = 0; i < lifts.Count; i++)
            {
                var li = lifts[i];
                if (StageMesh.ContainsConvex(li.polygon, plan)) return li;

                float d = (li.plan - plan).sqrMagnitude;
                if (d < best) { best = d; nearest = li; }
            }
            return nearest;
        }

        /// <summary>
        /// 作用范围。语言层只需要说"外圈""中间那排"，不用列 36 个编号。
        /// 支持：all / ring:2 / disk:2.5 / row:3 / col:4 / quadrant:1 / corner:3
        /// </summary>
        public bool InScope(string scope, LiftInfo li)
        {
            if (string.IsNullOrEmpty(scope) || scope == "all") return true;

            int colon = scope.IndexOf(':');
            string key = colon < 0 ? scope : scope.Substring(0, colon);
            string arg = colon < 0 ? "" : scope.Substring(colon + 1);

            switch (key)
            {
                case "ring":
                    return li.ring == Mathf.RoundToInt(Parse(arg, -1f));
                case "disk":
                    return li.plan.magnitude <= Parse(arg, 0f) + 1e-4f;
                case "row":
                    return li.row + 1 == Mathf.RoundToInt(Parse(arg, -1f));
                case "col":
                    return li.col + 1 == Mathf.RoundToInt(Parse(arg, -1f));
                case "quadrant":
                {
                    int q = Mathf.RoundToInt(Parse(arg, -1f));
                    bool right = li.col >= columns / 2;
                    bool back = li.row < rows / 2;
                    if (q == 1) return !right && back;
                    if (q == 2) return right && back;
                    if (q == 3) return !right && !back;
                    if (q == 4) return right && !back;
                    return false;
                }
                case "corner":
                {
                    int k = Mathf.RoundToInt(Parse(arg, -1f));
                    bool left = li.col < columns / 2;
                    bool back = li.row < rows / 2;
                    if (k == 1) return left && back;
                    if (k == 2) return !left && back;
                    if (k == 3) return left && !back;
                    if (k == 4) return !left && !back;
                    return false;
                }
            }

            return true;   // 认不出来的范围一律当全场，宁可动得多也不要静默不动
        }

        static float Parse(string s, float fallback)
        {
            float v;
            return float.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v) ? v : fallback;
        }
    }
}
