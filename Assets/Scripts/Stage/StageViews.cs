using UnityEngine;

namespace DigitalPerformance
{
    /// <summary>
    /// 多视角观察：正面、侧面、俯视三台相机。
    ///
    /// 相机的机位每帧由这里重算，所以直接去 Inspector 里拖相机的位移是留不住的。
    /// 想调构图就调这里的 distance / rise / fov，改完立刻生效，编辑态也生效，不用按 Play。
    /// 想让相机完全自己管，把 driveCameras 关掉，这里就不再碰它们。
    ///
    /// 播放时：滚轮 拉近拉远，Q / E 升降机位，Z / X 改视场角，V 换视角，H 收起面板。
    /// 面板脚本读 StageViews.HudVisible，所以 H 键能一键让出画面。
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Digital Performance/Stage Views")]
    public class StageViews : MonoBehaviour
    {
        public Camera front;
        public Camera side;
        public Camera top;

        [Tooltip("看向哪里，一般是舞台顶面中心")]
        public Transform focus;

        [Header("机位，改完立刻生效")]
        public float distance = 16f;
        public float rise = 4.5f;
        public float fov = 45f;

        [Tooltip("0 分屏(正+侧)  1 只看正面  2 只看侧面  3 只看俯视")]
        public int mode = 0;

        [Header("俯视用正交投影，才是一张真正的平面图")]
        public bool topOrthographic = true;
        public float topSize = 4.6f;

        [Tooltip("关掉之后这里不再摆相机，相机可以自己拖")]
        public bool driveCameras = true;

        public bool allowKeySwitch = true;

        public static readonly string[] ModeNames = { "正面+侧面", "正面", "侧面", "俯视" };

        public string ModeName
        {
            get { return ModeNames[Mathf.Clamp(mode, 0, ModeNames.Length - 1)]; }
        }

        readonly Camera[] _all = new Camera[3];

        /// <summary>返回屏幕坐标落在哪台相机的视口里。分屏时鼠标在哪半边就用哪台。</summary>
        public Camera CameraAt(Vector2 screenPoint)
        {
            _all[0] = front;
            _all[1] = side;
            _all[2] = top;

            for (int i = 0; i < _all.Length; i++)
            {
                var cam = _all[i];
                if (cam == null || !cam.enabled) continue;
                if (cam.pixelRect.Contains(screenPoint)) return cam;
            }
            return null;
        }

        public const float MinDistance = 5f;
        public const float MaxDistance = 80f;

        /// <summary>H 键收起所有面板，面板脚本读这个开关。</summary>
        public static bool HudVisible = true;

        bool _dirty = true;
        int _lastMode = -1;
        float _lastDistance, _lastRise, _lastFov, _lastTopSize;
        Vector3 _lastFocus;

        Vector3 FocusPoint
        {
            get { return focus != null ? focus.position : Vector3.zero; }
        }

        void OnEnable()
        {
            _dirty = true;
        }

        void Update()
        {
            if (Application.isPlaying && allowKeySwitch) HandleInput();

            // 只在参数真的变了才重摆，否则编辑态每帧重设会一直把场景标脏
            if (driveCameras && NeedsLayout()) Layout();
        }

        bool NeedsLayout()
        {
            if (_dirty) return true;
            if (mode != _lastMode) return true;
            if (!Mathf.Approximately(distance, _lastDistance)) return true;
            if (!Mathf.Approximately(rise, _lastRise)) return true;
            if (!Mathf.Approximately(fov, _lastFov)) return true;
            if (!Mathf.Approximately(topSize, _lastTopSize)) return true;
            if ((FocusPoint - _lastFocus).sqrMagnitude > 1e-6f) return true;
            return false;
        }

        void HandleInput()
        {
            // Shift + 滚轮留给"单独调整某块台面"，这里只处理纯滚轮缩放
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (!shift && Mathf.Abs(scroll) > 0.0001f)
            {
                // 俯视是正交相机，改的是取景范围，不是机位远近
                if (mode == 3 && topOrthographic)
                    topSize = Mathf.Clamp(topSize * (1f - scroll * 2f), 2.5f, 12f);
                else
                    distance = Mathf.Clamp(distance * (1f - scroll * 3f), MinDistance, MaxDistance);
            }

            if (Input.GetKey(KeyCode.Q)) rise = Mathf.Clamp(rise + 9f * Time.deltaTime, -6f, 40f);
            if (Input.GetKey(KeyCode.E)) rise = Mathf.Clamp(rise - 9f * Time.deltaTime, -6f, 40f);
            if (Input.GetKey(KeyCode.Z)) fov = Mathf.Clamp(fov + 25f * Time.deltaTime, 15f, 90f);
            if (Input.GetKey(KeyCode.X)) fov = Mathf.Clamp(fov - 25f * Time.deltaTime, 15f, 90f);
            if (Input.GetKeyDown(KeyCode.V)) mode = (mode + 1) % 4;
            if (Input.GetKeyDown(KeyCode.H)) HudVisible = !HudVisible;
        }

        public void Layout()
        {
            var f = FocusPoint;

            var left = new Rect(0f, 0f, 0.5f, 1f);
            var right = new Rect(0.5f, 0f, 0.5f, 1f);
            var full = new Rect(0f, 0f, 1f, 1f);

            Apply(front, f + new Vector3(0f, rise, -distance), false, false, mode == 0 || mode == 1, mode == 0 ? left : full);
            Apply(side, f + new Vector3(distance, rise, 0f), false, false, mode == 0 || mode == 2, mode == 0 ? right : full);
            Apply(top, f + new Vector3(0f, distance * 1.2f, 0f), true, topOrthographic, mode == 3, full);

            _dirty = false;
            _lastMode = mode;
            _lastDistance = distance;
            _lastRise = rise;
            _lastFov = fov;
            _lastTopSize = topSize;
            _lastFocus = f;
        }

        void Apply(Camera cam, Vector3 position, bool straightDown, bool ortho, bool on, Rect rect)
        {
            if (cam == null) return;

            cam.transform.position = position;

            if (straightDown)
            {
                // 正上方俯视时用 LookAt 会退化，直接给定欧拉角
                cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            }
            else if (focus != null)
            {
                cam.transform.LookAt(focus);
            }

            cam.orthographic = ortho;
            if (ortho) cam.orthographicSize = topSize;
            else cam.fieldOfView = fov;

            cam.rect = rect;
            cam.enabled = on;
        }
    }
}
