using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LiquidMusic;

internal enum IslandMode { Hidden, Compact, Expanded }

/// <summary>Остров у края экрана: окно без активации с WebView2 (ui/island.html).
/// ОКНО И WEBVIEW НЕ ДВИГАЮТСЯ И НЕ МЕНЯЮТ РАЗМЕР: они один раз ставятся у края экрана (сцена 340×120 + небольшой запас к краю).
/// Анимируется только ФОРМА окна — регион (SetWindowRgn) со скруглением. Пружина считает форму ~120 раз в секунду,
/// а страница получает ту же форму ("S:x,y,w,h,r") и рисует стекло ровно по ней. Поэтому содержимое (обложка, текст)
/// стоит на месте, ничего не «съезжает» и не мигает от пересоздания окна/WebView2.</summary>
internal sealed class IslandWindow : Form
{
    private const int StageW = 340, StageH = 120, PillW = 126, PillH = 34, PillR = 17, CardR = 42, EdgeMargin = 28;
    private const double PageLeadMs = 28;   // страница показывает форму позже окна (IPC + кадр + композитор): шлём форму «с упреждением»
    private const uint SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;

    /// <summary>Пружина: полу-неявный Эйлер, подшаги по 4 мс. Скорость не зависит от частоты таймера.</summary>
    private struct Spring
    {
        public double X, V, T;
        public void Step(double dt, double stiffness, double damping)
        {
            var acceleration = stiffness * (T - X) - damping * V;
            V += acceleration * dt; X += V * dt;
        }
        public readonly bool Settled => Math.Abs(T - X) < 0.35 && Math.Abs(V) < 5;
        public void Snap() { X = T; V = 0; }
        public void Jump(double value) { X = value; T = value; V = 0; }
    }
    // (жёсткость, затухание): «живая» с небольшим перелётом, средняя и почти критическая — чтобы не отскакивать от края
    private static readonly (double K, double C) Lively = (380, 26);
    private static readonly (double K, double C) Medium = (380, 31);
    private static readonly (double K, double C) Calm = (340, 37);

    private readonly WebView2 view = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };
    private readonly System.Windows.Forms.Timer frameTimer = new() { Interval = 8 };
    private readonly System.Windows.Forms.Timer backdropTimer = new() { Interval = 650 };
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private Spring sx, sy, sw, sh;                         // форма маски в координатах ОКНА, физические px
    private double stiffness = Lively.K, damping = Lively.C;
    private Rectangle windowRect, stageScreen, pill, hiddenShape, stageInWindow;
    private string side = "";
    private bool ready, captureOn, fineTimer;
    private long lastTick, lastPost;
    private string lastState = "";
    private float DpiScale => DeviceDpi / 96f;
    public IslandMode Mode { get; private set; } = IslandMode.Hidden;
    /// <summary>Куда придёт форма острова в координатах экрана (а не где она сейчас): по ней MainForm решает, раскрывать или прятать остров.</summary>
    public Rectangle TargetBounds => new(windowRect.X + (int)Math.Round(sx.T), windowRect.Y + (int)Math.Round(sy.T), (int)Math.Round(sw.T), (int)Math.Round(sh.T));
    public event Action<string>? Command;
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x80; return p; } }   // NOACTIVATE | TOOLWINDOW

    public IslandWindow()
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; BackColor = Color.Black;
        StartPosition = FormStartPosition.Manual; Location = new(-32000, -32000); Size = new(200, 80);
        Controls.Add(view);
        frameTimer.Tick += (_, _) => OnFrame();
        backdropTimer.Tick += (_, _) => PushBackdrop();
    }

    public async Task StartAsync(CoreWebView2Environment environment)
    {
        _ = Handle; Show();                                // WebView2 нужен созданный и «видимый» хост; окно за экраном и без активации
        await view.EnsureCoreWebView2Async(environment);
        var core = view.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping("liquid.local", Path.Combine(AppContext.BaseDirectory, "ui"), CoreWebView2HostResourceAccessKind.DenyCors);
        core.Settings.AreDefaultContextMenusEnabled = false; core.Settings.AreDevToolsEnabled = false;
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith("https://liquid.local/", StringComparison.OrdinalIgnoreCase)) e.Cancel = true; };
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.WebMessageReceived += (_, e) =>
        {
            try
            {
                var command = JsonNode.Parse(e.WebMessageAsJson)?["command"]?.GetValue<string>();
                if (command is "previous" or "stop" or "play_pause" or "next") Command?.Invoke(command);
            }
            catch (Exception error) { AppPaths.Log(error); }
        };
        var loaded = new TaskCompletionSource();
        core.NavigationCompleted += (_, _) => loaded.TrySetResult();
        core.Navigate("https://liquid.local/island.html");
        await Task.WhenAny(loaded.Task, Task.Delay(8000));
        ready = true; Hide();
    }

    /// <summary>Окно, сцена, пилюля и «капля» у края для выбранной стороны. true — геометрия изменилась.</summary>
    private bool Arrange(string newSide, Rectangle area)
    {
        var s = DpiScale;
        var sizeW = (int)Math.Round(StageW * s); var sizeH = (int)Math.Round(StageH * s);
        var e = (int)Math.Round(EdgeMargin * s);
        var st = newSide switch
        {
            "top" => new Rectangle(area.Left + (area.Width - sizeW) / 2, area.Top + 5, sizeW, sizeH),
            "bottom" => new Rectangle(area.Left + (area.Width - sizeW) / 2, area.Bottom - sizeH - 5, sizeW, sizeH),
            "left" => new Rectangle(area.Left + 5, area.Top + (area.Height - sizeH) / 2, sizeW, sizeH),
            _ => new Rectangle(area.Right - sizeW - 5, area.Top + (area.Height - sizeH) / 2, sizeW, sizeH)
        };
        if (st == stageScreen && newSide == side) return false;
        stageScreen = st; side = newSide;
        // окно = сцена + запас к краю экрана, чтобы остров мог «выпрыгивать» из-за края внутри окна
        windowRect = newSide switch
        {
            "top" => new Rectangle(st.X, st.Y - e, st.Width, st.Height + e),
            "bottom" => new Rectangle(st.X, st.Y, st.Width, st.Height + e),
            "left" => new Rectangle(st.X - e, st.Y, st.Width + e, st.Height),
            _ => new Rectangle(st.X, st.Y, st.Width + e, st.Height)
        };
        stageInWindow = new Rectangle(st.X - windowRect.X, st.Y - windowRect.Y, st.Width, st.Height);
        var pw = (int)Math.Round(PillW * s); var ph = (int)Math.Round(PillH * s);
        var sg = stageInWindow;
        pill = newSide switch
        {
            "top" => new Rectangle(sg.Left + (sg.Width - pw) / 2, sg.Top, pw, ph),
            "bottom" => new Rectangle(sg.Left + (sg.Width - pw) / 2, sg.Bottom - ph, pw, ph),
            "left" => new Rectangle(sg.Left, sg.Top + (sg.Height - ph) / 2, pw, ph),
            _ => new Rectangle(sg.Right - pw, sg.Top + (sg.Height - ph) / 2, pw, ph)
        };
        // «капля» целиком за краем экрана: из неё остров выпрыгивает и в неё же уходит
        var cx = pill.Left + pill.Width / 2; var cy = pill.Top + pill.Height / 2;
        var along = (int)Math.Round(pw * 0.45); var across = (int)Math.Round(ph * 0.3);
        var (dw, dh) = newSide is "top" or "bottom" ? (along, across) : (across, (int)Math.Round(ph * 0.5));
        hiddenShape = newSide switch
        {
            "top" => new Rectangle(cx - dw / 2, area.Top - windowRect.Y - dh, dw, dh),
            "bottom" => new Rectangle(cx - dw / 2, area.Bottom - windowRect.Y, dw, dh),
            "left" => new Rectangle(area.Left - windowRect.X - dw, cy - dh / 2, dw, dh),
            _ => new Rectangle(area.Right - windowRect.X, cy - dh / 2, dw, dh)
        };
        Script($"window.setSide&&setSide('{newSide}',{(stageInWindow.X / s).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)},{(stageInWindow.Y / s).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)})");
        return true;
    }

    public void SetMode(IslandMode mode, string newSide, Rectangle area)
    {
        if (!ready) return;
        if (mode == IslandMode.Hidden)
        {
            if (Mode == IslandMode.Hidden) return;
            Mode = IslandMode.Hidden;
            Script("window.setMode&&setMode('hidden')");
            Retarget(hiddenShape, Calm);
            return;
        }
        var layoutChanged = Arrange(newSide, area);
        var wasHidden = Mode == IslandMode.Hidden;
        if (!wasHidden && !layoutChanged && mode == Mode) return;
        var before = Mode;
        Mode = mode;
        if (layoutChanged && Visible) { Visible = false; }   // край/монитор сменили: окно переезжает, остров появится заново
        if (!Visible)
        {
            // появление: стартуем из капли за краем экрана
            SetBounds(windowRect.X, windowRect.Y, windowRect.Width, windowRect.Height);
            sx.Jump(hiddenShape.X); sy.Jump(hiddenShape.Y); sw.Jump(hiddenShape.Width); sh.Jump(hiddenShape.Height);
            Apply(true); Show();
            Script("window.setActive&&setActive(true)");
        }
        Script($"window.setMode&&setMode('{(mode == IslandMode.Compact ? "compact" : "expanded")}')");
        Retarget(mode == IslandMode.Compact ? pill : stageInWindow, before == IslandMode.Expanded ? Medium : Lively);
        PushBackdrop(); backdropTimer.Start();
    }

    private void Retarget(Rectangle rect, (double K, double C) spring)
    {
        sx.T = rect.X; sy.T = rect.Y; sw.T = rect.Width; sh.T = rect.Height;
        stiffness = spring.K; damping = spring.C;
        if (frameTimer.Enabled) return;
        lastTick = clock.ElapsedMilliseconds;
        if (!fineTimer) { Native.timeBeginPeriod(1); fineTimer = true; }   // таймер Windows по умолчанию ~15 мс, нужен ~8
        frameTimer.Start();
    }

    private void OnFrame()
    {
        var now = clock.ElapsedMilliseconds;
        var dt = Math.Min(0.05, (now - lastTick) / 1000.0); lastTick = now;
        var steps = Math.Max(1, (int)Math.Ceiling(dt / 0.004)); var step = dt / steps;
        for (var i = 0; i < steps; i++) { sx.Step(step, stiffness, damping); sy.Step(step, stiffness, damping); sw.Step(step, stiffness, damping); sh.Step(step, stiffness, damping); }
        if (sx.Settled && sy.Settled && sw.Settled && sh.Settled)
        {
            sx.Snap(); sy.Snap(); sw.Snap(); sh.Snap();
            Apply(true);
            frameTimer.Stop();
            if (fineTimer) { Native.timeEndPeriod(1); fineTimer = false; }
            if (Mode == IslandMode.Hidden)
            {
                backdropTimer.Stop(); Hide();
                Script("window.setActive&&setActive(false)");   // скрытое окно не рисуем вообще
            }
            return;
        }
        Apply(false);
    }

    /// <summary>Скругление растёт вместе с высотой: пилюля = капсула, карточка = 42 px.</summary>
    private int RadiusFor(double w, double h)
    {
        var t = Math.Clamp((h - pill.Height) / Math.Max(1.0, stageInWindow.Height - pill.Height), 0, 1);
        return (int)Math.Round(Math.Min(Math.Min(w, h) / 2.0, (PillR + (CardR - PillR) * t) * DpiScale));
    }

    /// <summary>Только форма окна: SetWindowRgn. Окно и WebView2 остаются на месте.</summary>
    private void Apply(bool force)
    {
        if (!IsHandleCreated) return;
        var x = (int)Math.Round(sx.X); var y = (int)Math.Round(sy.X);
        var w = Math.Max(2, (int)Math.Round(sw.X)); var h = Math.Max(2, (int)Math.Round(sh.X));
        var radius = RadiusFor(w, h);
        var region = Native.CreateRoundRectRgn(x, y, x + w + 1, y + h + 1, radius * 2, radius * 2);
        if (Native.SetWindowRgn(Handle, region, true) == 0) Native.DeleteObject(region);   // при успехе регион принадлежит Windows
        PostShape(force);
    }

    /// <summary>Страница рисует форму с задержкой в 1–2 кадра, поэтому ей отправляем форму «на PageLeadMs вперёд».</summary>
    private void PostShape(bool force)
    {
        var now = clock.ElapsedMilliseconds;
        if (!force && now - lastPost < 10) return;
        lastPost = now;
        if (view.CoreWebView2 == null) return;
        var ax = sx; var ay = sy; var aw = sw; var ah = sh;
        if (!force)
        {
            const int steps = 7; var dt = PageLeadMs / 1000.0 / steps;
            for (var i = 0; i < steps; i++) { ax.Step(dt, stiffness, damping); ay.Step(dt, stiffness, damping); aw.Step(dt, stiffness, damping); ah.Step(dt, stiffness, damping); }
        }
        var w = Math.Max(2, aw.X); var h = Math.Max(2, ah.X);
        var s = DpiScale;
        view.CoreWebView2.PostWebMessageAsString(FormattableString.Invariant($"S:{ax.X / s:F1},{ay.X / s:F1},{w / s:F1},{h / s:F1},{RadiusFor(w, h) / s:F1}"));
    }

    /// <summary>Окно не попадает в снимок экрана — иначе остров фотографировал бы сам себя.</summary>
    public void SetCapture(bool on)
    {
        captureOn = on;
        if (IsHandleCreated) Native.SetWindowDisplayAffinity(Handle, on ? 0x11u : 0u);
        if (!on) Script("window.LiquidGlass&&LiquidGlass.clearBackdrop()");
    }

    public void Render(JsonObject state)
    {
        if (!ready) return;
        var json = state.ToJsonString();
        if (json == lastState) return;
        lastState = json; Script("window.renderDrop&&renderDrop(" + json + ")");
    }

    private void PushBackdrop()
    {
        if (!ready || !captureOn || Mode == IslandMode.Hidden || !Visible) return;
        // окно неподвижно: снимок рабочего стола берём вокруг него, а положение окна на экране шлём странице
        var rect = windowRect; var pad = (int)Math.Round(80 * DpiScale); var left = windowRect.Left; var top = windowRect.Top;
        _ = Task.Run(() =>
        {
            try
            {
                var result = DesktopCapture.Grab(rect, pad, 0.6, 72);
                if (result == null) return;
                var json = JsonSerializer.Serialize(result);
                BeginInvoke(() => { if (!IsDisposed && Mode != IslandMode.Hidden) Script($"window.LiquidGlass&&LiquidGlass.setPos({left},{top});(()=>{{const r={json};window.setBackdrop&&setBackdrop(r.data,r)}})()"); });
            }
            catch (Exception error) { AppPaths.Log("Island backdrop: " + error.Message); }
        });
    }

    private void Script(string code) { if (view.CoreWebView2 != null) _ = view.ExecuteScriptAsync(code); }
    // без стирания фона: иначе при каждой смене региона окно успевает мигнуть чёрным
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x14) { m.Result = 1; return; }          // WM_ERASEBKGND
        if (m.Msg == 0x21) { m.Result = 3; return; }          // WM_MOUSEACTIVATE -> MA_NOACTIVATE
        base.WndProc(ref m);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            frameTimer.Dispose(); backdropTimer.Dispose();
            if (fineTimer) { Native.timeEndPeriod(1); fineTimer = false; }
        }
        base.Dispose(disposing);
    }
}
