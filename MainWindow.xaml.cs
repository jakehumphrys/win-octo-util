using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
 
namespace UsageWidget;
 
public partial class MainWindow : Window
{
    private static readonly Brush Green  = Brush("#3DDC84");
    private static readonly Brush Yellow = Brush("#F2C94C");
    private static readonly Brush Red    = Brush("#EB5757");
 
    public MainWindow()
    {
        InitializeComponent();
 
        // Demo: tick the clock every second. Replace with your real data timer.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Stamp.Text = DateTime.Now.ToString("HH:mm:ss");
        timer.Start();
    }
 
    private void Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
 
    private void Minimise(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    // ▾ / ▴ collapses the window down to just the gas + electricity overview.
    private void ToggleDetails(object sender, RoutedEventArgs e)
    {
        var collapse = Details.Visibility == Visibility.Visible;
        Details.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
        CollapseToggle.Content = collapse ? "▴" : "▾";
        CollapseToggle.ToolTip = collapse ? "Expand" : "Collapse";
    }
 
    private static Brush Brush(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;
}