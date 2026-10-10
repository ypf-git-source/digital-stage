using UnityEngine;

namespace DigitalPerformance
{
    /// <summary>
    /// 相机云台：机位由 cue 表驱动，朝向永远看向 Focus。
    /// 朝向不做阻尼，是为了保持"任意时刻都能由 t 独立算出来"。
    /// </summary>
    public class CameraRig : MonoBehaviour
    {
        public Transform focus;
        public bool lookAtFocus = true;

        void LateUpdate()
        {
            if (!lookAtFocus || focus == null) return;
            transform.LookAt(focus.position);
        }
    }
}
