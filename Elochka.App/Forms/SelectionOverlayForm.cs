using System.Drawing.Drawing2D;

namespace Elochka.App.Forms;

internal sealed class SelectionOverlayForm : Form
{
    private const int MinimumSelectionSize = 12;
    private const int MoveStep = 4;
    private const int ResizeStep = 12;

    private readonly Rectangle _screenBounds;
    private readonly Font _hintFont = new("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Point);

    private Rectangle _selection;
    private Point _dragStartScreen;
    private Point _moveOffset;
    private InteractionMode _interactionMode;

    public SelectionOverlayForm(Rectangle initialSelection)
    {
        _screenBounds = Screen.PrimaryScreen?.Bounds ?? Screen.FromPoint(Cursor.Position).Bounds;
        _selection = ClampToScreen(initialSelection);

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        AutoScaleMode = AutoScaleMode.Dpi;
        Bounds = _screenBounds;
        DoubleBuffered = true;
        FormBorderStyle = FormBorderStyle.None;
        KeyPreview = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.Black;
        Opacity = 0.20d;
        Cursor = Cursors.Cross;
        Text = "Выделение области";
    }

    public event Action<Rectangle>? SelectionCompleted;

    public event Action? SelectionCancelled;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hintFont.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var createParams = base.CreateParams;
            createParams.ExStyle |= 0x80;
            return createParams;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        Focus();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        var point = ToScreen(e.Location);

        if (e.Button == MouseButtons.Left)
        {
            _interactionMode = InteractionMode.Selecting;
            _dragStartScreen = point;
            _selection = Rectangle.Empty;
            Invalidate();
            return;
        }

        if (e.Button == MouseButtons.Right && !_selection.IsEmpty)
        {
            _interactionMode = InteractionMode.Moving;
            _moveOffset = new Point(point.X - _selection.X, point.Y - _selection.Y);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var point = ToScreen(e.Location);

        if (_interactionMode == InteractionMode.Selecting)
        {
            _selection = NormalizeRectangle(_dragStartScreen, point);
            Invalidate();
            return;
        }

        if (_interactionMode == InteractionMode.Moving && !_selection.IsEmpty)
        {
            var movedRectangle = new Rectangle(
                point.X - _moveOffset.X,
                point.Y - _moveOffset.Y,
                _selection.Width,
                _selection.Height);

            _selection = ClampToScreen(movedRectangle);
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (_interactionMode == InteractionMode.Selecting && e.Button == MouseButtons.Left)
        {
            _interactionMode = InteractionMode.None;
            if (IsValidSelection(_selection))
            {
                CompleteSelection();
            }
            else
            {
                _selection = Rectangle.Empty;
                Invalidate();
            }

            return;
        }

        if (_interactionMode == InteractionMode.Moving && e.Button == MouseButtons.Right)
        {
            _interactionMode = InteractionMode.None;
            if (IsValidSelection(_selection))
            {
                CompleteSelection();
            }
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);

        if (_selection.IsEmpty)
        {
            return;
        }

        var delta = e.Delta > 0 ? ResizeStep : -ResizeStep;
        ResizeSelection(delta, delta / 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var instructions = _selection.IsEmpty
            ? "ЛКМ: выделить область. Esc: отмена."
            : "Enter: перевести. ПКМ: двигать. Колесо/стрелки: подправить. Esc: отмена.";

        using var hintBrush = new SolidBrush(Color.FromArgb(220, 255, 255, 255));
        e.Graphics.DrawString(instructions, _hintFont, hintBrush, new PointF(24, 20));

        if (_selection.IsEmpty)
        {
            return;
        }

        var drawRectangle = ToClient(_selection);
        using var fillBrush = new SolidBrush(Color.FromArgb(80, 87, 179, 255));
        using var outlinePen = new Pen(Color.FromArgb(255, 255, 211, 74), 2f)
        {
            DashStyle = DashStyle.Dash,
        };

        e.Graphics.FillRectangle(fillBrush, drawRectangle);
        e.Graphics.DrawRectangle(outlinePen, drawRectangle);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape:
                CancelSelection();
                return true;
            case Keys.Enter:
            case Keys.Space:
                if (IsValidSelection(_selection))
                {
                    CompleteSelection();
                }

                return true;
            case Keys.Left:
                MoveSelection(-MoveStep, 0);
                return true;
            case Keys.Right:
                MoveSelection(MoveStep, 0);
                return true;
            case Keys.Up:
                MoveSelection(0, -MoveStep);
                return true;
            case Keys.Down:
                MoveSelection(0, MoveStep);
                return true;
            case Keys.Shift | Keys.Left:
                ResizeSelection(-ResizeStep, 0);
                return true;
            case Keys.Shift | Keys.Right:
                ResizeSelection(ResizeStep, 0);
                return true;
            case Keys.Shift | Keys.Up:
                ResizeSelection(0, -ResizeStep);
                return true;
            case Keys.Shift | Keys.Down:
                ResizeSelection(0, ResizeStep);
                return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void MoveSelection(int deltaX, int deltaY)
    {
        if (_selection.IsEmpty)
        {
            return;
        }

        _selection = ClampToScreen(new Rectangle(
            _selection.X + deltaX,
            _selection.Y + deltaY,
            _selection.Width,
            _selection.Height));

        Invalidate();
    }

    private void ResizeSelection(int deltaWidth, int deltaHeight)
    {
        if (_selection.IsEmpty)
        {
            return;
        }

        var centered = new Rectangle(
            _selection.X - (deltaWidth / 2),
            _selection.Y - (deltaHeight / 2),
            Math.Max(MinimumSelectionSize, _selection.Width + deltaWidth),
            Math.Max(MinimumSelectionSize, _selection.Height + deltaHeight));

        _selection = ClampToScreen(centered);
        Invalidate();
    }

    private Rectangle ClampToScreen(Rectangle rectangle)
    {
        if (rectangle.IsEmpty)
        {
            return Rectangle.Empty;
        }

        var width = Math.Min(rectangle.Width, _screenBounds.Width);
        var height = Math.Min(rectangle.Height, _screenBounds.Height);
        var x = Math.Max(_screenBounds.Left, Math.Min(rectangle.X, _screenBounds.Right - width));
        var y = Math.Max(_screenBounds.Top, Math.Min(rectangle.Y, _screenBounds.Bottom - height));
        return new Rectangle(x, y, width, height);
    }

    private Rectangle NormalizeRectangle(Point start, Point end)
    {
        var left = Math.Min(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        var right = Math.Max(start.X, end.X);
        var bottom = Math.Max(start.Y, end.Y);
        return ClampToScreen(Rectangle.FromLTRB(left, top, right, bottom));
    }

    private Rectangle ToClient(Rectangle screenRectangle) =>
        new(
            screenRectangle.X - _screenBounds.X,
            screenRectangle.Y - _screenBounds.Y,
            screenRectangle.Width,
            screenRectangle.Height);

    private Point ToScreen(Point clientPoint) =>
        new(clientPoint.X + _screenBounds.X, clientPoint.Y + _screenBounds.Y);

    private static bool IsValidSelection(Rectangle selection) =>
        selection.Width >= MinimumSelectionSize && selection.Height >= MinimumSelectionSize;

    private void CompleteSelection()
    {
        var selection = _selection;
        SelectionCompleted?.Invoke(selection);
        Close();
    }

    private void CancelSelection()
    {
        SelectionCancelled?.Invoke();
        Close();
    }

    private enum InteractionMode
    {
        None,
        Selecting,
        Moving,
    }
}
