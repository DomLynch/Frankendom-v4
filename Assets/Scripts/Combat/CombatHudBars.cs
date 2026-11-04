using UnityEngine;

// Minimal combat HUD under the HP bars: a thin stamina bar, a clear POSTURE bar under it (P6, posture.json "hud"), a guard line that shows only while the guard is up
// (gold during the parry window), and the cue texts: GUARD BROKEN / PARRY (P5, stamina.json "hud") and POSTURE BROKEN / DEATHBLOW (P6). Cue texts sit right-aligned on the name row above the HP bar
// so they never cover the bars. Called from GameHUD.OnGUI.
public static class CombatHudBars
{
    static GUIStyle label;
    static void Fill(Rect r, Color c) { GUI.color = c; GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = Color.white; }

    // hp = the HP bar rect of this fighter. Draws below it.
    public static void Draw(Rect hp, DuelFighter f, bool isPlayer)
    {
        var H = StaminaTable.Get().hud;
        if (f == null || !H.enabled || (!isPlayer && !H.showForEnemy)) return;
        float y = hp.yMax + H.gap;
        float frac = Mathf.Clamp01(f.stamina / Mathf.Max(1f, f.maxStamina));
        var bar = new Rect(hp.x, y, hp.width, H.barHeight);
        float bp = H.borderPx;
        Fill(bar, new Color(0f, 0f, 0f, 0.65f));
        Color c = frac < H.lowFraction ? StaminaTable.Col(H.lowColor) : StaminaTable.Col(H.staminaColor);
        if (f.Exhausted) { float p = 0.55f + 0.45f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * Mathf.PI * H.exhaustedPulseHz)); c = StaminaTable.Col(H.exhaustedColor) * p; c.a = 1f; }
        Fill(new Rect(bar.x + bp, bar.y + bp, (bar.width - 2f * bp) * frac, bar.height - 2f * bp), c);

        // posture bar (P6): under stamina, always shown on both fighters (posture.json hud.alwaysShow), fill = posture / this fighter's max
        float after = bar.yMax + 2f;
        var PD = PostureTable.Get(); var PH = PD.hud;
        string cue = null; Color cueCol = Color.white;
        if (PD.enabled && PH.enabled)
        {
            float pmax = Mathf.Max(1f, f.PostureMax), pf = Mathf.Clamp01(f.posture / pmax);
            if (PH.alwaysShow || pf > 0.01f || f.PostureBroken)
            {
                var pr = new Rect(hp.x, bar.yMax + PH.gap, hp.width, PH.height);
                Fill(pr, PostureTable.Col(PH.bgColor));
                var inner = new Rect(pr.x + bp, pr.y + bp, pr.width - 2f * bp, pr.height - 2f * bp);
                if (f.DeathblowOpen)
                {
                    // the window is open: the bar becomes a countdown (full -> empty), pulsing between the break colour and the deathblow colour
                    float k = f.DeathblowLeft / (float)Mathf.Max(1, f.DeathblowTotal);
                    float pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * Mathf.PI * 2f * PH.pulseHz);
                    Color cc = Color.Lerp(PostureTable.Col(PH.breakColor, 1f), PostureTable.Col(PH.deathblowColor, 1f), pulse);
                    Fill(new Rect(inner.x, inner.y, inner.width * k, inner.height), cc);
                    cue = PH.deathblowText; cueCol = cc;
                }
                else
                {
                    Color pc = pf >= PH.highFrom ? Color.Lerp(PostureTable.Col(PH.color, 1f), PostureTable.Col(PH.highColor, 1f), Mathf.InverseLerp(PH.highFrom, 1f, pf)) : PostureTable.Col(PH.color, 1f);
                    Fill(new Rect(inner.x, inner.y, inner.width * pf, inner.height), pc);
                }
                if (f.PostureBreakFlash > 0)
                {
                    float a = f.PostureBreakFlash / (float)Mathf.Max(1, PD.posBreak.flashTicks);
                    Fill(new Rect(pr.x - 2f, pr.y - 2f, pr.width + 4f, pr.height + 4f), PostureTable.Col(PH.breakColor, 0.85f * a));
                    Fill(new Rect(inner.x, inner.y, inner.width, inner.height), PostureTable.Col(PH.deathblowColor, 0.9f * a));
                    if (cue == null) { cue = PH.breakText; cueCol = PostureTable.Col(PH.breakColor, 1f); }
                }
                after = pr.yMax + 2f;
            }
        }
        // guard / parry indicator: one thin line, only while the guard is up
        int win = f.ParryWindowLeft;
        if (f.state == FState.Guard)
        {
            var gl = new Rect(hp.x, after, hp.width, 2f);
            if (win > 0) { float k = win / (float)Mathf.Max(1, f.ParryWindowTicks()); Fill(gl, new Color(0f, 0f, 0f, 0.5f)); Fill(new Rect(gl.x, gl.y, gl.width * k, gl.height), StaminaTable.Col(H.parryColor)); }
            else Fill(gl, StaminaTable.Col(H.guardColor, f.IsGuarding ? 0.8f : 0.35f));
        }
        // flashes (P5); the text goes to the name row so it does not cover the bars
        var nameRow = new Rect(hp.x, hp.y - 24f, hp.width, 22f);
        if (f.GuardBreakFlash > 0)
        {
            float a = f.GuardBreakFlash / (float)Mathf.Max(1, StaminaTable.Get().guardBreak.flashTicks);
            Fill(new Rect(bar.x - 1f, bar.y - 1f, bar.width + 2f, bar.height + 2f), StaminaTable.Col(H.breakColor, 0.85f * a));
            if (cue == null) { cue = H.breakText; cueCol = StaminaTable.Col(H.breakColor, a); }
        }
        else if (f.ParryFlash > 0)
        {
            float a = f.ParryFlash / (float)Mathf.Max(1, H.parryFlashTicks);
            Fill(new Rect(bar.x - 1f, bar.y - 1f, bar.width + 2f, bar.height + 2f), StaminaTable.Col(H.parryColor, 0.7f * a));
            if (cue == null) { cue = H.parryText; cueCol = StaminaTable.Col(H.parryColor, a); }
        }
        if (cue != null) Text(nameRow, cue, cueCol, f.DeathblowOpen ? Mathf.Max(PH.labelFontSize, H.labelFontSize) : H.labelFontSize);
    }

    static void Text(Rect r, string t, Color c, int size)
    {
        if (label == null) label = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperRight, fontStyle = FontStyle.Bold };
        label.alignment = TextAnchor.UpperRight; label.fontSize = size; label.normal.textColor = new Color(0f, 0f, 0f, c.a);
        GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), t, label);
        label.normal.textColor = c; GUI.Label(r, t, label);
    }
}
