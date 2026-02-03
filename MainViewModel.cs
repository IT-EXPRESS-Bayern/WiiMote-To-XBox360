using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WiiMote_To_XBox360.Services;

namespace WiiMote_To_XBox360.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public readonly WiimoteService _service;

    // --- UI Eigenschaften ---
    [ObservableProperty] private string _logOutput = "System bereit.";
    [ObservableProperty] private string _statusText = "WIIMOTE // SUCHE...";
    [ObservableProperty] private Brush _statusColor = Brushes.Red;
    [ObservableProperty] private bool _isConnected;

    // Einstellungen
    [ObservableProperty] private double _sensitivity = 1.0;
    [ObservableProperty] private double _deadzone = 2.0;
    [ObservableProperty] private double _smoothingSlider = 80;
    [ObservableProperty] private string _smoothText = "Ultra";

    // Visualisierung
    [ObservableProperty] private double _visualAngle;
    [ObservableProperty] private string _visualAngleText = "0°";

    // Kalibrierung Overlay Steuerung
    [ObservableProperty] private Visibility _calibOverlayVis = Visibility.Visible;
    [ObservableProperty] private Visibility _step1Vis = Visibility.Visible;
    [ObservableProperty] private Visibility _step2Vis = Visibility.Collapsed;

    // Farben für die Grafik (Performance optimiert)
    public ObservableCollection<Brush> ButtonBrushes { get; } = new();
    public ObservableCollection<Brush> LedBrushes { get; } = new();

    // Statische Pinsel (spart Speicher)
    private static readonly Brush BrushOn = new SolidColorBrush(Color.FromRgb(0, 209, 255));
    private static readonly Brush BrushOff = new SolidColorBrush(Color.FromRgb(200, 200, 200));
    private static readonly Brush BrushTriggerOn = new SolidColorBrush(Color.FromRgb(255, 50, 50));
    private static readonly Brush BrushTransparent = Brushes.Transparent;
    private static readonly Brush BrushLedOn = new SolidColorBrush(Color.FromRgb(50, 255, 50));
    private static readonly Brush BrushLedOff = new SolidColorBrush(Color.FromRgb(50, 50, 50));

    public MainViewModel()
    {
        BrushOn.Freeze(); BrushOff.Freeze(); BrushTriggerOn.Freeze();
        BrushLedOn.Freeze(); BrushLedOff.Freeze();

        // 11 Buttons initialisieren
        for (int i = 0; i < 11; i++) ButtonBrushes.Add(i == 1 ? BrushTransparent : BrushOff); // Index 1 ist Trigger (B) -> Transparent wenn aus
        // 4 LEDs initialisieren
        for (int i = 0; i < 4; i++) LedBrushes.Add(BrushLedOff);

        _service = new WiimoteService();
        _service.OnLog += msg => Application.Current.Dispatcher.Invoke(() => LogOutput = $"> {msg}\n" + LogOutput);
        _service.OnStateChanged += OnStateChanged;

        _service.Initialize();
        _service.Start();
    }

    private void OnStateChanged(WiimoteState state)
    {
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            IsConnected = state.IsConnected;
            StatusColor = state.IsConnected ? BrushOn : Brushes.Red;
            StatusText = state.IsConnected ? "WIIMOTE // VERBUNDEN" : "WIIMOTE // GETRENNT";

            if (!state.IsConnected) return;

            VisualAngle = state.Angle;
            VisualAngleText = $"{state.Angle:F0}°";

            // Buttons updaten
            // Mapping: 0:A, 1:B(Trigger), 2:1, 3:2, 4:+, 5:-, 6:Home, 7:Left, 8:Right, 9:Up, 10:Down
            for (int i = 0; i < state.Buttons.Length; i++)
            {
                Brush target;
                if (i == 1) // Spezialfall Trigger (B)
                    target = state.Buttons[i] ? BrushTriggerOn : BrushTransparent;
                else
                    target = state.Buttons[i] ? BrushOn : BrushOff;

                if (ButtonBrushes[i] != target) ButtonBrushes[i] = target;
            }

            // LEDs updaten
            for (int i = 0; i < state.Leds.Length; i++)
            {
                var target = state.Leds[i] ? BrushLedOn : BrushLedOff;
                if (LedBrushes[i] != target) LedBrushes[i] = target;
            }
        }, System.Windows.Threading.DispatcherPriority.Render);
    }

    // --- Commands (Buttons im UI) ---

    [RelayCommand]
    private void StepLeft()
    {
        _service.SetLeftLimit();
        Step1Vis = Visibility.Collapsed;
        Step2Vis = Visibility.Visible;
    }

    [RelayCommand]
    private void StepRight()
    {
        _service.SetRightLimitAndCalibrate();
        CalibOverlayVis = Visibility.Collapsed;
    }

    [RelayCommand]
    private void CloseApp()
    {
        _service.Stop();
        Application.Current.Shutdown();
    }

    // --- Slider Logik ---
    partial void OnSensitivityChanged(double value) => _service.Sensitivity = value;
    partial void OnDeadzoneChanged(double value) => _service.Deadzone = value;
    partial void OnSmoothingSliderChanged(double value)
    {
        _service.SmoothingFactor = 0.05 + (value / 100.0) * 0.85;
        if (value < 30) SmoothText = "Smooth";
        else if (value < 70) SmoothText = "Mittel";
        else SmoothText = "Ultra";
    }
}