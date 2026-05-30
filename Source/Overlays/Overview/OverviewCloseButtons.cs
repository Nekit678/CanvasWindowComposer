using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CanvasDesktop;

/// <summary>
/// Small topmost close buttons layered above overview thumbnails.
/// </summary>
internal sealed class OverviewCloseButtons : IDisposable
{
    private const int ButtonSize = 22;
    private const int ButtonMargin = 6;
    private const int MinWindowSizeForButton = 44;
    private const float CloseLineWidth = 2.1f;

    private readonly IReadOnlyList<OverviewOverlay> _passes;
    private readonly OverviewWindowList _windows;
    private readonly OverviewCamera _camera;
    private readonly IWindowApi _win32;
    private readonly Action<IntPtr> _closeRequested;
    private readonly Dictionary<IntPtr, CloseButtonForm> _buttons = new();

    public OverviewCloseButtons(
        IReadOnlyList<OverviewOverlay> passes,
        OverviewWindowList windows,
        OverviewCamera camera,
        IWindowApi win32,
        Action<IntPtr> closeRequested)
    {
        _passes = passes;
        _windows = windows;
        _camera = camera;
        _win32 = win32;
        _closeRequested = closeRequested;
    }

    public void Reconcile(bool visible)
    {
        if (!visible)
        {
            Hide();
            return;
        }

        var seen = new HashSet<IntPtr>();
        var orderedButtons = new List<CloseButtonForm>();
        for (int i = 0; i < _windows.Count; i++)
        {
            var entry = _windows.Windows[i];
            if (!TryGetButtonBounds(entry, out var bounds))
                continue;

            seen.Add(entry.HWnd);
            if (!_buttons.TryGetValue(entry.HWnd, out var button))
            {
                button = new CloseButtonForm(_closeRequested);
                _buttons[entry.HWnd] = button;
            }

            button.SetTarget(entry.HWnd);
            if (button.Bounds != bounds)
                button.Bounds = bounds;
            if (!button.Visible)
                button.Show();
            orderedButtons.Add(button);
        }

        // _windows is topmost-first. Raise bottom-to-top so topmost close
        // buttons win when thumbnails overlap.
        for (int i = orderedButtons.Count - 1; i >= 0; i--)
            RaiseButton(orderedButtons[i]);

        var stale = new List<IntPtr>();
        foreach (var (hWnd, button) in _buttons)
        {
            if (seen.Contains(hWnd)) continue;
            button.Dispose();
            stale.Add(hWnd);
        }
        foreach (var hWnd in stale)
            _buttons.Remove(hWnd);
    }

    public void Hide()
    {
        foreach (var button in _buttons.Values)
            button.Hide();
    }

    private bool TryGetButtonBounds(OverviewWindowList.Entry entry, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;

        var screen = GetScreenRect(entry);
        if (screen.W < MinWindowSizeForButton || screen.H < MinWindowSizeForButton)
            return false;

        var windowRect = new Rectangle(screen.X, screen.Y, screen.W, screen.H);
        Rectangle bestVisible = Rectangle.Empty;
        long bestArea = 0;
        foreach (var pass in _passes)
        {
            var visible = Rectangle.Intersect(windowRect, pass.Screen.Bounds);
            if (visible.Width < ButtonSize || visible.Height < ButtonSize)
                continue;

            long area = (long)visible.Width * visible.Height;
            if (area > bestArea)
            {
                bestArea = area;
                bestVisible = visible;
            }
        }

        if (bestArea == 0)
            return false;

        int desiredX = screen.X + screen.W - ButtonSize - ButtonMargin;
        int desiredY = screen.Y + ButtonMargin;
        int x = Math.Clamp(desiredX, bestVisible.Left, bestVisible.Right - ButtonSize);
        int y = Math.Clamp(desiredY, bestVisible.Top, bestVisible.Bottom - ButtonSize);
        bounds = new Rectangle(x, y, ButtonSize, ButtonSize);
        return true;
    }

    private WindowRect GetScreenRect(OverviewWindowList.Entry entry)
    {
        if (entry.ScreenFixed)
        {
            var (x, y, w, h) = _win32.GetWindowRect(entry.HWnd);
            return new WindowRect(x, y, Math.Max(1, w), Math.Max(1, h));
        }

        var world = entry.World;
        return new WindowRect(
            (int)((world.X - _camera.X) * _camera.Zoom),
            (int)((world.Y - _camera.Y) * _camera.Zoom),
            Math.Max(1, (int)(world.W * _camera.Zoom)),
            Math.Max(1, (int)(world.H * _camera.Zoom)));
    }

    private static void RaiseButton(CloseButtonForm button)
    {
        if (!button.IsHandleCreated) return;

        HWND topMost = (HWND)new IntPtr(-1);
        PInvoke.SetWindowPos((HWND)button.Handle, topMost, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE |
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
            SET_WINDOW_POS_FLAGS.SWP_NOSENDCHANGING);
    }

    public void Dispose()
    {
        foreach (var button in _buttons.Values)
            button.Dispose();
        _buttons.Clear();
    }

    private sealed class CloseButtonForm : Form
    {
        private readonly Action<IntPtr> _closeRequested;
        private bool _hover;
        private bool _pressed;

        public IntPtr TargetHWnd { get; private set; }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |=
                    (int)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW |
                    (int)WINDOW_EX_STYLE.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        public CloseButtonForm(Action<IntPtr> closeRequested)
        {
            _closeRequested = closeRequested;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            ClientSize = new Size(ButtonSize, ButtonSize);
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            BackColor = Color.Black;
            Opacity = 0.92;
            SetEllipseRegion();
        }

        public void SetTarget(IntPtr hWnd)
        {
            TargetHWnd = hWnd;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            SetEllipseRegion();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            _pressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _pressed = true;
                Invalidate();
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool activate = _pressed && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location);
            _pressed = false;
            Invalidate();
            if (activate)
                _closeRequested(TargetHWnd);
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new RectangleF(1.0f, 1.0f, Width - 2.0f, Height - 2.0f);
            Color fill = _pressed
                ? Color.FromArgb(230, 150, 28, 28)
                : _hover
                    ? Color.FromArgb(235, 214, 44, 44)
                    : Color.FromArgb(220, 36, 36, 36);

            using (var brush = new SolidBrush(fill))
                e.Graphics.FillEllipse(brush, rect);
            using (var border = new Pen(Color.FromArgb(230, 255, 255, 255), 1.0f))
                e.Graphics.DrawEllipse(border, rect);

            float pad = 7.0f;
            using var pen = new Pen(Color.White, CloseLineWidth)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            e.Graphics.DrawLine(pen, pad, pad, Width - pad, Height - pad);
            e.Graphics.DrawLine(pen, Width - pad, pad, pad, Height - pad);
        }

        private void SetEllipseRegion()
        {
            using var path = new GraphicsPath();
            path.AddEllipse(0, 0, Width, Height);
            Region?.Dispose();
            Region = new Region(path);
        }
    }
}
