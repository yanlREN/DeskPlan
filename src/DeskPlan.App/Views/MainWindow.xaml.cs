using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
    private void ResizeDelta(object sender, DragDeltaEventArgs e)
    {
        var b = WindowGeometry.Capture(this, controller.Settings);
        WindowGeometry.Place(this, Host, b with { Width = Math.Max(MinWidth, ActualWidth + e.HorizontalChange), Height = Math.Max(MinHeight, ActualHeight + e.VerticalChange) });
    }
    private void ResizeDone(object sender, DragCompletedEventArgs e) => controller.SaveWindow();
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
