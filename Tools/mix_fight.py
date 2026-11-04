import re, sys, wave, subprocess, os, shutil
import numpy as np
import imageio_ffmpeg

root = r'C:\Users\dom12\Documents\unity_test'
rec = sys.argv[1]; out_mp4 = sys.argv[2]
FPS = 30; N = 600
ff = imageio_ffmpeg.get_ffmpeg_exe()

# fill dropped frames by repeating the previous one
frames = os.path.join(rec, 'rec'); prev = None
for i in range(1, N + 1):
    p = os.path.join(frames, f'f_{i:05d}.png')
    if os.path.exists(p): prev = p
    elif prev: shutil.copy(prev, p)
    else:
        nxt = next((os.path.join(frames, f'f_{j:05d}.png') for j in range(i, N + 1) if os.path.exists(os.path.join(frames, f'f_{j:05d}.png'))), None)
        shutil.copy(nxt, p); prev = p

# manifest from CueBus.cs
src = open(os.path.join(root, 'Assets', 'Scripts', 'Combat', 'CueBus.cs'), encoding='utf-8').read()
man = {}
for m in re.finditer(r'\{ "(\w+)", new\[\] \{ ([^}]*) \} \}', src):
    nums = [float(x.rstrip('f')) for x in m.group(2).replace('f,', ',').replace('f', '').split(',') if x.strip()]
    man[m.group(1)] = [(nums[i], nums[i + 1]) for i in range(0, len(nums), 2)]

w = wave.open(os.path.join(root, 'Assets', 'Audio', 'sprite.wav'))
hz = w.getframerate(); data = np.frombuffer(w.readframes(w.getnframes()), dtype=np.int16).astype(np.float32) / 32768.0
total = int((N / FPS + 1.5) * hz); mix = np.zeros(total, dtype=np.float32)
for line in open(os.path.join(rec, 'cues.txt')):
    t, name, idx, vol, pitch = line.strip().split('\t'); t = float(t); idx = int(idx); vol = float(vol); pitch = float(pitch)
    if name not in man: continue
    s0, dur = man[name][idx]; seg = data[int(s0 * hz): int((s0 + dur) * hz)].copy()
    f = min(len(seg) // 4, int(0.008 * hz)); ramp = np.linspace(0, 1, f, dtype=np.float32); seg[:f] *= ramp; seg[-f:] *= ramp[::-1]
    if abs(pitch - 1) > 1e-3:
        n = int(len(seg) / pitch); seg = np.interp(np.linspace(0, len(seg) - 1, n), np.arange(len(seg)), seg).astype(np.float32)
    o = int(t * hz); e = min(total, o + len(seg))
    if o < total: mix[o:e] += seg[:e - o] * vol
# soft limiter, no pumping: tanh above a knee
mix = np.tanh(mix * 1.4) / np.tanh(1.4) * 0.9
wav_path = os.path.join(rec, 'fight_mix.wav')
with wave.open(wav_path, 'wb') as ow:
    ow.setnchannels(1); ow.setsampwidth(2); ow.setframerate(hz); ow.writeframes((np.clip(mix, -1, 1) * 32767).astype(np.int16).tobytes())
subprocess.run([ff, '-y', '-framerate', str(FPS), '-i', os.path.join(frames, 'f_%05d.png'), '-i', wav_path, '-t', str(N / FPS),
                '-c:v', 'libx264', '-preset', 'medium', '-crf', '26', '-pix_fmt', 'yuv420p', '-vf', 'scale=1920:1080', '-c:a', 'aac', '-b:a', '128k', '-shortest', out_mp4], check=True)
print(os.path.getsize(out_mp4) / 1e6, 'MB')
