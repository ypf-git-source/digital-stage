#!/usr/bin/env python3
"""合成一段测试音频，用来验证音频驱动模块。

刻意做成三个频段互不串扰，这样"分频段驱动"才验证得出来：
  低频 (~50 Hz)      底鼓，每拍一次            -> 驱动主灯强度（运动）
  中频 (1.2-3.5 kHz) 军鼓，每小节第 2、4 拍    -> 驱动主灯色彩
  高频 (>7 kHz)      踩镲，每八分音符一次      -> 驱动细节灯微闪

每个声源都单独做了带限和起振斜坡：底鼓低通到 200 Hz 以内，噪声簇加 2 ms
起振，否则瞬时跳变会产生宽带咔哒声，污染另外两个频段。

120 BPM，12 秒，24 拍 / 6 小节，和演示工程的表演时长对齐。
"""

import math
import os
import random
import struct
import wave

SR = 44100
DUR = 12.0
BPM = 120.0
BEAT = 60.0 / BPM            # 0.5 s
BAR = BEAT * 4               # 2.0 s
N = int(SR * DUR)


def biquad_coeff(kind, f0, q=0.70710678):
    w0 = 2.0 * math.pi * f0 / SR
    c, s = math.cos(w0), math.sin(w0)
    alpha = s / (2.0 * q)
    if kind == "lp":
        b0, b1, b2 = (1 - c) / 2, 1 - c, (1 - c) / 2
    else:
        b0, b1, b2 = (1 + c) / 2, -(1 + c), (1 + c) / 2
    a0, a1, a2 = 1 + alpha, -2 * c, 1 - alpha
    return b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0


def biquad(x, kind, f0, q=0.70710678):
    b0, b1, b2, a1, a2 = biquad_coeff(kind, f0, q)
    x1 = x2 = y1 = y2 = 0.0
    out = []
    for v in x:
        y = b0 * v + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2
        x2, x1 = x1, v
        y2, y1 = y1, y
        out.append(y)
    return out


random.seed(20261009)
buf = [0.0] * N


def add(t0, samples):
    start = int(t0 * SR)
    for i, v in enumerate(samples):
        if start + i < N:
            buf[start + i] += v


def kick(t0, amp=0.9):
    n = int(0.35 * SR)
    out = []
    phase = 0.0
    for i in range(n):
        t = i / SR
        f = 48.0 + 14.0 * math.exp(-t / 0.090)      # 62 Hz 迅速落到 48 Hz
        phase += 2.0 * math.pi * f / SR
        env = min(1.0, t / 0.012) * math.exp(-t / 0.090)
        out.append(amp * env * math.sin(phase))
    add(t0, biquad(out, "lp", 200.0))               # 掐掉起振的宽带瞬态


def noise_burst(t0, amp, tau, seed, dur, lp=None, hp=None):
    n = int(dur * SR)
    rnd = random.Random(seed)
    x = [rnd.uniform(-1.0, 1.0) for _ in range(n)]
    if lp is not None:
        x = biquad(x, "lp", lp)
    if hp is not None:
        x = biquad(x, "hp", hp)
    add(t0, [amp * min(1.0, (i / SR) / 0.002) * math.exp(-i / SR / tau) * x[i]
             for i in range(n)])


beats = int(DUR / BEAT)                              # 24
for b in range(beats):
    kick(b * BEAT)
    if b % 4 in (1, 3):                              # 每小节第 2、4 拍
        noise_burst(b * BEAT, 5.0, 0.045, 100 + b, 0.25, lp=3500, hp=1200)

for e in range(int(DUR / (BEAT / 2))):               # 八分音符
    noise_burst(e * BEAT / 2, 5.0, 0.010, 900 + e, 0.08, hp=7000)

peak = max(abs(v) for v in buf) or 1.0
scale = 0.92 / peak
fade = int(0.005 * SR)
for i in range(N):
    v = buf[i] * scale
    if i < fade:
        v *= i / fade
    if i > N - fade:
        v *= (N - i) / fade
    buf[i] = v

out_dir = os.path.normpath(
    os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Assets", "Audio"))
os.makedirs(out_dir, exist_ok=True)
out_path = os.path.join(out_dir, "show.wav")

with wave.open(out_path, "wb") as w:
    w.setnchannels(1)
    w.setsampwidth(2)
    w.setframerate(SR)
    frames = bytearray()
    for v in buf:
        frames += struct.pack("<h", int(max(-1.0, min(1.0, v)) * 32767))
    w.writeframes(bytes(frames))

print("wrote", out_path)
print("duration %.2fs  samples %d  beats %d  peak %.3f"
      % (DUR, N, beats, max(abs(v) for v in buf)))
