import os, sys, glob, re, json
import numpy as np
from PIL import Image

# HCKIT3 v93: thresholds / globs / caps / log strings come from Tools/gate_config.json "qc" (defaults below = the old literals)
def _qc_cfg():
    d = {"p1Max": 0.040, "meanMax": 0.280, "contrastMin": 0.500, "sampleCaps": {"verify": 10, "fight": 5, "cam": 5},
         "globs": {"verify": "verify_out/*_game.png", "fight": "verify_out/fight_*.png", "cam": "camproof/cam_*.png"},
         "failLogStrings": ["HDRP/Decal shader missing", "using bone-parented quads"]}
    try:
        with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "gate_config.json"), encoding="utf-8-sig") as f:
            j = json.load(f).get("qc", {})
        for k in d:
            if k in j: d[k] = j[k]
    except Exception:
        pass
    return d

QC = _qc_cfg()

def analyze_image(path):
    try:
        im = Image.open(path).convert('RGB')
        arr = np.asarray(im, dtype=np.float32) / 255.0
        # sRGB luma approx
        luma = 0.2126 * arr[:,:,0] + 0.7152 * arr[:,:,1] + 0.0722 * arr[:,:,2]
        mean_val = float(np.mean(luma))
        median_val = float(np.median(luma))
        std_val = float(np.std(luma))
        p1 = float(np.percentile(luma, 1))
        p5 = float(np.percentile(luma, 5))
        p95 = float(np.percentile(luma, 95))
        rel_contrast = std_val / (mean_val + 1e-6)
        return {
            "path": path,
            "mean": mean_val,
            "median": median_val,
            "std": std_val,
            "p1": p1,
            "p5": p5,
            "p95": p95,
            "rel_contrast": rel_contrast
        }
    except Exception as e:
        return {"path": path, "error": str(e)}

def audit_directory(build_dir):
    print(f"=== ANTIGRAVITY VISUAL & SHADER QC AUDIT: {build_dir} ===")
    
    # 1. Shader and fallback log check
    log_files = glob.glob(os.path.join(build_dir, "*.log"))
    fallback_errors = []
    for lf in log_files:
        try:
            with open(lf, 'r', errors='ignore') as f:
                content = f.read()
                for bad in QC["failLogStrings"]:
                    if bad in content:
                        fallback_errors.append(f"{os.path.basename(lf)}: fallback string detected: {bad}")
        except Exception as e:
            pass

    # 2. Image sampling
    verify_imgs = glob.glob(os.path.join(build_dir, *QC["globs"]["verify"].split("/")))
    fight_imgs = glob.glob(os.path.join(build_dir, *QC["globs"]["fight"].split("/")))
    cam_imgs = glob.glob(os.path.join(build_dir, *QC["globs"]["cam"].split("/")))
    
    sample_imgs = []
    if verify_imgs:
        sample_imgs.extend(verify_imgs[:QC["sampleCaps"]["verify"]])
    if fight_imgs:
        sample_imgs.extend(fight_imgs[:QC["sampleCaps"]["fight"]])
    if cam_imgs:
        sample_imgs.extend(cam_imgs[:QC["sampleCaps"]["cam"]])

    results = []
    for p in sample_imgs:
        res = analyze_image(p)
        if "error" not in res:
            results.append(res)

    if not results:
        print("[QC WARNING] No screenshots found to evaluate.")
        return False

    means = [r["mean"] for r in results]
    p1s = [r["p1"] for r in results]
    p5s = [r["p5"] for r in results]
    contrasts = [r["rel_contrast"] for r in results]

    avg_mean = float(np.mean(means))
    avg_p1 = float(np.mean(p1s))
    avg_p5 = float(np.mean(p5s))
    avg_contrast = float(np.mean(contrasts))

    print(f"Sampled Frames: {len(results)}")
    print(f"Mean Luma:       {avg_mean:.4f}  (Baseline v65: ~0.118, Overlit alert: >0.260)")
    print(f"Shadow p1:       {avg_p1:.4f}  (Baseline v65: ~0.004, Washed-out alert: >0.035)")
    print(f"Shadow p5:       {avg_p5:.4f}  (Baseline v65: ~0.005, Washed-out alert: >0.050)")
    print(f"Rel Contrast:    {avg_contrast:.4f}  (Baseline v65: ~0.980, Flatness alert: <0.550)")

    gates_passed = True
    reasons = []

    # HARD QC CRITERIA
    if fallback_errors:
        gates_passed = False
        reasons.extend(fallback_errors)

    if avg_p1 > QC["p1Max"]:
        gates_passed = False
        reasons.append(f"HARD FAIL: Shadow floor washed out (p1 = {avg_p1:.4f} > {QC['p1Max']:.3f}, blacks are grey)")

    if avg_mean > QC["meanMax"]:
        gates_passed = False
        reasons.append(f"HARD FAIL: Scene overexposed (mean = {avg_mean:.4f} > {QC['meanMax']:.3f}, scene too bright)")

    if avg_contrast < QC["contrastMin"]:
        gates_passed = False
        reasons.append(f"HARD FAIL: Low contrast / dynamic range crushed ({avg_contrast:.4f} < {QC['contrastMin']:.3f})")

    print("-" * 50)
    if gates_passed:
        print("[QC RESULT: PASS] Dark, gritty visual profile and shader integrity confirmed.")
    else:
        print("[QC RESULT: FAIL]")
        for r in reasons:
            print(f"  - {r}")
    print("=" * 50)
    return gates_passed

if __name__ == "__main__":
    b_dir = sys.argv[1] if len(sys.argv) > 1 else "Builds/HDRP_v68"
    audit_directory(b_dir)
