using UnityEngine;

namespace DigitalPerformance
{
    /// <summary>
    /// 把 AudioSource 从属于时间轴。
    ///
    /// 时间轴是主、音频是从：每帧只检查漂移，超过阈值才纠正一次，
    /// 而不是每帧都去设 source.time（那样会有咔哒声）。
    ///
    /// 反过来让音频当主时钟是不行的——那样拖动进度条就没法回看，
    /// 整场演出也不再可复现。真实演出里音频的时间码是主时钟，
    /// 但那是硬件时间码（LTC/MTC），不是 AudioSource 的播放位置。
    /// </summary>
    [AddComponentMenu("Digital Performance/Audio Sync")]
    public class AudioSync : MonoBehaviour
    {
        public PerformanceDirector director;
        public AudioSource source;
        public float resyncThreshold = 0.06f;

        void Start()
        {
            if (source == null) return;
            source.playOnAwake = false;
            source.loop = false;
        }

        void Update()
        {
            if (director == null || source == null || source.clip == null) return;

            float t = Mathf.Clamp(director.CurrentTime, 0f, Mathf.Max(0f, source.clip.length - 0.02f));

            if (director.playing)
            {
                if (!source.isPlaying)
                {
                    source.time = t;
                    source.Play();
                }
                else if (Mathf.Abs(source.time - t) > resyncThreshold)
                {
                    source.time = t;
                }
            }
            else
            {
                if (source.isPlaying) source.Pause();
                if (Mathf.Abs(source.time - t) > resyncThreshold) source.time = t;
            }
        }
    }
}
