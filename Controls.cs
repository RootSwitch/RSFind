// Owner-drawn controls that follow the Canvas Suite design language: flat
// components, 1px borders, 4-6px radii, accent-colored focus, no gradients.
//
// WinForms will not theme its stock chrome - a NumericUpDown keeps system
// colored spin buttons and a CheckBox keeps a system glyph, both of which look
// wrong on 25 of the 29 palettes. So the pieces that would give the game away
// are painted here instead, and every one of them reads Th.T at paint time so
// a theme switch is a repaint rather than a control rebuild.
//
// C# 5 only (in-box csc).

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

namespace RSFind
{
    // The one DPI scale factor, sampled once at startup after the process
    // declares itself DPI-aware. Every hand-placed pixel dimension goes through
    // S(). Declaring awareness without scaling the layout would trade "blurry
    // at 150%" for "tiny at 150%", which is worse; the two ship together.
    public static class Dpi
    {
        public static double Factor = 1.0;

        public static void Init()
        {
            IntPtr screen = IntPtr.Zero;
            using (Graphics g = Graphics.FromHwnd(screen))
                Factor = g.DpiX / 96.0;
            if (Factor < 1.0) Factor = 1.0;
        }

        public static int S(int logicalPx)
        {
            return (int)Math.Round(logicalPx * Factor);
        }
    }

    public static class Draw
    {
        public static GraphicsPath Round(Rectangle r, int radius)
        {
            int d = radius * 2;
            GraphicsPath p = new GraphicsPath();
            if (radius <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Rectangle r, int radius, Color fill)
        {
            using (GraphicsPath p = Round(r, radius))
            using (SolidBrush b = new SolidBrush(fill))
                g.FillPath(b, p);
        }

        // Border rectangles are inset by 1px so the 1px pen is not clipped.
        public static void FillBorderRound(Graphics g, Rectangle r, int radius, Color fill, Color border)
        {
            Rectangle inner = new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1);
            using (GraphicsPath p = Round(inner, radius))
            {
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, p);
                using (Pen pen = new Pen(border)) g.DrawPath(pen, p);
            }
        }
    }

    // A 1px-bordered, input-colored container for a borderless child control.
    // This is the only way to get a themed border on a TextBox: the control
    // draws its own system border otherwise, and BorderStyle has no color.
    public class InputHost : Panel
    {
        Control _child;
        bool _focused;

        public InputHost(Control child, int padX, int padY)
        {
            _child = child;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Padding = new Padding(padX, padY, padX, padY);
            child.Dock = DockStyle.Fill;
            Controls.Add(child);
            child.GotFocus += OnChildFocus;
            child.LostFocus += OnChildBlur;
            Th.Changed += OnThemeChanged;
            OnThemeChanged(null, EventArgs.Empty);   // the child starts system-colored
        }

        void OnChildFocus(object sender, EventArgs e) { _focused = true; Invalidate(); }
        void OnChildBlur(object sender, EventArgs e) { _focused = false; Invalidate(); }

        void OnThemeChanged(object sender, EventArgs e)
        {
            _child.BackColor = Th.T.Input;
            _child.ForeColor = Th.T.Txt;
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Th.Changed -= OnThemeChanged;
            base.Dispose(disposing);
        }

        // The two smoothing modes are not interchangeable and the order is
        // load-bearing. A rounded shape leaves antialiased pixels outside its
        // path, and those blend with whatever is already in the buffer - so
        // the parent's color has to be laid down first, flat, or every control
        // wears a dark fringe on light themes and a light one on dark. The
        // fill itself is drawn with smoothing off because an antialiased
        // rectangle feathers its own edges against the very pixels the border
        // is about to occupy.
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.None;
            using (SolidBrush b = new SolidBrush(Parent != null ? Parent.BackColor : Th.T.Panel))
                e.Graphics.FillRectangle(b, ClientRectangle);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Draw.FillBorderRound(e.Graphics, ClientRectangle, 4, Th.T.Input,
                                 _focused ? Th.T.Accent : Th.T.Border);
        }
    }

    public class ThemedButton : Button
    {
        public bool Primary;
        bool _hover;

        // False makes the button toolbar-like: clicking it acts without taking
        // focus. Cancel uses it, so that stopping a long scan leaves the caret
        // in the search box ready for the next query rather than parked on a
        // button that is about to disable itself.
        public bool TakesFocus
        {
            set { SetStyle(ControlStyles.Selectable, value); }
        }

        public ThemedButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Th.Changed += OnThemeChanged;
        }

        void OnThemeChanged(object sender, EventArgs e) { Invalidate(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Th.Changed -= OnThemeChanged;
            base.Dispose(disposing);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Th.T;
            e.Graphics.SmoothingMode = SmoothingMode.None;   // see the note in InputHost.OnPaint
            using (SolidBrush bg = new SolidBrush(Parent != null ? Parent.BackColor : t.Panel))
                e.Graphics.FillRectangle(bg, ClientRectangle);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            Color fill, border, text;
            if (Primary)
            {
                // .btn-primary: accent fill, white text, brightness(1.1) hover.
                fill = _hover && Enabled ? Th.Brighten(t.Accent, 1.1) : t.Accent;
                border = fill;
                text = Th.OnColor(t.Accent);
            }
            else
            {
                fill = t.Panel2;
                border = _hover && Enabled ? t.Accent : t.Border;
                text = t.Txt;
            }
            if (!Enabled)
            {
                // button:disabled { opacity: 0.5 }
                fill = Th.Mix(fill, Parent != null ? Parent.BackColor : t.Panel, 0.5);
                border = Th.Mix(t.Border, Parent != null ? Parent.BackColor : t.Panel, 0.5);
                text = t.TxtDim;
            }

            Draw.FillBorderRound(e.Graphics, ClientRectangle, 4, fill, border);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis);
        }
    }

    // input[type=checkbox] { accent-color: var(--se-accent); width:15px; height:15px }
    public class ThemedCheck : CheckBox
    {
        static int Box { get { return Dpi.S(15); } }
        static int Gap { get { return Dpi.S(8); } }
        bool _hover;

        public ThemedCheck()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AutoSize = false;
            Th.Changed += OnThemeChanged;
        }

        void OnThemeChanged(object sender, EventArgs e) { Invalidate(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Th.Changed -= OnThemeChanged;
            base.Dispose(disposing);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        // AutoSize cannot be trusted once we take over painting, so callers size
        // the control from its text instead. Measure with the same flags OnPaint
        // draws with, or the label comes out clipped.
        public void SizeToText()
        {
            Size s = TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            Size = new Size(Box + Gap + s.Width + Dpi.S(6), Math.Max(Box + 4, s.Height + Dpi.S(6)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Th.T;
            e.Graphics.SmoothingMode = SmoothingMode.None;   // see the note in InputHost.OnPaint
            using (SolidBrush bg = new SolidBrush(Parent != null ? Parent.BackColor : t.Panel))
                e.Graphics.FillRectangle(bg, ClientRectangle);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            int top = (Height - Box) / 2;
            Rectangle box = new Rectangle(0, top, Box, Box);
            Color border = Checked ? t.Accent : (_hover && Enabled ? t.Accent : t.Border);
            Color fill = Checked ? t.Accent : t.Input;
            if (!Enabled)
            {
                fill = Th.Mix(fill, Parent != null ? Parent.BackColor : t.Panel, 0.5);
                border = Th.Mix(border, Parent != null ? Parent.BackColor : t.Panel, 0.5);
            }
            Draw.FillBorderRound(e.Graphics, box, 3, fill, border);

            if (Checked)
            {
                // Glyph geometry in fifteenths of the box, so it scales with it.
                float u = box.Width / 15f;
                using (Pen pen = new Pen(Th.OnColor(t.Accent), Math.Max(2f, 2f * u)))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    e.Graphics.DrawLines(pen, new PointF[] {
                        new PointF(box.Left + 3 * u, box.Top + 7 * u),
                        new PointF(box.Left + 6 * u, box.Top + 10 * u),
                        new PointF(box.Left + 11 * u, box.Top + 4 * u)
                    });
                }
            }

            Rectangle textRect = new Rectangle(Box + Gap, 0, Width - Box - Gap, Height);
            TextRenderer.DrawText(e.Graphics, Text, Font, textRect,
                Enabled ? t.Txt : t.TxtDim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    // Numeric field with painted spin arrows. Replaces NumericUpDown, whose
    // spin buttons render in system colors no matter what you set.
    // The inner field of a SpinBox. Keypresses were already filtered to digits;
    // Ctrl+V was not, and that is the path that mattered: a pasted value too
    // large for int makes TryParse fail, and SpinBox.Value then falls back to
    // its minimum. Here that turns a size cap into zero, which the engine
    // reads as no cap at all - so a stray paste into the wrong box would send
    // the scan through every multi-gigabyte file it can reach.
    class DigitBox : TextBox
    {
        const int WM_PASTE = 0x0302;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_PASTE)
            {
                PasteDigits();
                return;
            }
            base.WndProc(ref m);
        }

        void PasteDigits()
        {
            string clip;
            try
            {
                if (!Clipboard.ContainsText()) return;
                clip = Clipboard.GetText();
            }
            catch (System.Runtime.InteropServices.ExternalException) { return; }
            if (clip == null) return;

            StringBuilder digits = new StringBuilder(clip.Length);
            for (int i = 0; i < clip.Length; i++)
                if (char.IsDigit(clip[i])) digits.Append(clip[i]);
            if (digits.Length == 0) return;

            // Longer than int can hold is not a number the caller can use;
            // keeping the leading digits beats discarding the paste silently.
            if (digits.Length > 9) digits.Length = 9;
            SelectedText = digits.ToString();
        }
    }

    public class SpinBox : Control
    {
        static int ArrowW { get { return Dpi.S(18); } }

        TextBox _box;
        int _min = 0;
        int _max = 100;
        int _hoverArrow;   // 0 none, 1 up, 2 down
        bool _enabled = true;

        public event EventHandler ValueChanged;

        // Hides Control.Enabled on purpose. Truly disabling the control
        // disables the child TextBox, and a disabled EDIT ignores BackColor
        // and paints system gray - a bright hole in 20 of the 29 palettes.
        // Instead the child stays enabled but read-only, and the chrome is
        // painted dimmed.
        public new bool Enabled
        {
            get { return _enabled; }
            set
            {
                _enabled = value;
                if (_box != null)
                {
                    _box.ReadOnly = !value;
                    _box.TabStop = value;
                    _box.ForeColor = value ? Th.T.Txt : Th.T.TxtDim;
                    _box.BackColor = Th.T.Input;   // re-assert: ReadOnly toggles reset it
                }
                TabStop = value;
                Invalidate();
            }
        }

        public SpinBox(int min, int max, int value)
        {
            _min = min;
            _max = max;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            _box = new DigitBox();
            _box.BorderStyle = BorderStyle.None;
            _box.TextAlign = HorizontalAlignment.Left;
            _box.Text = value.ToString(CultureInfo.InvariantCulture);
            _box.GotFocus += Repaint;
            _box.LostFocus += OnBoxBlur;
            _box.KeyPress += OnBoxKeyPress;
            _box.TextChanged += OnBoxTextChanged;
            Controls.Add(_box);
            Th.Changed += OnThemeChanged;
            OnThemeChanged(null, EventArgs.Empty);   // the child starts system-colored
        }

        void OnThemeChanged(object sender, EventArgs e)
        {
            _box.BackColor = Th.T.Input;
            _box.ForeColor = _enabled ? Th.T.Txt : Th.T.TxtDim;
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Th.Changed -= OnThemeChanged;
            base.Dispose(disposing);
        }

        public override Font Font
        {
            get { return base.Font; }
            set { base.Font = value; if (_box != null) { _box.Font = value; Relayout(); } }
        }

        // The form font arrives ambiently after parenting, which skips the
        // setter above - sync the child here too.
        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            if (_box != null) { _box.Font = Font; Relayout(); }
        }

        public int Value
        {
            get
            {
                int n;
                if (int.TryParse(_box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                    return Clamp(n);
                return _min;
            }
            set
            {
                _box.Text = Clamp(value).ToString(CultureInfo.InvariantCulture);
            }
        }

        int Clamp(int n) { return n < _min ? _min : (n > _max ? _max : n); }

        void Repaint(object sender, EventArgs e) { Invalidate(); }

        void OnBoxBlur(object sender, EventArgs e)
        {
            // Normalize whatever was typed once the field is left, so an empty
            // or out-of-range box never silently reports the minimum.
            _box.Text = Value.ToString(CultureInfo.InvariantCulture);
            Invalidate();
        }

        void OnBoxKeyPress(object sender, KeyPressEventArgs e)
        {
            if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true;
        }

        void OnBoxTextChanged(object sender, EventArgs e)
        {
            if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); }

        void Relayout()
        {
            if (_box == null) return;
            int h = _box.PreferredHeight;
            _box.SetBounds(Dpi.S(7), Math.Max(1, (Height - h) / 2),
                           Math.Max(10, Width - ArrowW - Dpi.S(9)), h);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!_enabled) return;
            int was = _hoverArrow;
            _hoverArrow = 0;
            if (e.X >= Width - ArrowW - 1)
                _hoverArrow = e.Y < Height / 2 ? 1 : 2;
            if (was != _hoverArrow) Invalidate();
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hoverArrow != 0) { _hoverArrow = 0; Invalidate(); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (!_enabled) return;
            if (e.X >= Width - ArrowW - 1)
            {
                Value = Value + (e.Y < Height / 2 ? 1 : -1);
                _box.Focus();
                _box.SelectionStart = _box.Text.Length;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (_enabled && _box.Focused) Value = Value + (e.Delta > 0 ? 1 : -1);
            base.OnMouseWheel(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Th.T;
            Color back = Parent != null ? Parent.BackColor : t.Panel;
            e.Graphics.SmoothingMode = SmoothingMode.None;   // see the note in InputHost.OnPaint
            using (SolidBrush bg = new SolidBrush(back))
                e.Graphics.FillRectangle(bg, ClientRectangle);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            // Disabled = same shapes, everything blended toward the panel,
            // mirroring the 0.5-opacity treatment the suite gives buttons.
            Color border = _enabled ? (_box.Focused ? t.Accent : t.Border)
                                    : Th.Mix(t.Border, back, 0.5);
            Color fill = _enabled ? t.Input : Th.Mix(t.Input, back, 0.5);
            Draw.FillBorderRound(e.Graphics, ClientRectangle, 4, fill, border);

            Color arrowIdle = _enabled ? t.TxtDim : Th.Mix(t.TxtDim, back, 0.5);
            int cx = Width - ArrowW / 2 - Dpi.S(3);
            int gap = Dpi.S(5);
            DrawArrow(e.Graphics, cx, Height / 2 - gap, true, _hoverArrow == 1 && _enabled ? t.Accent : arrowIdle);
            DrawArrow(e.Graphics, cx, Height / 2 + gap, false, _hoverArrow == 2 && _enabled ? t.Accent : arrowIdle);
        }

        static void DrawArrow(Graphics g, int cx, int cy, bool up, Color color)
        {
            Point[] pts = up
                ? new Point[] { new Point(cx - 4, cy + 2), new Point(cx + 4, cy + 2), new Point(cx, cy - 3) }
                : new Point[] { new Point(cx - 4, cy - 2), new Point(cx + 4, cy - 2), new Point(cx, cy + 3) };
            using (SolidBrush b = new SolidBrush(color)) g.FillPolygon(b, pts);
        }
    }

    // Menu chrome. ProfessionalColorTable is the only supported way to recolor
    // ToolStrip surfaces without painting every item by hand.
    // A dropdown that matches the rest of the chrome.
    //
    // Not a ComboBox. A real one paints its list from the system palette and
    // ignores most attempts to theme it, which on the darker two thirds of the
    // 29 palettes is a bright white hole - the same reason SpinBox hosts a
    // borderless TextBox rather than using a NumericUpDown. This paints its own
    // closed state and opens the ContextMenuStrip the app already themes
    // everywhere else, so a palette cannot be added that forgets about it.
    public class ThemedDropdown : Control
    {
        static int CaretW { get { return Dpi.S(20); } }

        string[] _items = new string[0];
        int _index;
        bool _hover;
        bool _open;
        ContextMenuStrip _menu;

        public event EventHandler SelectedIndexChanged;

        public ThemedDropdown()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = true;
            Th.Changed += OnThemeChanged;
        }

        void OnThemeChanged(object sender, EventArgs e) { Invalidate(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Th.Changed -= OnThemeChanged;
                if (_menu != null) _menu.Dispose();
            }
            base.Dispose(disposing);
        }

        public void SetItems(string[] items)
        {
            _items = items == null ? new string[0] : items;
            if (_index >= _items.Length) _index = 0;
            Invalidate();
        }

        public int SelectedIndex
        {
            get { return _index; }
            set { SetSelectedIndex(value, true); }
        }

        public string SelectedItem
        {
            get { return _index >= 0 && _index < _items.Length ? _items[_index] : ""; }
        }

        // notify:false exists for loading saved settings, where raising the
        // event would write the file back out during the read that produced it.
        public void SetSelectedIndex(int index, bool notify)
        {
            if (index < 0 || index >= _items.Length || index == _index) return;
            _index = index;
            Invalidate();
            if (notify && SelectedIndexChanged != null)
                SelectedIndexChanged(this, EventArgs.Empty);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e); _hover = true; Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e); _hover = false; Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) { Focus(); Open(); }
        }

        // Without this the arrow keys are eaten as navigation and never reach
        // OnKeyDown, so a focused dropdown moves focus instead of changing.
        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Up || keyData == Keys.Down) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Down) { SetSelectedIndex(_index + 1, true); e.Handled = true; }
            else if (e.KeyCode == Keys.Up) { SetSelectedIndex(_index - 1, true); e.Handled = true; }
            else if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { Open(); e.Handled = true; }
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        void Open()
        {
            if (_items.Length == 0) return;
            if (_menu == null)
            {
                _menu = new ContextMenuStrip();
                _menu.Renderer = new ToolStripProfessionalRenderer(new ThemeColorTable());
                _menu.ShowImageMargin = false;
                _menu.Closed += delegate { _open = false; Invalidate(); };
            }

            // Rebuilt per open rather than kept, so the check mark cannot drift
            // from the selection and one menu object is reused either way.
            _menu.Items.Clear();
            for (int i = 0; i < _items.Length; i++)
            {
                ToolStripMenuItem item = new ToolStripMenuItem(_items[i]);
                int pick = i;
                item.Checked = i == _index;
                item.Click += delegate { SetSelectedIndex(pick, true); };
                _menu.Items.Add(item);
            }

            MenuTheme.Apply(_menu);
            _open = true;
            Invalidate();
            _menu.Show(this, new Point(0, Height));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme t = Th.T;
            Color back = Parent != null ? Parent.BackColor : t.Panel;
            e.Graphics.SmoothingMode = SmoothingMode.None;   // see the note in InputHost.OnPaint
            using (SolidBrush bg = new SolidBrush(back))
                e.Graphics.FillRectangle(bg, ClientRectangle);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            Color border = (Focused || _open) ? t.Accent
                         : _hover ? Th.Mix(t.Border, t.Accent, 0.5)
                         : t.Border;
            Draw.FillBorderRound(e.Graphics, ClientRectangle, 4, t.Input, border);

            Rectangle text = new Rectangle(Dpi.S(7), 0,
                                           Math.Max(0, Width - CaretW - Dpi.S(8)), Height);
            TextRenderer.DrawText(e.Graphics, SelectedItem, Font, text, t.Txt,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

            int cx = Width - CaretW / 2 - Dpi.S(3);
            int cy = Height / 2;
            int w = Dpi.S(4);
            Point[] caret = {
                new Point(cx - w, cy - w / 2),
                new Point(cx + w, cy - w / 2),
                new Point(cx, cy + w)
            };
            using (SolidBrush b = new SolidBrush(_hover || _open ? t.Accent : t.TxtDim))
                e.Graphics.FillPolygon(b, caret);
        }
    }

    public class ThemeColorTable : ProfessionalColorTable
    {
        public ThemeColorTable() { UseSystemColors = false; }

        public override Color ToolStripDropDownBackground { get { return Th.T.Panel; } }
        public override Color MenuBorder { get { return Th.T.Border; } }
        public override Color MenuItemBorder { get { return Th.T.Accent; } }
        public override Color MenuItemSelected { get { return Th.T.Panel2; } }
        public override Color MenuItemSelectedGradientBegin { get { return Th.T.Panel2; } }
        public override Color MenuItemSelectedGradientEnd { get { return Th.T.Panel2; } }
        public override Color MenuItemPressedGradientBegin { get { return Th.T.Panel; } }
        public override Color MenuItemPressedGradientMiddle { get { return Th.T.Panel; } }
        public override Color MenuItemPressedGradientEnd { get { return Th.T.Panel; } }
        public override Color ImageMarginGradientBegin { get { return Th.T.Panel; } }
        public override Color ImageMarginGradientMiddle { get { return Th.T.Panel; } }
        public override Color ImageMarginGradientEnd { get { return Th.T.Panel; } }
        public override Color CheckBackground { get { return Th.T.Accent; } }
        public override Color CheckSelectedBackground { get { return Th.T.Accent; } }
        public override Color CheckPressedBackground { get { return Th.T.Accent; } }
        public override Color ButtonSelectedBorder { get { return Th.T.Accent; } }
        public override Color SeparatorDark { get { return Th.T.Border; } }
        public override Color SeparatorLight { get { return Th.T.Border; } }
    }

    public static class MenuTheme
    {
        // Applied on open as well as on build, because a theme switch made from
        // inside the menu has to recolor the menu that is still on screen.
        public static void Apply(ToolStrip strip)
        {
            strip.BackColor = Th.T.Panel;
            strip.ForeColor = Th.T.Txt;
            ApplyItems(strip.Items);
        }

        static void ApplyItems(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                item.BackColor = Th.T.Panel;
                item.ForeColor = Th.T.Txt;
                ToolStripMenuItem mi = item as ToolStripMenuItem;
                if (mi != null && mi.HasDropDownItems)
                {
                    mi.DropDown.BackColor = Th.T.Panel;
                    ApplyItems(mi.DropDownItems);
                }
            }
        }
    }

    // Chrome that belongs to the OS rather than to WinForms. Neither of these
    // can be given an arbitrary color - Windows offers a dark variant and a
    // light one - so each palette picks whichever side it sits on. Without this
    // a dark theme still shows white scrollbars and a white title bar, which is
    // the detail that makes the whole window look unthemed.
    public static class OsChrome
    {
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        const int DWMWA_USE_IMMERSIVE_DARK_MODE_PRE_20H1 = 19;

        public static bool IsDark(Color c)
        {
            return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0 < 0.5;
        }

        public static void ApplyTitleBar(Form form)
        {
            if (form == null || !form.IsHandleCreated) return;
            int on = IsDark(Th.T.Panel) ? 1 : 0;
            // Attribute 20 is the documented one; builds before 20H1 used 19.
            if (Native.DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, 4) != 0)
                Native.DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_PRE_20H1, ref on, 4);
        }

        // Called once at startup: without it the per-window calls below do
        // nothing. AllowDark(1) opts in without overriding the user's setting.
        public static void EnableDarkModeSupport()
        {
            try { Native.SetPreferredAppMode(1 /* AllowDark */); }
            catch (EntryPointNotFoundException) { }   // pre-1809
            catch (DllNotFoundException) { }
        }

        public static void ApplyScrollBars(Control c)
        {
            if (c == null || !c.IsHandleCreated) return;
            bool dark = IsDark(Th.T.Input);
            try { Native.AllowDarkModeForWindow(c.Handle, dark); }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }
            Native.SetWindowTheme(c.Handle, dark ? "DarkMode_Explorer" : "Explorer", null);
            // An EDIT control caches its scrollbar theme; it only re-reads it on
            // WM_THEMECHANGED, and the bars live in the non-client area so a
            // plain Invalidate never reaches them.
            Native.SendMessage(c.Handle, Native.WM_THEMECHANGED, IntPtr.Zero, IntPtr.Zero);
            Native.RedrawWindow(c.Handle, IntPtr.Zero, IntPtr.Zero,
                Native.RDW_FRAME | Native.RDW_INVALIDATE | Native.RDW_UPDATENOW);
        }
    }

    public static class Brand
    {
        // RSFind's mark is a magnifier over lines of text, drawn rather than
        // shipped as an .ico so it can be recolored per theme and so the repo
        // carries no binary asset.
        //
        // Its own subject, in the family grammar: the suite's easel and the
        // style guide's two-squares-and-an-elbow are neither of them templates,
        // and RSPaster's clipboard is not this app. What carries the family is
        // the geometry - a 64-unit tile at rx 12, roughly 4-unit strokes,
        // logo-a as the primary stroke and logo-b as the secondary - not the
        // shape standing inside it. Matches favicon.svg; change both together.
        public static void PaintMark(Graphics g, Rectangle r, Color glass, Color lines)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float u = r.Width / 64f;          // one unit of the 64-unit tile

            // The text being searched: three rules, shortening downward, in
            // the secondary color. Drawn first so the lens sits over them.
            using (Pen pen = new Pen(lines, Math.Max(1f, 4f * u)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLine(pen, r.X + 12 * u, r.Y + 16 * u, r.X + 46 * u, r.Y + 16 * u);
                g.DrawLine(pen, r.X + 12 * u, r.Y + 28 * u, r.X + 38 * u, r.Y + 28 * u);
                g.DrawLine(pen, r.X + 12 * u, r.Y + 40 * u, r.X + 30 * u, r.Y + 40 * u);
            }

            // The lens. Filled with the ground color before the rim is stroked,
            // so the rules stop at its edge instead of running through the
            // glass - the one detail that makes it read as a lens rather than
            // as a circle laid on top of some lines.
            RectangleF lens = new RectangleF(r.X + 26 * u, r.Y + 24 * u, 26 * u, 26 * u);
            using (SolidBrush fill = new SolidBrush(Th.T.Panel))
                g.FillEllipse(fill, lens);
            using (Pen pen = new Pen(glass, Math.Max(1f, 4.5f * u)))
            {
                g.DrawEllipse(pen, lens);
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLine(pen, r.X + 48 * u, r.Y + 46 * u, r.X + 56 * u, r.Y + 54 * u);
            }
        }

        // Bitmap.GetHicon() hands back a handle the Icon wrapper does not own,
        // so callers must DestroyIcon the previous one on every theme switch or
        // the process bleeds GDI handles.
        public static Icon CreateIcon(int size, out IntPtr handle)
        {
            using (Bitmap bmp = new Bitmap(size, size))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    Brand.PaintMark(g, new Rectangle(0, 0, size, size), Th.T.LogoA, Th.T.LogoB);
                }
                handle = bmp.GetHicon();
                return Icon.FromHandle(handle);
            }
        }
    }
}
