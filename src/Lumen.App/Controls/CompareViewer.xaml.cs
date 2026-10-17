using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;

namespace Lumen.App.Controls;

/// <summary>
/// 画布控件：缩放、平移、原图/结果对比。
/// - 图片显示尺寸 = 图像像素尺寸 × <see cref="Zoom"/>；
/// - 滚轮（滚动条默认行为）负责平移，Ctrl+滚轮缩放；
/// - 对比模式：结果图叠在原图上，用矩形 Clip 只露出 <see cref="ComparePosition"/> 右侧，
///   相机分隔线可拖动；<see cref="ShowOriginalTemporarily"/> 用于按住 Space 看原图。
/// </summary>
public sealed partial class CompareViewer : UserControl
{
    private bool _isDraggingCompare;
    private bool _isPanning;
    private Point _panStartPoint;
    private double _panStartHorizontalOffset;
    private double _panStartVerticalOffset;
    private const double MinZoom = 0.02;
    private const double MaxZoom = 8.0;

    private bool _fitPending;

    public CompareViewer()
    {
        InitializeComponent();

        // ScrollViewer 会先处理滚轮事件并标记 Handled，这里用 handledEventsToo:true 兜底，
        // 才能实现 Ctrl+滚轮缩放。
        Scroller.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnScrollerPointerWheelChanged), true);

        // 键盘可达：画布聚焦后可用左右键调对比、+/- 缩放、方向键平移。
        KeyDown += OnViewerKeyDown;
    }

    // ───────────── 依赖属性 ─────────────

    public static readonly DependencyProperty OriginalSourceProperty = DependencyProperty.Register(
        nameof(OriginalSource), typeof(ImageSource), typeof(CompareViewer),
        new PropertyMetadata(null, OnImageSourceChanged));

    public static readonly DependencyProperty ResultSourceProperty = DependencyProperty.Register(
        nameof(ResultSource), typeof(ImageSource), typeof(CompareViewer),
        new PropertyMetadata(null, OnImageSourceChanged));

    public static readonly DependencyProperty PixelWidthProperty = DependencyProperty.Register(
        nameof(PixelWidth), typeof(int), typeof(CompareViewer),
        new PropertyMetadata(0, OnImageSizeChanged));

    public static readonly DependencyProperty PixelHeightProperty = DependencyProperty.Register(
        nameof(PixelHeight), typeof(int), typeof(CompareViewer),
        new PropertyMetadata(0, OnImageSizeChanged));

    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
        nameof(Zoom), typeof(double), typeof(CompareViewer),
        new PropertyMetadata(1.0, OnZoomChanged));

    public static readonly DependencyProperty IsCompareModeProperty = DependencyProperty.Register(
        nameof(IsCompareMode), typeof(bool), typeof(CompareViewer),
        new PropertyMetadata(false, OnCompareStateChanged));

    public static readonly DependencyProperty IsShowingOriginalProperty = DependencyProperty.Register(
        nameof(IsShowingOriginal), typeof(bool), typeof(CompareViewer),
        new PropertyMetadata(false, OnCompareStateChanged));

    private static readonly DependencyProperty ComparePositionProperty = DependencyProperty.Register(
        nameof(ComparePosition), typeof(double), typeof(CompareViewer),
        new PropertyMetadata(0.5, OnComparePositionChanged));

    /// <summary>原图预览（<see cref="Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap"/>）。</summary>
    public ImageSource? OriginalSource
    {
        get => (ImageSource?)GetValue(OriginalSourceProperty);
        set => SetValue(OriginalSourceProperty, value);
    }

    /// <summary>处理结果预览。</summary>
    public ImageSource? ResultSource
    {
        get => (ImageSource?)GetValue(ResultSourceProperty);
        set => SetValue(ResultSourceProperty, value);
    }

    /// <summary>原始图片像素宽度（用于计算 100% 显示尺寸）。</summary>
    public int PixelWidth
    {
        get => (int)GetValue(PixelWidthProperty);
        set => SetValue(PixelWidthProperty, value);
    }

    /// <summary>原始图片像素高度。</summary>
    public int PixelHeight
    {
        get => (int)GetValue(PixelHeightProperty);
        set => SetValue(PixelHeightProperty, value);
    }

    /// <summary>1.0 = 100% 像素映射。</summary>
    public double Zoom
    {
        get => (double)GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public bool IsCompareMode
    {
        get => (bool)GetValue(IsCompareModeProperty);
        set => SetValue(IsCompareModeProperty, value);
    }

    /// <summary>按住 Space 时临时只显示原图。</summary>
    public bool IsShowingOriginal
    {
        get => (bool)GetValue(IsShowingOriginalProperty);
        set => SetValue(IsShowingOriginalProperty, value);
    }

    /// <summary>分隔线位置，0=最左，1=最右。</summary>
    public double ComparePosition
    {
        get => (double)GetValue(ComparePositionProperty);
        set => SetValue(ComparePositionProperty, value);
    }

    // ───────────── 公开操作 ─────────────

    /// <summary>按视口等比缩放并居中（“适应窗口”/Ctrl+0/图片载入后自动调用）。</summary>
    public void FitToWindow()
    {
        if (PixelWidth <= 0 || PixelHeight <= 0)
        {
            return;
        }

        double viewportWidth = Scroller.ActualWidth;
        double viewportHeight = Scroller.ActualHeight;
        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            _fitPending = true;
            return;
        }

        const double padding = 32;
        double scale = Math.Min(
            (viewportWidth - padding) / PixelWidth,
            (viewportHeight - padding) / PixelHeight);

        _fitPending = false;
        Zoom = Math.Clamp(scale, MinZoom, MaxZoom);
        DispatcherQueue.TryEnqueue(() => Scroller.ChangeView(0, 0, null, true));
    }

    /// <summary>Space 临时看原图：true 时隐藏结果层与对比分隔线。</summary>
    public void ShowOriginalTemporarily(bool show) => IsShowingOriginal = show;

    // ───────────── 依赖属性回调 ─────────────

    private static void OnImageSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var viewer = (CompareViewer)d;
        viewer.UpdateImageSources();
        viewer._fitPending = true;
        viewer.DispatcherQueue.TryEnqueue(viewer.FitIfPending);
    }

    private static void OnImageSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var viewer = (CompareViewer)d;
        viewer.UpdateLayoutSizes();
        viewer._fitPending = true;
        viewer.DispatcherQueue.TryEnqueue(viewer.FitIfPending);
    }

    private static void OnZoomChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((CompareViewer)d).UpdateLayoutSizes();

    private static void OnCompareStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((CompareViewer)d).UpdateCompareVisuals();

    private static void OnComparePositionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((CompareViewer)d).UpdateCompareLayout();

    // ───────────── 布局 ─────────────

    private void FitIfPending()
    {
        if (_fitPending)
        {
            FitToWindow();
        }
    }

    private void UpdateImageSources()
    {
        OriginalImageElement.Source = OriginalSource;
        ResultImageElement.Source = ResultSource;
        UpdateCompareVisuals();
    }

    private void UpdateLayoutSizes()
    {
        if (PixelWidth <= 0 || PixelHeight <= 0)
        {
            ImageHost.Width = 0;
            ImageHost.Height = 0;
            ScrollContent.Width = 0;
            ScrollContent.Height = 0;
            return;
        }

        double width = Math.Max(1, PixelWidth * Zoom);
        double height = Math.Max(1, PixelHeight * Zoom);
        ImageHost.Width = width;
        ImageHost.Height = height;

        // 内容至少和视口一样大，配合 ImageHost 的居中实现“小图居中、大图可滚动”。
        ScrollContent.Width = Math.Max(width, Scroller.ActualWidth);
        ScrollContent.Height = Math.Max(height, Scroller.ActualHeight);

        UpdateCompareLayout();
    }

    private void UpdateCompareVisuals()
    {
        bool hasResult = ResultSource is not null;
        bool showResult = hasResult && !IsShowingOriginal;
        bool showCompareOverlay = IsCompareMode && showResult;

        ResultImageElement.Visibility = showResult ? Visibility.Visible : Visibility.Collapsed;
        CompareOverlay.Visibility = showCompareOverlay ? Visibility.Visible : Visibility.Collapsed;

        if (!showCompareOverlay)
        {
            ResultImageElement.Clip = null;
        }

        UpdateCompareLayout();
    }

    private void UpdateCompareLayout()
    {
        double width = ImageHost.Width;
        double height = ImageHost.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        double divider = Math.Clamp(ComparePosition, 0.0, 1.0) * width;

        DividerLine.Height = height;
        Canvas.SetLeft(DividerLine, Math.Max(0, divider - (DividerLine.Width / 2)));
        Canvas.SetTop(DividerLine, 0);

        Canvas.SetLeft(DividerHandle, divider - (DividerHandle.Width / 2));
        Canvas.SetTop(DividerHandle, (height - DividerHandle.Height) / 2);

        if (IsCompareMode && ResultImageElement.Visibility == Visibility.Visible)
        {
            if (ResultImageElement.Clip is not RectangleGeometry clip)
            {
                clip = new RectangleGeometry();
                ResultImageElement.Clip = clip;
            }

            clip.Rect = new Rect(divider, 0, Math.Max(0, width - divider), height);
        }
    }

    // ───────────── 输入 ─────────────

    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateLayoutSizes();
        FitIfPending();
    }

    private void OnImageHostPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // 点画布即把键盘焦点交给画布（Space/方向键等快捷键的后续操作对象）。
        Focus(FocusState.Pointer);

        var point = e.GetCurrentPoint(ImageHost);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (IsCompareMode && ResultSource is not null)
        {
            // 对比模式：在图片上按下即开始拖动分隔线。
            _isDraggingCompare = true;
            ImageHost.CapturePointer(e.Pointer);
            ComparePosition = Math.Clamp(point.Position.X / Math.Max(1.0, ImageHost.Width), 0.0, 1.0);
            e.Handled = true;
            return;
        }

        // 普通模式：拖动平移（ScrollViewer 的滚动位置反向跟随指针）。
        _isPanning = true;
        _panStartPoint = e.GetCurrentPoint(Scroller).Position;
        _panStartHorizontalOffset = Scroller.HorizontalOffset;
        _panStartVerticalOffset = Scroller.VerticalOffset;
        ImageHost.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnImageHostPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isDraggingCompare)
        {
            var point = e.GetCurrentPoint(ImageHost);
            ComparePosition = Math.Clamp(point.Position.X / Math.Max(1.0, ImageHost.Width), 0.0, 1.0);
            e.Handled = true;
            return;
        }

        if (_isPanning)
        {
            var point = e.GetCurrentPoint(Scroller).Position;
            double horizontal = _panStartHorizontalOffset - (point.X - _panStartPoint.X);
            double vertical = _panStartVerticalOffset - (point.Y - _panStartPoint.Y);
            Scroller.ChangeView(horizontal, vertical, null, true);
            e.Handled = true;
        }
    }

    private void OnImageHostPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingCompare && !_isPanning)
        {
            return;
        }

        _isDraggingCompare = false;
        _isPanning = false;
        ImageHost.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    /// <summary>Ctrl+滚轮：以指针所在图像位置为中心缩放（缩放后校正滚动偏移，避免画面跳动）。</summary>
    private void OnScrollerPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if ((e.KeyModifiers & VirtualKeyModifiers.Control) == 0)
        {
            return;
        }

        var point = e.GetCurrentPoint(Scroller);
        double factor = point.Properties.MouseWheelDelta > 0 ? 1.1 : 1.0 / 1.1;
        double oldZoom = Zoom;
        double newZoom = Math.Clamp(oldZoom * factor, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - oldZoom) < 0.0001)
        {
            e.Handled = true;
            return;
        }

        double hostLeft = Math.Max(0, (ScrollContent.Width - ImageHost.Width) / 2);
        double hostTop = Math.Max(0, (ScrollContent.Height - ImageHost.Height) / 2);
        double imageX = (Scroller.HorizontalOffset + point.Position.X - hostLeft) / oldZoom;
        double imageY = (Scroller.VerticalOffset + point.Position.Y - hostTop) / oldZoom;
        Point pointerPosition = point.Position;

        Zoom = newZoom;
        DispatcherQueue.TryEnqueue(() =>
        {
            double newHostLeft = Math.Max(0, (ScrollContent.Width - ImageHost.Width) / 2);
            double newHostTop = Math.Max(0, (ScrollContent.Height - ImageHost.Height) / 2);
            Scroller.ChangeView(
                newHostLeft + (imageX * newZoom) - pointerPosition.X,
                newHostTop + (imageY * newZoom) - pointerPosition.Y,
                null,
                true);
        });

        e.Handled = true;
    }

    /// <summary>键盘操作：左右键调对比分隔线（对比模式）或水平平移；方向键平移；+/- 缩放。</summary>
    private void OnViewerKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (PixelWidth <= 0)
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Left:
                if (IsCompareMode)
                {
                    ComparePosition = Math.Clamp(ComparePosition - 0.02, 0.0, 1.0);
                }
                else
                {
                    Pan(-48, 0);
                }

                e.Handled = true;
                break;
            case VirtualKey.Right:
                if (IsCompareMode)
                {
                    ComparePosition = Math.Clamp(ComparePosition + 0.02, 0.0, 1.0);
                }
                else
                {
                    Pan(48, 0);
                }

                e.Handled = true;
                break;
            case VirtualKey.Up:
                Pan(0, -48);
                e.Handled = true;
                break;
            case VirtualKey.Down:
                Pan(0, 48);
                e.Handled = true;
                break;
            case VirtualKey.Add:
            case (VirtualKey)0xBB:
                SetZoom(Zoom * 1.25);
                e.Handled = true;
                break;
            case VirtualKey.Subtract:
            case (VirtualKey)0xBD:
                SetZoom(Zoom / 1.25);
                e.Handled = true;
                break;
        }
    }

    private void Pan(double horizontalDelta, double verticalDelta) =>
        Scroller.ChangeView(
            Scroller.HorizontalOffset + horizontalDelta,
            Scroller.VerticalOffset + verticalDelta,
            null,
            true);

    private void SetZoom(double value) => Zoom = Math.Clamp(value, MinZoom, MaxZoom);
}
