using System.Drawing.Drawing2D;

namespace KwikKonvert.Forms;

/// <summary>
/// The doge: sits still when idle, dances while files are being processed.
/// Drives the GIF itself with <see cref="ImageAnimator"/> (instead of relying on PictureBox's automatic animation,
/// which only starts if the box is already visible at the moment the image is set).
/// </summary>
internal sealed class DogeBox : Control
{
    private readonly Image? _still = Ui.DogeStill();
    private Image? _gif;
    private bool _dancing;
    private bool _animating;

    public DogeBox(int size)
    {
        Size = new Size(size, size);
        Margin = new Padding(3);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    public bool Dancing
    {
        get => _dancing;
        set
        {
            if (_dancing == value) return;
            _dancing = value;
            if (value)
            {
                _gif ??= Ui.DogeGif();
                StartAnimating();
            }
            else
            {
                StopAnimating();
            }
            Invalidate();
        }
    }

    private void StartAnimating()
    {
        if (_animating || _gif is null || !ImageAnimator.CanAnimate(_gif)) return;
        ImageAnimator.Animate(_gif, OnFrameChanged);
        _animating = true;
    }

    private void StopAnimating()
    {
        if (!_animating || _gif is null) return;
        ImageAnimator.StopAnimate(_gif, OnFrameChanged);
        _animating = false;
    }

    /// <summary>Called on the animator's own thread whenever the next frame is due.</summary>
    private void OnFrameChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(new Action(Invalidate)); }
        catch (InvalidOperationException) { /* closing */ }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var image = _dancing && _gif is not null ? _gif : _still;
        if (image is null) return;
        if (image == _gif) ImageAnimator.UpdateFrames(_gif);

        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        e.Graphics.DrawImage(image, ClientRectangle);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopAnimating();
            _gif?.Dispose();
            _still?.Dispose();
        }
        base.Dispose(disposing);
    }
}
