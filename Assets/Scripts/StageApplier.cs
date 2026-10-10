using UnityEngine;

namespace DigitalPerformance
{
    /// <summary>表演状态。解释器算出它，执行器把它写进场景。</summary>
    public struct ParamState
    {
        public Vector3 position;
        public Vector3 rotation;
        public Vector3 scale;
        public Color color;
        public float intensity;
    }

    /// <summary>
    /// 执行器 / 设备抽象层。
    ///
    /// 屏幕上的物体、灯、音响、串口设备，最终都实现同一个 Apply；
    /// 表演逻辑只认识 ParamState，不认识任何具体设备。要接新硬件时，
    /// 只需要在这里加一个实现，时间轴那一侧一行都不用改。
    /// </summary>
    public class StageApplier
    {
        static readonly int BaseColorProp = Shader.PropertyToID("_BaseColor");
        static readonly int ColorProp = Shader.PropertyToID("_Color");

        readonly MaterialPropertyBlock _block = new MaterialPropertyBlock();

        public void Apply(Transform tr, Renderer rend, Light light, ParamState st)
        {
            if (tr != null)
            {
                tr.localPosition = st.position;
                tr.localEulerAngles = st.rotation;
                tr.localScale = st.scale;
            }

            if (rend != null && rend.sharedMaterial != null)
            {
                var shared = rend.sharedMaterial;
                int prop = shared.HasProperty(BaseColorProp) ? BaseColorProp : ColorProp;
                _block.Clear();
                _block.SetColor(prop, st.color);
                rend.SetPropertyBlock(_block);
            }

            // 同一条 color / intensity 通道，落到灯具上就是灯的颜色和强度。
            // 表演逻辑一行都不用改，这就是设备抽象层的意义。
            if (light != null)
            {
                light.color = st.color;
                light.intensity = st.intensity;
            }
        }

        /// <summary>读出场时物体的初始颜色，作为求值的起点。</summary>
        public static Color ReadColor(Renderer rend, Color fallback)
        {
            if (rend == null || rend.sharedMaterial == null) return fallback;
            var shared = rend.sharedMaterial;
            return shared.HasProperty(BaseColorProp) ? shared.GetColor(BaseColorProp) : shared.color;
        }

        public static float ReadIntensity(Light light, float fallback)
        {
            return light != null ? light.intensity : fallback;
        }
    }
}
