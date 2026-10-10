using UnityEngine;

namespace DigitalPerformance
{
    /// <summary>
    /// 鼠标点选台面。
    ///
    /// 不走物理射线：台面是运行时按多边形挤出来的网格，没有碰撞体，加碰撞体又得跟着
    /// 台面高度每帧重建。这里直接把鼠标射线和每块台面的顶面求交，再判断交点是否落在
    /// 那块台面的多边形里；高的台面优先，所以俯视和侧视都能点得准。
    /// </summary>
    public static class StagePicker
    {
        public static LiftInfo Pick(StageGrid grid, float[] heights, float elevation, Ray ray,
                                    out Vector3 hitPoint)
        {
            hitPoint = Vector3.zero;
            if (grid == null) return null;

            LiftInfo best = null;
            float bestY = float.MinValue;

            for (int i = 0; i < grid.Count; i++)
            {
                var li = grid.lifts[i];
                float y = elevation + ((heights != null && i < heights.Length) ? heights[i] : 0f);

                var plane = new Plane(Vector3.up, new Vector3(li.plan.x, y, li.plan.y));
                float dist;
                if (!plane.Raycast(ray, out dist)) continue;

                var p = ray.GetPoint(dist);
                if (!StageMesh.ContainsConvex(li.polygon, new Vector2(p.x, p.z))) continue;

                if (y > bestY)
                {
                    bestY = y;
                    best = li;
                    hitPoint = p;
                }
            }

            return best;
        }
    }
}
