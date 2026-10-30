using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DeskPlan.App.ViewModels;
using DeskPlan.Core;
using DeskPlan.Windows;

namespace DeskPlan.App.Views;

public partial class MainWindow : Window
{
    private readonly AppController controller;
    private NativeMethods.Point dragOrigin;
    private PlannerSettings? dragBounds;
    private Point gripOrigin;
    private ResizeEdges resizeEdges;
    private NativeMethods.Point resizeOrigin;
    private Point resizeThumbOrigin;
    private WindowBounds? resizeStart;
    private WindowBounds resizeWorkArea;
    private double resizeScale;
    public IWindowHost Host { get; set; } = new FloatingWindowHost(false);
    public bool InternalClose { get; set; }
    public MainViewModel Model => controller.ViewModel;
    public double ScrollPosition { get => TaskScroll.VerticalOffset; set => TaskScroll.ScrollToVerticalOffset(value); }
    public MainWindow(AppController controller)
    {
        this.controller = controller;
        InitializeComponent(); SetResourceReference(StyleProperty, typeof(Window)); DataContext = controller.ViewModel;
        Closing += (_, e) =>
        {
            if (InternalClose) return;
            e.Cancel = true;
            if (controller.Settings.CloseToTray) { controller.SaveWindow(); Hide(); }
            else controller.Exit();
        };
        SizeChanged += (_, _) => { if (IsLoaded) controller.ScheduleBoundsSave(); };
        LocationChanged += (_, _) => { if (IsLoaded) controller.ScheduleBoundsSave(); };
    }
    private static bool IsControl(DependencyObject? item)
    {
        while (item is not null) { if (item is Button) return true; item = item is System.Windows.ContentElement content ? ContentOperations.GetParent(content) : VisualTreeHelper.GetParent(item); }
        return false;
    }
    private void HeaderDown(object sender, MouseButtonEventArgs e)
    {
        if (IsControl(e.OriginalSource as DependencyObject)) return;
        controller.Flush(); NativeMethods.GetCursorPos(out dragOrigin);
        dragBounds = WindowGeometry.Capture(this, controller.Settings); Header.CaptureMouse(); e.Handled = true;
    }
    private void HeaderMove(object sender, MouseEventArgs e)
    {
        if (dragBounds is null || e.LeftButton != MouseButtonState.Pressed) return;
        NativeMethods.GetCursorPos(out var point);
        WindowGeometry.Place(this, Host, dragBounds with { Left = dragBounds.Left + point.X - dragOrigin.X, Top = dragBounds.Top + point.Y - dragOrigin.Y });
    }
    private void HeaderUp(object sender, MouseButtonEventArgs e)
    { if (dragBounds is null) return; Header.ReleaseMouseCapture(); dragBounds = null; controller.SaveWindow(); }
    private void ResizeStarted(object sender, DragStartedEventArgs e)
    {
        if (sender is Thumb { Tag: string direction } thumb && Enum.TryParse<ResizeEdges>(direction, out var edges))
        {
            resizeThumbOrigin = new(e.HorizontalOffset, e.VerticalOffset);
            BeginResize(edges, ScreenPoint(thumb.PointToScreen(resizeThumbOrigin)));
        }
        e.Handled = true;
    }
    private void ResizeDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is Thumb thumb)
            ResizeTo(ScreenPoint(thumb.PointToScreen(new(resizeThumbOrigin.X + e.HorizontalChange, resizeThumbOrigin.Y + e.VerticalChange))));
        e.Handled = true;
    }
    private void ResizeDone(object sender, DragCompletedEventArgs e)
    {
        // Thumb reports the final total in screen pixels. Apply it even if move messages were
        // coalesced during a fast drag, and use the event position rather than a later global cursor.
        if (!e.Canceled && resizeStart is not null)
            ResizeTo(new() { X = resizeOrigin.X + (int)Math.Round(e.HorizontalChange), Y = resizeOrigin.Y + (int)Math.Round(e.VerticalChange) });
        EndResize(); e.Handled = true;
    }
    private static NativeMethods.Point ScreenPoint(Point point) => new() { X = (int)Math.Round(point.X), Y = (int)Math.Round(point.Y) };
    // Share the same screen-coordinate path with the isolated window integration harness.
    internal void BeginResize(ResizeEdges edges, NativeMethods.Point point)
    {
        controller.Flush();
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return;
        resizeEdges = edges; resizeOrigin = point; resizeScale = WindowGeometry.Scale(hwnd);
        resizeStart = new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        resizeWorkArea = WindowGeometry.WorkArea(hwnd);
    }
    internal void ResizeTo(NativeMethods.Point point)
    {
        if (resizeStart is not { } start) return;
        // Thumb-relative deltas change when resizing the left/top edge. Use the original screen
        // pointer and rectangle for the whole gesture to avoid feedback, drift and DPI scaling errors.
        var bounds = WindowResize.Calculate(start, resizeEdges, point.X - resizeOrigin.X, point.Y - resizeOrigin.Y,
            MinWidth * resizeScale, MinHeight * resizeScale, resizeWorkArea);
        WindowGeometry.Place(this, Host, controller.Settings with
        { Left = bounds.Left, Top = bounds.Top, Width = bounds.Width / resizeScale, Height = bounds.Height / resizeScale });
    }
    internal void EndResize() { resizeStart = null; controller.SaveWindow(); }
    private void SwitchMode(object sender, RoutedEventArgs e) => controller.SwitchMode();
    private void ReturnToDesktop(object sender, RoutedEventArgs e) => controller.ReturnToDesktop();
    private void OpenReview(object sender, RoutedEventArgs e) => controller.OpenReview();
    private void OpenSettings(object sender, RoutedEventArgs e) => controller.OpenSettings();
    private void OpenOverdue(object sender, RoutedEventArgs e) => controller.OpenOverdue();
    private void CloseWidget(object sender, RoutedEventArgs e) => Close();
    private void Today(object sender, RoutedEventArgs e) => Model.SelectedDate = DateTime.Today;
    private void Tomorrow(object sender, RoutedEventArgs e) => Model.SelectedDate = DateTime.Today.AddDays(1);
    private void AddTask(object sender, RoutedEventArgs e) => controller.EditTask(null, QuickTitle.Text);
    private void EditTask(object sender, RoutedEventArgs e)
    { if ((sender as FrameworkElement)?.DataContext is TaskItemViewModel task) controller.EditTask(task.Id); }
    private void QuickAddKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || string.IsNullOrWhiteSpace(QuickTitle.Text)) return;
        controller.Service.Add(new(QuickTitle.Text, Model.Date)); QuickTitle.Clear(); Model.Refresh(); e.Handled = true;
    }
    private void SliderReleased(object sender, MouseButtonEventArgs e) => Dispatcher.BeginInvoke(() => controller.Flush());
    private void SliderBlurred(object sender, KeyboardFocusChangedEventArgs e) => controller.Flush();
    private void GripDown(object sender, MouseButtonEventArgs e) => gripOrigin = e.GetPosition(this);
    private void GripMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || (e.GetPosition(this) - gripOrigin).Length < 5) return;
        if ((sender as FrameworkElement)?.DataContext is TaskItemViewModel task)
            DragDrop.DoDragDrop((DependencyObject)sender, task.Id.ToString(), DragDropEffects.Move);
    }
    private void TaskDrop(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TaskItemViewModel target && Guid.TryParse(e.Data.GetData(DataFormats.StringFormat) as string, out var id))
        {
            Model.Move(id, target.Id); controller.SetManualSort(); Model.Refresh(); e.Handled = true;
        }
    }
}
