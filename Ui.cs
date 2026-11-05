// Interface : thème sombre dessiné à la main (GDI+), aucune dépendance. Rien n'est dessiné tant qu'aucune fenêtre n'est ouverte.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class Theme
{
    public static readonly Color Bg = C(0x0E1016), Card = C(0x161922), Hover = C(0x232837), Field = C(0x1C202B), Line = C(0x272C39),
        Text = C(0xE9ECF2), Muted = C(0x8A90A2), Off = C(0x2E3442),
        Blue = C(0x4F8CFF), BlueHi = C(0x6E9FFF), Green = C(0x34D399), Violet = C(0xA78BFA), Amber = C(0xFBBF24), Red = C(0xF43F5E);

    // Échelle d'affichage Windows (125 %, 150 %...) : tout est dessiné en "pixels 100 %" puis agrandi.
    public static readonly float S = Dpi();
    public static readonly Font FTitle = Semi(22), FH = Semi(14), FBody = F(13), FBodyB = Semi(13), FSmall = F(12), FSmallB = Semi(12),
        FLabel = Semi(11), FMid = Semi(22), FBig = Semi(26);
    public static readonly StringFormat Mid = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap },
        Right = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };

    static Color C(int rgb) { return Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }
    static Font F(float px) { return new Font("Segoe UI", px, FontStyle.Regular, GraphicsUnit.Pixel); }
    static Font Semi(float px) { return new Font("Segoe UI Semibold", px, FontStyle.Regular, GraphicsUnit.Pixel); }
    static float Dpi() { using (var g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f; }

    public static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath(); float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }
    public static void FillRound(Graphics g, RectangleF r, float rad, Color c) { using (var p = Round(r, rad)) using (var b = new SolidBrush(c)) g.FillPath(b, p); }
    public static void StrokeRound(Graphics g, RectangleF r, float rad, Color c) { using (var p = Round(r, rad)) using (var pen = new Pen(c)) g.DrawPath(pen, p); }
    public static void CardBox(Graphics g, RectangleF r) { FillRound(g, r, 14, Card); StrokeRound(g, r, 14, Line); }
    public static void T(Graphics g, string s, Font f, Color c, float x, float y) { using (var b = new SolidBrush(c)) g.DrawString(s, f, b, x, y); }
    public static void Center(Graphics g, string s, Font f, Color c, RectangleF r) { using (var b = new SolidBrush(c)) g.DrawString(s, f, b, r, Mid); }

    // Petite étiquette arrondie alignée à droite sur "right" ; renvoie son bord gauche.
    public static float Pill(Graphics g, float right, float y, string s, Color c)
    {
        float w = g.MeasureString(s, FLabel).Width + 24, h = 24; var r = new RectangleF(right - w, y, w, h);
        FillRound(g, r, h / 2, Color.FromArgb(40, c));
        using (var b = new SolidBrush(c)) { g.FillEllipse(b, r.X + 10, y + h / 2 - 3, 6, 6); g.DrawString(s, FLabel, b, new RectangleF(r.X + 17, y, w - 17, h), Mid); }
        return r.X;
    }

    public static Color TempColor(float t, int limit) { return t < 0 ? Muted : t >= limit ? Red : t >= limit - 10 ? Amber : Green; }
    public static string Short(string exe, int n)
    {
        if (exe == null) return "";
        if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe = exe.Substring(0, exe.Length - 4);
        return exe.Length > n ? exe.Substring(0, n - 1) + "…" : exe;
    }

    [DllImport("dwmapi")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
    // Barre de titre sombre de la même couleur que la fenêtre, coins arrondis Windows 11 (ignoré sur Windows 10).
    public static void DarkWindow(IntPtr h)
    {
        int on = 1, caption = 0x16100E, border = 0x392C27, corner = 2; // COLORREF = 0xBBGGRR
        DwmSetWindowAttribute(h, 20, ref on, 4); DwmSetWindowAttribute(h, 35, ref caption, 4);
        DwmSetWindowAttribute(h, 34, ref border, 4); DwmSetWindowAttribute(h, 33, ref corner, 4);
    }
    public static void SmallCorners(IntPtr h) { int v = 3; DwmSetWindowAttribute(h, 33, ref v, 4); }
}

// Fenêtre de base : dessin à l'échelle + zones cliquables avec survol (boutons, onglets, interrupteurs).
class DarkForm : Form
{
    protected readonly float W, H;
    readonly List<RectangleF> hotR = new List<RectangleF>();
    readonly List<Action> hotA = new List<Action>();
    int hover = -1;

    public DarkForm(float w, float h, string title)
    {
        W = w; H = h; Text = title; BackColor = Theme.Bg; ForeColor = Theme.Text; DoubleBuffered = true;
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; ShowIcon = false; AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size((int)(w * Theme.S), (int)(h * Theme.S));
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.DarkWindow(Handle); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.ScaleTransform(Theme.S, Theme.S);
        hotR.Clear(); hotA.Clear();
        Draw(g);
    }

    protected virtual void Draw(Graphics g) { }

    // Déclare une zone cliquable pendant le dessin ; renvoie vrai si la souris est dessus.
    protected bool Hot(RectangleF r, Action a) { hotR.Add(r); hotA.Add(a); return hotR.Count - 1 == hover; }

    int Find(Point p)
    {
        var q = new PointF(p.X / Theme.S, p.Y / Theme.S);
        for (int i = hotR.Count - 1; i >= 0; i--) if (hotR[i].Contains(q)) return i;
        return -1;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = Find(e.Location);
        if (i != hover) { hover = i; Cursor = i >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hover >= 0) { hover = -1; Cursor = Cursors.Default; Invalidate(); } }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int i = Find(e.Location);
        if (e.Button != MouseButtons.Left || i < 0) return;
        hotA[i]();
        if (!IsDisposed) Invalidate();
    }

    protected void Button(Graphics g, RectangleF r, string text, bool primary, Action a)
    {
        bool h = Hot(r, a);
        Theme.FillRound(g, r, 10, primary ? (h ? Theme.BlueHi : Theme.Blue) : (h ? Theme.Hover : Theme.Card));
        if (!primary) Theme.StrokeRound(g, r, 10, Theme.Line);
        Theme.Center(g, text, Theme.FBodyB, primary ? Color.White : Theme.Text, r);
    }

    protected Rectangle Px(RectangleF r) { return Rectangle.Round(new RectangleF(r.X * Theme.S, r.Y * Theme.S, r.Width * Theme.S, r.Height * Theme.S)); }
}

// Tableau de bord : jauges CPU / GPU / RAM / FPS, courbes récentes et actions rapides.
sealed class Dashboard : DarkForm
{
    readonly App app;
    bool temps; // onglet "Températures" au lieu de "Charge"

    public Dashboard(App a) : base(680, 520, "MoniteurJeu")
    {
        app = a;
        StartPosition = FormStartPosition.Manual;
        var wa = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
    }

    protected override void Draw(Graphics g)
    {
        bool game = app.GameName != null;
        Theme.T(g, "MoniteurJeu", Theme.FTitle, Theme.Text, 22, 14);
        Theme.T(g, app.Admin ? "Surveillance active · toutes les optimisations sont disponibles" : "Mode limité · relance en administrateur pour le FPS et le nettoyage RAM",
            Theme.FSmall, Theme.Muted, 24, 46);
        float x = Theme.Pill(g, W - 24, 22, game ? "EN JEU · " + Theme.Short(app.GameName, 16).ToUpperInvariant() : "PRÊT", game ? Theme.Red : Theme.Green);
        if (app.Battery) Theme.Pill(g, x - 8, 22, "BATTERIE", Theme.Amber);

        float cw = (W - 48 - 36) / 4f, cy = 80, ch = 172;
        Gauge(g, 24, cy, cw, ch, "CPU", app.Cpu, (int)app.Cpu + "%",
            app.CpuT >= 0 ? (int)app.CpuT + " °C" : "—", Theme.TempColor(app.CpuT, app.Cfg.AlerteCpuC), Theme.Blue, app.CpuThrottle);
        Gauge(g, 24 + (cw + 12), cy, cw, ch, "GPU", app.Gpu, app.Gpu >= 0 ? (int)app.Gpu + "%" : "—",
            app.GpuT >= 0 ? (int)app.GpuT + " °C" : "en veille", Theme.TempColor(app.GpuT, app.Cfg.AlerteGpuC), Theme.Green, app.GpuThrottle);
        Gauge(g, 24 + 2 * (cw + 12), cy, cw, ch, "RAM", app.Ram, (int)app.Ram + "%",
            app.RamUsedGo.ToString("0.0") + " / " + app.RamTotGo.ToString("0") + " Go", Theme.TempColor(app.Ram, app.Cfg.AlerteRam), Theme.Violet, false);
        float f = app.FpsVal;
        Gauge(g, 24 + 3 * (cw + 12), cy, cw, ch, "FPS", f >= 0 ? Math.Min(100, f / 1.44f) : -1, f >= 0 ? ((int)f).ToString() : "—",
            !game ? "aucun jeu" : f >= 0 ? Theme.Short(app.GameName, 14) : app.Admin ? "mesure…" : "admin requis", Theme.Muted, Theme.Amber, false);

        Chart(g, new RectangleF(24, 268, W - 48, 172));

        float bw = (W - 48 - 24) / 3f, by = 458;
        Button(g, new RectangleF(24, by, bw, 42), "Vider la RAM", true, app.CleanRam);
        Button(g, new RectangleF(24 + bw + 12, by, bw, 42), "Historique", false, app.OpenHistory);
        Button(g, new RectangleF(24 + 2 * (bw + 12), by, bw, 42), "Réglages", false, app.ShowSettings);
    }

    // Carte avec anneau de progression (pct de 0 à 100, -1 = vide).
    void Gauge(Graphics g, float x, float y, float w, float h, string label, float pct, string value, string sub, Color subC, Color accent, bool throttled)
    {
        Theme.CardBox(g, new RectangleF(x, y, w, h));
        Theme.T(g, label, Theme.FSmallB, Theme.Muted, x + 14, y + 12);
        if (throttled) Theme.Pill(g, x + w - 10, y + 10, "BRIDÉ", Theme.Red);
        float cx = x + w / 2, cy = y + 86, r = 40;
        var ring = new RectangleF(cx - r, cy - r, 2 * r, 2 * r);
        using (var p = new Pen(Color.FromArgb(38, accent), 9)) g.DrawEllipse(p, ring);
        if (pct > 0.5f)
            using (var p = new Pen(accent, 9) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(p, ring, -90, 360f * Math.Min(pct, 100) / 100f);
        Theme.Center(g, value, value.Length > 4 ? Theme.FMid : Theme.FBig, Theme.Text, ring);
        Theme.Center(g, sub, Theme.FSmall, subC, new RectangleF(x + 6, y + h - 32, w - 12, 20));
    }

    void Chart(Graphics g, RectangleF c)
    {
        Theme.CardBox(g, c);
        Theme.T(g, "Activité récente", Theme.FH, Theme.Text, c.X + 16, c.Y + 14);
        float lx = c.X + 26 + g.MeasureString("Activité récente", Theme.FH).Width;
        lx = Key(g, lx, c.Y + 17, "CPU", Theme.Blue); Key(g, lx, c.Y + 17, "GPU", Theme.Green);

        var seg = new RectangleF(c.Right - 16 - 200, c.Y + 12, 200, 30);
        Theme.FillRound(g, seg, 9, Theme.Field);
        string[] tabs = { "Charge", "Températures" };
        for (int k = 0; k < 2; k++)
        {
            int kk = k;
            var t = new RectangleF(seg.X + 3 + k * 97, seg.Y + 3, 97, 24);
            bool h = Hot(t, delegate { temps = kk == 1; });
            bool sel = (k == 1) == temps;
            if (sel || h) Theme.FillRound(g, t, 7, sel ? Theme.Hover : Color.FromArgb(120, Theme.Hover));
            Theme.Center(g, tabs[k], Theme.FSmallB, sel ? Theme.Text : Theme.Muted, t);
        }

        var p = new RectangleF(c.X + 50, c.Y + 54, c.Width - 66, c.Height - 80);
        string unit = temps ? "°" : "%";
        using (var pen = new Pen(Theme.Line) { DashStyle = DashStyle.Dash })
            for (int v = 0; v <= 100; v += 25)
            {
                float y = p.Bottom - p.Height * v / 100f;
                g.DrawLine(pen, p.Left, y, p.Right, y);
                using (var b = new SolidBrush(Theme.Muted)) g.DrawString(v + unit, Theme.FSmall, b, new RectangleF(c.X + 4, y - 9, 40, 18), Theme.Right);
            }
        if (temps) { Series(g, p, app.HCpuT, Theme.Blue); Series(g, p, app.HGpuT, Theme.Green); }
        else { Series(g, p, app.HCpu, Theme.Blue); Series(g, p, app.HGpu, Theme.Green); }
        using (var b = new SolidBrush(Theme.Muted)) g.DrawString("maintenant", Theme.FSmall, b, new RectangleF(p.Right - 120, p.Bottom + 2, 120, 20), Theme.Right);
    }

    static float Key(Graphics g, float x, float y, string s, Color c)
    {
        using (var b = new SolidBrush(c)) g.FillEllipse(b, x, y + 5, 8, 8);
        Theme.T(g, s, Theme.FSmall, Theme.Muted, x + 11, y);
        return x + 22 + g.MeasureString(s, Theme.FSmall).Width;
    }

    // Courbe lissée avec remplissage en dégradé ; les trous (-1) coupent la courbe.
    void Series(Graphics g, RectangleF p, float[] h, Color c)
    {
        int n = h.Length; var pts = new List<PointF>();
        for (int i = 0; i <= n; i++)
        {
            float v = i < n ? h[(app.Head + i) % n] : -1;
            if (v >= 0) { pts.Add(new PointF(p.Left + p.Width * i / (n - 1), p.Bottom - p.Height * Math.Min(v, 100) / 100f)); continue; }
            if (pts.Count > 1)
            {
                var poly = new List<PointF>(pts);
                poly.Add(new PointF(pts[pts.Count - 1].X, p.Bottom)); poly.Add(new PointF(pts[0].X, p.Bottom));
                using (var b = new LinearGradientBrush(new RectangleF(p.X, p.Y - 1, p.Width, p.Height + 2), Color.FromArgb(70, c), Color.FromArgb(0, c), 90f))
                    g.FillPolygon(b, poly.ToArray());
                using (var pen = new Pen(c, 2f) { LineJoin = LineJoin.Round }) g.DrawLines(pen, pts.ToArray());
            }
            pts.Clear();
        }
    }
}

// Réglages : seuils avec boutons -/+, champs texte, interrupteurs.
sealed class SettingsForm : DarkForm
{
    readonly App app; readonly Reglages r; bool auto;
    readonly TextBox[] boxes = new TextBox[3];
    readonly Font boxFont = new Font("Segoe UI", 13 * Theme.S, GraphicsUnit.Pixel);
    static readonly string[] BLabel = { "Jeux surveillés", "Applis mises en pause", "Services arrêtés" },
        BHint = { "noms de programmes, séparés par ;", "gelées pendant le jeu (évite Discord en vocal)", "admin requis" },
        SLabel = { "Vider la RAM en veille au lancement d'un jeu", "Forcer la carte NVIDIA pour les jeux", "Overlay en jeu (FPS, CPU, GPU)", "Démarrer avec Windows" };
    const float BY = 222, BH = 60, SY = 426, SH = 44;

    public SettingsForm(App a) : base(560, 676, "Réglages")
    {
        app = a; r = Reglages.Load(app.CfgFile); auto = app.AutoStartOn;
        StartPosition = FormStartPosition.CenterScreen; KeyPreview = true;
        string[] vals = { r.Jeux, r.AppsEnPause, r.Services };
        for (int i = 0; i < 3; i++)
        {
            var b = new TextBox { BorderStyle = BorderStyle.None, BackColor = Theme.Field, ForeColor = Theme.Text, Font = boxFont, Text = vals[i] };
            b.Bounds = Px(new RectangleF(36, BY + i * BH + 22 + 8, W - 72, 20));
            b.GotFocus += delegate { Invalidate(); }; b.LostFocus += delegate { Invalidate(); };
            Controls.Add(b); boxes[i] = b;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.Escape) Close(); }
    protected override void OnShown(EventArgs e) { base.OnShown(e); ActiveControl = null; }

    protected override void Draw(Graphics g)
    {
        Theme.T(g, "Réglages", Theme.FTitle, Theme.Text, 22, 14);
        Theme.T(g, "Enregistre pour appliquer · Échap pour fermer", Theme.FSmall, Theme.Muted, 24, 46);

        Theme.T(g, "ALERTES", Theme.FLabel, Theme.Muted, 24, 84);
        float sw = (W - 48 - 24) / 3f;
        Stepper(g, 24, 104, sw, "CPU max", r.AlerteCpuC, "°C", 60, 105, v => r.AlerteCpuC = v);
        Stepper(g, 24 + sw + 12, 104, sw, "GPU max", r.AlerteGpuC, "°C", 60, 100, v => r.AlerteGpuC = v);
        Stepper(g, 24 + 2 * (sw + 12), 104, sw, "RAM max", r.AlerteRam, "%", 50, 99, v => r.AlerteRam = v);

        Theme.T(g, "MODE JEU", Theme.FLabel, Theme.Muted, 24, 200);
        for (int i = 0; i < 3; i++)
        {
            float y = BY + i * BH;
            Theme.T(g, BLabel[i], Theme.FBodyB, Theme.Text, 24, y);
            Theme.T(g, BHint[i], Theme.FSmall, Theme.Muted, 30 + g.MeasureString(BLabel[i], Theme.FBodyB).Width, y + 1);
            var f = new RectangleF(24, y + 22, W - 48, 34);
            Theme.FillRound(g, f, 8, Theme.Field);
            Theme.StrokeRound(g, f, 8, boxes[i] != null && boxes[i].Focused ? Theme.Blue : Theme.Line);
        }

        Theme.T(g, "OPTIONS", Theme.FLabel, Theme.Muted, 24, SY - 20);
        var card = new RectangleF(24, SY, W - 48, SH * 4);
        Theme.CardBox(g, card);
        bool[] on = { r.ViderRam, r.ForcerNvidia, r.Overlay, auto };
        for (int i = 0; i < 4; i++)
        {
            int k = i;
            var row = new RectangleF(card.X, SY + i * SH, card.Width, SH);
            if (Hot(row, delegate { Flip(k); })) Theme.FillRound(g, RectangleF.Inflate(row, -4, -3), 9, Theme.Hover);
            if (i > 0) using (var pen = new Pen(Theme.Line)) g.DrawLine(pen, row.X + 16, row.Y, row.Right - 16, row.Y);
            Theme.T(g, SLabel[i], Theme.FBody, Theme.Text, row.X + 16, row.Y + 12);
            Switch(g, row.Right - 16 - 40, row.Y + 11, on[i]);
        }

        float fy = SY + SH * 4 + 18;
        Button(g, new RectangleF(W - 24 - 140, fy, 140, 40), "Enregistrer", true, Save);
        Button(g, new RectangleF(W - 24 - 140 - 12 - 110, fy, 110, 40), "Annuler", false, Close);
    }

    void Flip(int k)
    {
        if (k == 0) r.ViderRam = !r.ViderRam;
        else if (k == 1) r.ForcerNvidia = !r.ForcerNvidia;
        else if (k == 2) r.Overlay = !r.Overlay;
        else auto = !auto;
    }

    void Save()
    {
        r.Jeux = boxes[0].Text.Trim(); r.AppsEnPause = boxes[1].Text.Trim(); r.Services = boxes[2].Text.Trim();
        app.ApplyCfg(r);
        if (auto != app.AutoStartOn) app.ToggleAutoStart();
        Close();
    }

    void Stepper(Graphics g, float x, float y, float w, string label, int val, string unit, int min, int max, Action<int> set)
    {
        Theme.CardBox(g, new RectangleF(x, y, w, 76));
        Theme.T(g, label, Theme.FSmall, Theme.Muted, x + 14, y + 12);
        Theme.T(g, val + " " + unit, Theme.FMid, Theme.Text, x + 12, y + 32);
        var minus = new RectangleF(x + w - 72, y + 38, 28, 28); var plus = new RectangleF(x + w - 40, y + 38, 28, 28);
        bool hm = Hot(minus, delegate { set(Math.Max(min, val - 1)); }), hp = Hot(plus, delegate { set(Math.Min(max, val + 1)); });
        Theme.FillRound(g, minus, 14, hm ? Theme.Hover : Theme.Field); Theme.Center(g, "−", Theme.FBodyB, Theme.Text, minus);
        Theme.FillRound(g, plus, 14, hp ? Theme.Hover : Theme.Field); Theme.Center(g, "+", Theme.FBodyB, Theme.Text, plus);
    }

    static void Switch(Graphics g, float x, float y, bool on)
    {
        Theme.FillRound(g, new RectangleF(x, y, 40, 22), 11, on ? Theme.Blue : Theme.Off);
        using (var b = new SolidBrush(on ? Color.White : Color.FromArgb(200, 204, 214))) g.FillEllipse(b, on ? x + 21 : x + 3, y + 3, 16, 16);
    }

    protected override void Dispose(bool disposing) { if (disposing) boxFont.Dispose(); base.Dispose(disposing); }
}

// Petit cadre en jeu : transparent aux clics, ne prend jamais le focus, redessiné 1x/s.
sealed class Overlay : Form
{
    readonly App app;

    public Overlay(App a)
    {
        app = a;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Bg; Opacity = 0.9; DoubleBuffered = true;
        float s = Theme.S;
        ClientSize = new Size((int)(196 * s), (int)(60 * s));
        StartPosition = FormStartPosition.Manual;
        Location = new Point(Screen.PrimaryScreen.Bounds.Left + (int)(12 * s), Screen.PrimaryScreen.Bounds.Top + (int)(12 * s));
        using (var p = Theme.Round(new RectangleF(0, 0, ClientSize.Width, ClientSize.Height), 12 * s)) Region = new Region(p);
    }

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x08000000; return cp; } // LAYERED | TRANSPARENT | TOOLWINDOW | NOACTIVATE
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.ScaleTransform(Theme.S, Theme.S);
        float f = app.FpsVal;
        Color fc = f < 0 ? Theme.Muted : f >= 60 ? Theme.Green : f >= 30 ? Theme.Amber : Theme.Red;
        Theme.Center(g, f >= 0 ? ((int)f).ToString() : "—", Theme.FBig, fc, new RectangleF(0, 4, 70, 34));
        Theme.Center(g, "FPS", Theme.FLabel, Theme.Muted, new RectangleF(0, 36, 70, 16));
        using (var pen = new Pen(Theme.Line)) g.DrawLine(pen, 70, 12, 70, 48);
        Row(g, 82, 10, "CPU", app.Cpu, app.CpuT, app.CpuThrottle, Theme.Blue);
        Row(g, 82, 32, "GPU", app.Gpu, app.GpuT, app.GpuThrottle, Theme.Green);
    }

    static void Row(Graphics g, float x, float y, string label, float v, float t, bool throttled, Color c)
    {
        using (var b = new SolidBrush(c)) g.FillEllipse(b, x, y + 6, 7, 7);
        Theme.T(g, label, Theme.FLabel, Theme.Muted, x + 11, y + 2);
        Theme.T(g, (v >= 0 ? (int)v + "%" : "—") + (t >= 0 ? "   " + (int)t + "°" : ""), Theme.FBodyB, throttled ? Theme.Red : Theme.Text, x + 42, y);
    }
}

// Menu de l'icône : sombre, arrondi, sélection en pastille.
sealed class DarkMenu : ToolStripProfessionalRenderer
{
    public DarkMenu() : base(new Colors()) { RoundedEdges = false; }

    sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.Card; } }
        public override Color MenuBorder { get { return Theme.Line; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Card; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Card; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Card; } }
        public override Color SeparatorDark { get { return Theme.Line; } }
        public override Color SeparatorLight { get { return Theme.Card; } }
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.FillRound(e.Graphics, new RectangleF(4, 1, e.Item.Width - 8, e.Item.Height - 2), 6, Theme.Hover);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Muted;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var r = e.ImageRectangle; e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var p = new Pen(Theme.Blue, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            e.Graphics.DrawLines(p, new[] { new PointF(r.X + r.Width * .2f, r.Y + r.Height * .52f), new PointF(r.X + r.Width * .42f, r.Y + r.Height * .74f), new PointF(r.X + r.Width * .8f, r.Y + r.Height * .3f) });
    }
}
