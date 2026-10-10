using System.Collections.Generic;
using UnityEngine;

namespace DigitalPerformance
{
    /// <summary>
    /// 把台面多边形挤成一个棱柱：顶面朝上、侧面朝外。
    ///
    /// 顶面在局部坐标 y = 0，柱体向下延伸 thickness，所以把物体的 y 设成台面高度就是台面高度本身。
    ///
    /// 顶点要减去 origin（台面的格心）变成**以自身格心为原点**的坐标。
    /// 这一步不能省：传入的多边形用的是舞台中心坐标系，而物体本身会被摆到格心上，
    /// 不减的话偏移会算两遍，台面就会各飞各的——整块的会整体平移，被弧裁的会向外散开。
    /// </summary>
    public static class StageMesh
    {
        public static Mesh BuildPrism(Vector2[] poly, Vector2 origin, float thickness,
                                      float uvRadius, string name)
        {
            int n = poly.Length;
            var verts = new List<Vector3>(n * 4 + 2);
            var norms = new List<Vector3>(n * 4 + 2);
            var uvs = new List<Vector2>(n * 4 + 2);
            var tris = new List<int>(n * 12);

            // 转到以格心为原点的局部坐标
            var p = new Vector2[n];
            for (int i = 0; i < n; i++) p[i] = poly[i] - origin;

            var c = Vector2.zero;
            for (int i = 0; i < n; i++) c += p[i];
            c /= n;

            // ---- 顶面：从质心拉扇面。绕序要让 Cross(v1-v0, v2-v0) 指向 +y。
            int topCenter = verts.Count;
            verts.Add(new Vector3(c.x, 0f, c.y));
            norms.Add(Vector3.up);
            uvs.Add(StageUv(c, origin, uvRadius));

            int topRing = verts.Count;
            for (int i = 0; i < n; i++)
            {
                verts.Add(new Vector3(p[i].x, 0f, p[i].y));
                norms.Add(Vector3.up);
                // 顶面 UV 用舞台坐标归一化，不是逐块的局部坐标。
                // 这样一张纹理铺满整个圆，叶脉跨台面也是连续的。
                uvs.Add(StageUv(p[i], origin, uvRadius));
            }
            for (int i = 0; i < n; i++)
            {
                int a = topRing + i;
                int b = topRing + (i + 1) % n;
                tris.Add(topCenter);
                tris.Add(b);
                tris.Add(a);
            }

            // ---- 底面（朝下），从下面看才不会漏。
            int botCenter = verts.Count;
            verts.Add(new Vector3(c.x, -thickness, c.y));
            norms.Add(Vector3.down);
            uvs.Add(StageUv(c, origin, uvRadius));

            int botRing = verts.Count;
            for (int i = 0; i < n; i++)
            {
                verts.Add(new Vector3(p[i].x, -thickness, p[i].y));
                norms.Add(Vector3.down);
                uvs.Add(StageUv(p[i], origin, uvRadius));
            }
            for (int i = 0; i < n; i++)
            {
                int a = botRing + i;
                int b = botRing + (i + 1) % n;
                tris.Add(botCenter);
                tris.Add(a);
                tris.Add(b);
            }

            // ---- 侧面
            int sideBase = verts.Count;
            for (int i = 0; i < n; i++)
            {
                Vector2 a = p[i];
                Vector2 b = p[(i + 1) % n];
                var d = b - a;
                var outward = new Vector3(d.y, 0f, -d.x).normalized;

                verts.Add(new Vector3(a.x, 0f, a.y));
                verts.Add(new Vector3(a.x, -thickness, a.y));
                norms.Add(outward);
                norms.Add(outward);

                // 侧面统一取纹理中点（叶肉的中绿），这样俯视时露出的侧面不会是黑的
                uvs.Add(new Vector2(0.5f, 0.5f));
                uvs.Add(new Vector2(0.5f, 0.5f));

                int t0 = sideBase + i * 2;
                int t1 = sideBase + i * 2 + 1;
                int t2 = sideBase + ((i + 1) % n) * 2;
                int t3 = t2 + 1;

                // 绕序要让法线朝外：Cross(top_i→top_j, top_i→bot_j) = (d.y, 0, -d.x)
                tris.Add(t0); tris.Add(t2); tris.Add(t3);
                tris.Add(t0); tris.Add(t3); tris.Add(t1);
            }

            var mesh = new Mesh { name = name };
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        static Vector2 StageUv(Vector2 local, Vector2 origin, float uvRadius)
        {
            if (uvRadius <= 0f) return local;
            return new Vector2(local.x + origin.x + uvRadius, local.y + origin.y + uvRadius)
                   / (2f * uvRadius);
        }

        /// <summary>凸多边形的质心（按面积加权）。用来检查扇心确实在多边形内部。</summary>
        public static Vector2 Centroid(Vector2[] p)
        {
            float a2 = 0f;
            var c = Vector2.zero;
            for (int i = 0, j = p.Length - 1; i < p.Length; j = i++)
            {
                float cross = p[j].x * p[i].y - p[i].x * p[j].y;
                a2 += cross;
                c += (p[j] + p[i]) * cross;
            }
            if (Mathf.Abs(a2) < 1e-9f) return Vector2.zero;
            return c / (3f * a2);
        }

        /// <summary>点是否在凸多边形内（含边界）。</summary>
        public static bool ContainsConvex(Vector2[] p, Vector2 pt)
        {
            int sign = 0;
            for (int i = 0, j = p.Length - 1; i < p.Length; j = i++)
            {
                float cross = (p[i].x - p[j].x) * (pt.y - p[j].y) - (p[i].y - p[j].y) * (pt.x - p[j].x);
                if (Mathf.Abs(cross) < 1e-6f) continue;
                int s = cross > 0f ? 1 : -1;
                if (sign == 0) sign = s;
                else if (sign != s) return false;
            }
            return true;
        }
    }
}
