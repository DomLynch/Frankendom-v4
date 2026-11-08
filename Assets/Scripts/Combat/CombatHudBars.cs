using UnityEngine;

// HUD2 (lane laneHUD2): thin tapered bars with ornate frames, drawn procedurally (HudDraw), plus the stamina / posture / cue logic that used to live here (P5 stamina.json "hud", P6 posture.json "hud").
// Sizes, colours and thresholds now come from hud.json (player{} / boss{} / bars{} / colors{}); stamina.json hud and posture.json hud keep only their enable switches and the cue texts
// (GUARD BROKEN, PARRY, POSTURE BROKEN, DEATHBLOW). Called from GameHUD.OnGUI inside its GUI.matrix scale (all numbers are HUD px at the 1080 reference).
public static class CombatHudBars
{
    /// <summary>The pale damage trail of a bar. HUDFIX v92: holds bars.lagHold s after the last drop (a new hit re-arms the hold), then closes the gap by an ease (bars.lagEase per s, floor bars.lagMinRate per s) and is forced shut bars.lagMaxS s after the hold ends. A heal or the first call snaps it to the bar.</summary>
    public class Lag
    {
        public float v = 1f; float holdUntil = -1f, last = -1f; bool init;   // HUDFIX v92
        public void Step(float f, float now, float dt)
        {
            var B = HudCfg.Get().bars;
            if (!init || f >= v) { init = true; v = f; last = f; holdUntil = -1f; return; }
            /* UIDATA_A v1 */ if (f < last - HudLayout.Get().barFx.lagHitEps) holdUntil = now + B.lagHold;   // a new hit: wait again, the trail keeps its old top
            last = f;
            if (now < holdUntil) return;
            float gap = v - f, step = Mathf.Max(gap * (1f - Mathf.Exp(-B.lagEase * dt)), B.lagMinRate * dt);
            if (B.lagMaxS > 0f && now - holdUntil >= B.lagMaxS) step = gap;
            v = gap <= HudLayout.Get().barFx.lagCloseEps || step >= gap ? f : v - step;
        }
    }

    /// <summary>One tapered bar: dark backing, optional damage trail, coloured fill with a faint gloss, optional tick marks, optional ornate frame (outline + end diamonds + thin lead-in lines). r = the bar rect.</summary>
    public static void Bar(Rect r, float taper, float frac, float lag, Color fill, bool frame, bool ticks)
    {
        var H = HudCfg.Get(); var B = H.bars; var C = H.colors; frac = Mathf.Clamp01(frac);
        float bp = B.backPad;
        var backR = new Rect(r.x - bp, r.y - bp, r.width + 2f * bp, r.height + 2f * bp);
        HudDraw.Tex(backR, HudDraw.BarShape(backR.width, backR.height, taper + bp, 0f), HudCfg.Col(C.hpBack));
        var shape = HudDraw.BarShape(r.width, r.height, taper, 0f);
        if (lag > frac + HudLayout.Get().barFx.lagShowEps) HudDraw.TexUV(new Rect(r.x, r.y, r.width * lag, r.height), shape, new Rect(0f, 0f, Mathf.Clamp01(lag), 1f), HudCfg.Col(C.hpLag));
        if (frac > HudLayout.Get().barFx.fillMinFrac)
        {
            HudDraw.TexUV(new Rect(r.x, r.y, r.width * frac, r.height), shape, new Rect(0f, 0f, frac, 1f), fill);
            if (B.gloss > 0f) HudDraw.TexUV(new Rect(r.x, r.y, r.width * frac, r.height * 0.5f), shape, new Rect(0f, 0.5f, frac, 0.5f), new Color(1f, 1f, 1f, B.gloss));   // texture y 0 = bottom, so 0.5..1 is the top half
        }
        if (ticks && B.ticks != null)
            for (int i = 0; i < B.ticks.Length; i++)
            {
                float tx = r.x + r.width * B.ticks[i], th = r.height * B.tickHeight;
                HudDraw.Fill(new Rect(tx - 0.5f, r.y + (r.height - th) * 0.5f, 1f, th), new Color(0f, 0f, 0f, B.tickAlpha));
            }
        if (frame) Frame(r, taper);
    }

    /// <summary>Ornate frame around a bar rect: tapered outline, small diamonds on the tips, thin fading lead-in lines beyond them.</summary>
    public static void Frame(Rect r, float taper)
    {
        var H = HudCfg.Get(); var B = H.bars; var C = H.colors; float p = B.framePad;
        var outer = new Rect(r.x - p, r.y - p, r.width + 2f * p, r.height + 2f * p);
        Color fc = HudCfg.Col(C.frame), dim = HudCfg.Col(C.frameDim);
        HudDraw.Tex(outer, HudDraw.BarShape(outer.width, outer.height, taper + p, B.frameLine * B.texScale), fc);
        float cs = B.capSize, cy = r.center.y, lead = B.capLead, lh = Mathf.Max(HudLayout.Get().barFx.leadThickMin, B.frameLine * HudLayout.Get().barFx.leadThickMul);
        float lx = outer.x - cs * HudLayout.Get().barFx.capOverhang, rx = outer.xMax + cs * HudLayout.Get().barFx.capOverhang;                       // tips of the outline
        HudDraw.Tex(new Rect(lx - cs, cy - cs, cs * 2f, cs * 2f), HudDraw.Diamond(), HudLayout.Col(HudLayout.Get().barFx.capBack));
        HudDraw.Tex(new Rect(lx - cs, cy - cs, cs * 2f, cs * 2f), HudDraw.DiamondRing(), fc);
        HudDraw.Tex(new Rect(rx - cs, cy - cs, cs * 2f, cs * 2f), HudDraw.Diamond(), HudLayout.Col(HudLayout.Get().barFx.capBack));
        HudDraw.Tex(new Rect(rx - cs, cy - cs, cs * 2f, cs * 2f), HudDraw.DiamondRing(), fc);
        HudDraw.TexUV(new Rect(lx - cs - lead, cy - lh * 0.5f, lead, lh), HudDraw.GradH(), new Rect(1f, 0f, -1f, 1f), dim);      // opaque at the diamond, fades outwards
        HudDraw.TexUV(new Rect(rx + cs, cy - lh * 0.5f, lead, lh), HudDraw.GradH(), new Rect(0f, 0f, 1f, 1f), dim);
    }

    /// <summary>Stamina bar then posture bar under it (either may be null / hidden), anchored at x (pivot 0 = left edge, 0.5 = centre), first bar top at y.
    /// Returns the y under the last bar. cue / cueCol = the one text cue to show for this fighter this frame (null = none); cueBig = a deathblow window is open.</summary>
    public static float Draw(float x, float y, float pivot, DuelFighter f, bool isPlayer, HudCfg.BarC stam, HudCfg.BarC post, bool showStam, bool showPost, out string cue, out Color cueCol, out bool cueBig)
    {
        cue = null; cueCol = Color.white; cueBig = false;
        var H = HudCfg.Get(); var B = H.bars; var C = H.colors; var SH = StaminaTable.Get().hud; var PD = PostureTable.Get(); var PH = PD.hud;
        if (f == null) return y;
        float yy = y; bool dbOpen = f.DeathblowOpen && f.DeathblowLeft > 0 && f.PostureBroken;   // V61A: only while THIS fighter's window is really open
        Rect stamRect = default(Rect); bool haveStam = false;

        if (showStam && stam != null && SH.enabled && (isPlayer || SH.showForEnemy))
        {
            float frac = Mathf.Clamp01(f.stamina / Mathf.Max(1f, f.maxStamina));
            stamRect = new Rect(x - stam.w * pivot, yy, stam.w, stam.h); haveStam = true;
            Color c = frac < B.lowFraction ? HudCfg.Col(C.staminaLow) : HudCfg.Col(C.stamina);
            if (f.Exhausted) { float p = HudLayout.Get().barFx.exhaustMin + HudLayout.Get().barFx.exhaustAmp * Mathf.Abs(Mathf.Sin(Time.unscaledTime * Mathf.PI * B.exhaustedPulseHz)); c = HudCfg.Col(C.staminaExhausted) * p; c.a = 1f; }
            Bar(stamRect, stam.taper, frac, -1f, c, false, false);
            yy = stamRect.yMax + stam.gap;
        }
        if (showPost && post != null && PD.enabled && PH.enabled)
        {
            float pmax = Mathf.Max(1f, f.PostureMax), pf = Mathf.Clamp01(f.posture / pmax);
            if (PH.alwaysShow || pf > HudLayout.Get().barFx.postureShowMin || f.PostureBroken)
            {
                var pr = new Rect(x - post.w * pivot, yy, post.w, post.h);
                Color pc; float fillFrac = pf;
                if (dbOpen)
                {
                    // the window is open: the bar becomes a countdown (full -> empty), pulsing between the break colour and the deathblow colour
                    fillFrac = f.DeathblowLeft / (float)Mathf.Max(1, f.DeathblowTotal);
                    float pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * Mathf.PI * 2f * B.pulseHz);
                    pc = Color.Lerp(HudCfg.Col(C.postureBreak), HudCfg.Col(C.deathblow), pulse);
                    cue = PH.deathblowText; cueCol = pc; cueBig = true;
                }
                else pc = pf >= B.highFrom ? Color.Lerp(HudCfg.Col(C.posture), HudCfg.Col(C.postureHigh), Mathf.InverseLerp(B.highFrom, 1f, pf)) : HudCfg.Col(C.posture);
                Bar(pr, post.taper, fillFrac, -1f, pc, false, false);
                if (f.PostureBreakFlash > 0 && f.PostureBroken)   // V61A: the break flash belongs to an active break only
                {
                    float a = f.PostureBreakFlash / (float)Mathf.Max(1, PD.posBreak.flashTicks);
                    var fr = new Rect(pr.x - HudLayout.Get().barFx.flashPad, pr.y - HudLayout.Get().barFx.flashPad, pr.width + HudLayout.Get().barFx.flashGrow, pr.height + HudLayout.Get().barFx.flashGrow);
                    HudDraw.Tex(fr, HudDraw.BarShape(fr.width, fr.height, post.taper + HudLayout.Get().barFx.flashPad, 0f), HudCfg.Col(C.postureBreak, B.flashAlpha * a));
                    HudDraw.TexUV(pr, HudDraw.BarShape(pr.width, pr.height, post.taper, 0f), new Rect(0f, 0f, 1f, 1f), HudCfg.Col(C.deathblow, HudLayout.Get().barFx.deathFlashAlpha * a));
                    if (cue == null) { cue = PH.breakText; cueCol = HudCfg.Col(C.postureBreak); }
                }
                yy = pr.yMax + post.gap;
            }
        }
        // P5 flashes: guard broken / parry (the text goes to the caller's name row so it never covers the bars)
        if (f.GuardBreakFlash > 0)
        {
            float a = f.GuardBreakFlash / (float)Mathf.Max(1, StaminaTable.Get().guardBreak.flashTicks);
            if (haveStam) { var fr = new Rect(stamRect.x - HudLayout.Get().barFx.flashPad, stamRect.y - HudLayout.Get().barFx.flashPad, stamRect.width + HudLayout.Get().barFx.flashGrow, stamRect.height + HudLayout.Get().barFx.flashGrow); HudDraw.Tex(fr, HudDraw.BarShape(fr.width, fr.height, stam.taper + HudLayout.Get().barFx.flashPad, 0f), HudCfg.Col(C.postureBreak, B.flashAlpha * a)); }
            if (cue == null) { cue = SH.breakText; cueCol = HudCfg.Col(C.postureBreak, a); }
        }
        else if (f.ParryFlash > 0)
        {
            float a = f.ParryFlash / (float)Mathf.Max(1, SH.parryFlashTicks);
            if (haveStam) { var fr = new Rect(stamRect.x - HudLayout.Get().barFx.flashPad, stamRect.y - HudLayout.Get().barFx.flashPad, stamRect.width + HudLayout.Get().barFx.flashGrow, stamRect.height + HudLayout.Get().barFx.flashGrow); HudDraw.Tex(fr, HudDraw.BarShape(fr.width, fr.height, stam.taper + HudLayout.Get().barFx.flashPad, 0f), HudCfg.Col(C.parry, HudLayout.Get().barFx.parryFlashAlpha * a)); }
            if (cue == null) { cue = SH.parryText; cueCol = HudCfg.Col(C.parry, a); }
        }
        return yy;
    }
}
