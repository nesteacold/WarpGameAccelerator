// ============================================================
// Views/DashboardPage.xaml.cs — Code-behind: animations + sparkline
// ============================================================
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using WarpGameAccelerator.Models;
using WarpGameAccelerator.ViewModels;
using WarpGameAccelerator.Services;

namespace WarpGameAccelerator.Views;

public sealed partial class DashboardPage : Page
{
    public DashboardViewModel ViewModel { get; }
    public LocalizationService Loc { get; }

    private readonly Storyboard _idleGlow;
    private readonly Storyboard _connectedPulse;

    public DashboardPage()
    {
        InitializeComponent();

        ViewModel = App.Services.GetRequiredService<DashboardViewModel>();
        Loc       = App.Services.GetRequiredService<LocalizationService>();

        _idleGlow      = (Storyboard)Resources["IdleGlowAnim"];
        _connectedPulse = (Storyboard)Resources["ConnectedPulseAnim"];
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.PropertyChanged += OnPingHistoryChanged;

        if (ViewModel.CurrentState == AppState.Idle || ViewModel.CurrentState == AppState.Error)
        {
            _idleGlow.Begin();
            SetGlowColor(orange: true);
        }
        else if (ViewModel.CurrentState == AppState.Connected)
        {
            _connectedPulse.Begin();
            SetGlowColor(orange: false);
        }

        DrawSparkline();
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.PropertyChanged -= OnPingHistoryChanged;
        
        try { _idleGlow.Stop(); } catch { }
        try { _connectedPulse.Stop(); } catch { }
    }

    private void OnPingHistoryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.PingHistory))
            DrawSparkline();
    }

    // ── Animation state machine ─────────────────────────────

    private void OnViewModelPropertyChanged(object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ViewModel.CurrentState)) return;

        switch (ViewModel.CurrentState)
        {
            case AppState.Idle:
            case AppState.Error:
                _connectedPulse.Stop();
                _idleGlow.Begin();
                // Reset glow color to orange
                SetGlowColor(orange: true);
                break;

            case AppState.Connected:
                _idleGlow.Stop();
                _connectedPulse.Begin();
                SetGlowColor(orange: false);
                break;

            case AppState.Connecting:
            case AppState.Disconnecting:
                _idleGlow.Stop();
                _connectedPulse.Stop();
                break;
        }
    }

    private void SetGlowColor(bool orange)
    {
        // Swap glow fill to cyan when connected
        var color = orange
            ? Microsoft.UI.ColorHelper.FromArgb(60,  246, 150, 30)   // #40F6961E
            : Microsoft.UI.ColorHelper.FromArgb(60,  0,   230, 118); // #3C00E676 (Green Glow)

        if (BoostGlow.Fill is RadialGradientBrush rgb)
            rgb.GradientStops[0].Color = color;

        // Swap button background
        if (orange)
        {
            var brush = new LinearGradientBrush();
            brush.GradientStops.Add(new GradientStop { Color = Microsoft.UI.ColorHelper.FromArgb(255, 246, 150, 30), Offset = 0 });
            brush.GradientStops.Add(new GradientStop { Color = Microsoft.UI.ColorHelper.FromArgb(255, 224, 112, 16), Offset = 1 });
            BoostButton.Background = brush;
        }
        else
        {
            var brush = new LinearGradientBrush();
            brush.GradientStops.Add(new GradientStop { Color = Microsoft.UI.ColorHelper.FromArgb(255, 0, 230, 118), Offset = 0 }); // #00E676
            brush.GradientStops.Add(new GradientStop { Color = Microsoft.UI.ColorHelper.FromArgb(255, 0, 178, 89), Offset = 1 });  // #00B259
            BoostButton.Background = brush;
        }
    }

    // ── Sparkline drawing ────────────────────────────────────

    private void DrawSparkline()
    {
        PingSparkline.Children.Clear();
        var history = ViewModel.PingHistory;

        if (history == null || history.Count < 2) return;

        double canvasW = PingSparkline.ActualWidth;
        double canvasH = PingSparkline.ActualHeight;
        if (canvasW <= 0) canvasW = 340;

        var max = Math.Max(history.Max(), 1L);
        var stepX = canvasW / (history.Count - 1);

        // Background gradient fill (polyline area)
        var fillPoints = new Windows.Foundation.Point[history.Count + 2];
        for (int i = 0; i < history.Count; i++)
        {
            double x = i * stepX;
            double y = canvasH - (history[i] / (double)max) * (canvasH - 8) - 4;
            fillPoints[i] = new Windows.Foundation.Point(x, y);
        }
        fillPoints[history.Count]     = new Windows.Foundation.Point(canvasW, canvasH);
        fillPoints[history.Count + 1] = new Windows.Foundation.Point(0, canvasH);

        var fillPolygon = new Polygon
        {
            Opacity = 0.15,
            Fill    = new SolidColorBrush(
                ViewModel.IsConnected
                    ? Microsoft.UI.ColorHelper.FromArgb(255, 0, 230, 118)
                    : Microsoft.UI.ColorHelper.FromArgb(255, 246, 150, 30))
        };
        foreach (var pt in fillPoints) fillPolygon.Points.Add(pt);
        PingSparkline.Children.Add(fillPolygon);

        // Line
        var polyline = new Polyline
        {
            StrokeThickness = 2,
            StrokeLineJoin  = PenLineJoin.Round,
            Stroke          = new SolidColorBrush(
                ViewModel.IsConnected
                    ? Microsoft.UI.ColorHelper.FromArgb(255, 0, 230, 118)
                    : Microsoft.UI.ColorHelper.FromArgb(255, 246, 150, 30))
        };
        for (int i = 0; i < history.Count; i++)
        {
            double x = i * stepX;
            double y = canvasH - (history[i] / (double)max) * (canvasH - 8) - 4;
            polyline.Points.Add(new Windows.Foundation.Point(x, y));
        }
        PingSparkline.Children.Add(polyline);

        // Dot at last point
        if (history.Count > 0)
        {
            double lastX = (history.Count - 1) * stepX;
            double lastY = canvasH - (history[^1] / (double)max) * (canvasH - 8) - 4;
            var dot = new Ellipse
            {
                Width  = 8, Height = 8,
                Fill   = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 255, 255, 255)),
                Stroke = polyline.Stroke,
                StrokeThickness = 2
            };
            Canvas.SetLeft(dot, lastX - 4);
            Canvas.SetTop(dot,  lastY - 4);
            PingSparkline.Children.Add(dot);
        }
    }

    // ── Navigation ───────────────────────────────────────────

    private void ChangeGameButton_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(ProcessPickerPage));
    }
}
