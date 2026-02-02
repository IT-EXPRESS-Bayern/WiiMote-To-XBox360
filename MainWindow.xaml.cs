using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Controls;

namespace WiiMote_To_XBox360;

public partial class MainWindow : Window
{
    private Engine? _engine;

    // Farben definieren
    private SolidColorBrush _colorOn = new SolidColorBrush(Color.FromRgb(0, 209, 255));
    private SolidColorBrush _colorOff = new SolidColorBrush(Color.FromRgb(255, 255, 255));
    private SolidColorBrush _colorPathOff = new SolidColorBrush(Color.FromRgb(255, 255, 255));
    private SolidColorBrush _colorLedOn = new SolidColorBrush(Color.FromRgb(50, 255, 50));
    private SolidColorBrush _colorLedOff = new SolidColorBrush(Color.FromRgb(200, 200, 200));

    public MainWindow()
    {
        InitializeComponent();
        _engine = new Engine();
        _engine.OnLog += Log;
        _engine.OnConnectionChanged += OnConnChange;
        _engine.OnInputUpdate += OnInputUpdate;

        UpdateSliderTexts();
        ApplySettings();

        _engine.StartScanning();
    }

    // --- WIZARD SCHRITTE ---

    // 1. TISCH -> WEITER
    private void BtnStep1_Click(object sender, RoutedEventArgs e)
    {
        _engine?.CalibrateStep1_Table();

        Step1Grid.Visibility = Visibility.Collapsed;
        BtnStep1.Visibility = Visibility.Collapsed;

        Step2Grid.Visibility = Visibility.Visible;
        BtnStep2.Visibility = Visibility.Visible;
    }

    // 2. RECHTS -> WEITER
    private void BtnStep2_Click(object sender, RoutedEventArgs e)
    {
        _engine?.CalibrateStep2_Right();

        Step2Grid.Visibility = Visibility.Collapsed;
        BtnStep2.Visibility = Visibility.Collapsed;

        Step3Grid.Visibility = Visibility.Visible;
        BtnStep3.Visibility = Visibility.Visible;
    }

    // 3. LINKS -> ENDE
    private void BtnStep3_Click(object sender, RoutedEventArgs e)
    {
        _engine?.CalibrateStep3_Left();

        CalibrationOverlay.Visibility = Visibility.Collapsed;
        ControlPanel.Opacity = 1.0;
        ControlPanel.IsEnabled = true;
        TitleText.Text = "WIIMOTE PRO // AKTIV";
    }

    // --- RESTLICHER CODE ---

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        _engine?.Stop();
        Application.Current.Shutdown();
    }

    private void ApplySettings()
    {
        if (_engine == null) return;
        _engine.Sensitivity = SliderSens.Value;
        _engine.Deadzone = SliderDead.Value;
        double val = SliderSmooth.Value;
        double factor = 0.5 - (val * 0.0048);
        if (factor < 0.01) factor = 0.01;
        _engine.SmoothingFactor = factor;
    }

    private void SliderSens_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { ApplySettings(); UpdateSliderTexts(); }
    private void SliderDead_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { ApplySettings(); UpdateSliderTexts(); }
    private void SliderSmooth_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { ApplySettings(); UpdateSliderTexts(); }

    private void UpdateSliderTexts()
    {
        if (TxtSens != null) TxtSens.Text = $"{SliderSens.Value:F1}x";
        if (TxtDead != null) TxtDead.Text = $"{SliderDead.Value:F0}°";
        if (TxtSmooth != null) { double v = SliderSmooth.Value; if (v < 20) TxtSmooth.Text = "Niedrig"; else if (v < 60) TxtSmooth.Text = "Mittel"; else TxtSmooth.Text = "Hoch"; }
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left) this.DragMove(); }

    private void Log(string msg) { Dispatcher.Invoke(() => { LogText.Text = $"> {msg}\n" + LogText.Text; }); }

    private void OnConnChange(bool connected)
    {
        Dispatcher.Invoke(() => {
            if (connected)
            {
                if (CalibrationOverlay.Visibility == Visibility.Visible) Log("Verbunden. Starte Wizard.");
                StatusDot.Fill = _colorOn;
            }
            else StatusDot.Fill = Brushes.Red;
        });
    }

    private void OnInputUpdate(double angle, bool[] btns, bool[] leds)
    {
        Dispatcher.Invoke(() => {
            WiimoteRotation.Angle = angle;
            TxtAngle.Text = $"{angle:F0}°";

            UpdateTemplateElement<Shape>("BtnA", s => HighlightShape(s, btns[0]));
            UpdateTemplateElement<Path>("BtnB", p => HighlightBTrigger(p, btns[1]));
            UpdateTemplateElement<Shape>("Btn1", s => HighlightShape(s, btns[2]));
            UpdateTemplateElement<Shape>("Btn2", s => HighlightShape(s, btns[3]));
            UpdateTemplateElement<Shape>("BtnPlus", s => HighlightShape(s, btns[4]));
            UpdateTemplateElement<Shape>("BtnMinus", s => HighlightShape(s, btns[5]));
            UpdateTemplateElement<Shape>("BtnHome", s => HighlightShape(s, btns[6]));
            UpdateTemplateElement<Path>("BtnDL", p => HighlightPath(p, btns[7]));
            UpdateTemplateElement<Path>("BtnDR", p => HighlightPath(p, btns[8]));
            UpdateTemplateElement<Path>("BtnDU", p => HighlightPath(p, btns[9]));
            UpdateTemplateElement<Path>("BtnDD", p => HighlightPath(p, btns[10]));
            UpdateTemplateElement<Rectangle>("Led1", r => r.Fill = leds[0] ? _colorLedOn : _colorLedOff);
            UpdateTemplateElement<Rectangle>("Led2", r => r.Fill = leds[1] ? _colorLedOn : _colorLedOff);
            UpdateTemplateElement<Rectangle>("Led3", r => r.Fill = leds[2] ? _colorLedOn : _colorLedOff);
            UpdateTemplateElement<Rectangle>("Led4", r => r.Fill = leds[3] ? _colorLedOn : _colorLedOff);
        });
    }

    private void UpdateTemplateElement<T>(string name, Action<T> action) where T : FrameworkElement
    {
        var element = WiimoteLive.Template.FindName(name, WiimoteLive) as T;
        if (element != null) action(element);
    }

    private void HighlightShape(Shape shape, bool active) => shape.Fill = active ? _colorOn : _colorOff;
    private void HighlightPath(Path path, bool active) => path.Fill = active ? _colorOn : _colorPathOff;
    private void HighlightBTrigger(Path path, bool active) => path.Opacity = active ? 0.3 : 0.0;
}