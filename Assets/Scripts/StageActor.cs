using UnityEngine;

namespace DigitalPerformance
{
    /// <summary>
    /// 一个可被表演脚本驱动的演员。id 要和 cue 表里的 target 对应。
    /// </summary>
    public class StageActor : MonoBehaviour
    {
        public string id = "P1";
        public Renderer rend;
        public Light light;
    }
}
