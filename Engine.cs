using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using HidLibrary;

namespace WiiMote_To_XBox360;

public class Engine
{
    private ViGEmClient? _client;
    private IXbox360Controller? _xbox;
    private HidDevice? _wiiDevice;
    private bool _running = false;
    private bool _calibrated = false;

    public event Action<string>? OnLog;
    public event Action<bool>? OnConnectionChanged;
    public event Action<double, bool[], bool[]>? OnInputUpdate;

    public double Sensitivity { get; set; } = 1.0;
    public double Deadzone { get; set; } = 2.0;
    public double SmoothingFactor { get; set; } = 0.08;

    // Kalibrierungs-Punkte
    private double _valTable = 0; // Schritt 1 (Mitte/Tisch)
    private double _valRight = 0; // Schritt 2 (Rechtsanschlag)
    private double _valLeft = 0;  // Schritt 3 (Linksanschlag)

    // Berechnete Faktoren
    private double _scaleRight = 1.0;
    private double _scaleLeft = 1.0;

    private double _currentSmoothedAngle = 0;

    public void Initialize()
    {
        try
        {
            _client = new ViGEmClient();
            _xbox = _client.CreateXbox360Controller();
            _xbox.Connect();
            OnLog?.Invoke("Engine: BEREIT");
        }
        catch (Exception ex) { OnLog?.Invoke($"TREIBER FEHLER: {ex.Message}"); }
    }

    public void StartScanning()
    {
        if (_running) return;
        _running = true;
        Task.Run(ScanLoop);
    }

    // Liest den rohen Sensorwert (Neigung)
    private double GetSensorValue(byte[] data)
    {
        if (data.Length < 6) return 0;
        // Wir nutzen X (Byte 3) und Z (Byte 5) für die Rotation
        double accX = data[3] - 128.0;
        double accZ = data[5] - 128.0;

        // Berechnet den Winkel. 
        // Auf dem Tisch: X~0, Z~30 -> Winkel ~0
        // Hochkant Rechts: X~30, Z~0 -> Winkel ~90
        return Math.Atan2(accX, accZ) * (180.0 / Math.PI);
    }

    // SCHRITT 1: TISCH (NULLPUNKT)
    public void CalibrateStep1_Table()
    {
        if (_wiiDevice != null)
        {
            var report = _wiiDevice.Read();
            if (report.Status == HidDeviceData.ReadStatus.Success)
            {
                _valTable = GetSensorValue(report.Data);
            }
        }
        _currentSmoothedAngle = 0;
        _calibrated = false;
        OnLog?.Invoke($"Schritt 1 (Tisch) gespeichert: {_valTable:F1}°");
    }

    // SCHRITT 2: ZAHLEN RECHTS
    public void CalibrateStep2_Right()
    {
        if (_wiiDevice != null)
        {
            var report = _wiiDevice.Read();
            if (report.Status == HidDeviceData.ReadStatus.Success)
            {
                _valRight = GetSensorValue(report.Data);
            }
        }
        OnLog?.Invoke($"Schritt 2 (Rechts) gespeichert: {_valRight:F1}°");
    }

    // SCHRITT 3: ZAHLEN LINKS & FERTIG
    public void CalibrateStep3_Left()
    {
        if (_wiiDevice != null)
        {
            var report = _wiiDevice.Read();
            if (report.Status == HidDeviceData.ReadStatus.Success)
            {
                _valLeft = GetSensorValue(report.Data);
            }
        }


        double rangeToRight = _valRight - _valTable;
        double rangeToLeft = _valLeft - _valTable;

        // Sicherheits-Check (Division durch 0)
        if (Math.Abs(rangeToRight) < 5) rangeToRight = 90;
        if (Math.Abs(rangeToLeft) < 5) rangeToLeft = -90;

        _scaleRight = 90.0 / rangeToRight;
        _scaleLeft = -90.0 / rangeToLeft;

        _calibrated = true;
        OnLog?.Invoke($"FERTIG! R-Faktor:{_scaleRight:F2} | L-Faktor:{_scaleLeft:F2}");
    }

    private void ScanLoop()
    {
        OnLog?.Invoke("Suche Controller...");
        while (_running)
        {
            if (_wiiDevice == null || !_wiiDevice.IsConnected)
            {
                var dev = HidDevices.Enumerate(0x057e, 0x0306).FirstOrDefault() ??
                          HidDevices.Enumerate(0x057e, 0x0330).FirstOrDefault();

                if (dev != null)
                {
                    _wiiDevice = dev;
                    _wiiDevice.OpenDevice();
                    _wiiDevice.Write(new byte[] { 0x12, 0x00, 0x31 });
                    _wiiDevice.Write(new byte[] { 0x11, 0x10 });

                    OnConnectionChanged?.Invoke(true);
                    OnLog?.Invoke("VERBUNDEN! Starte Wizard.");

                    // Initialwert
                    var r = _wiiDevice.Read();
                    if (r.Status == HidDeviceData.ReadStatus.Success)
                        _valTable = GetSensorValue(r.Data);

                    InputLoop();
                }
            }
            Thread.Sleep(1000);
        }
    }

    private void InputLoop()
    {
        byte lastLedState = 0;
        while (_running && _wiiDevice != null && _wiiDevice.IsConnected)
        {
            var report = _wiiDevice.Read(100);
            if (report.Status != HidDeviceData.ReadStatus.Success) break;

            byte[] data = report.Data;
            if (data.Length < 6) continue;

            byte b1 = data[1];
            byte b2 = data[2];

            // 1. Messen
            double currentRaw = GetSensorValue(data);

            // 2. Differenz zum Tisch-Wert
            double diff = currentRaw - _valTable;

            // Unwrapping (falls Sprung über 180/-180)
            if (diff < -180) diff += 360;
            if (diff > 180) diff -= 360;

            // 3. Skalieren (Je nach Richtung)
            double finalAngle = 0;
            if (diff >= 0) finalAngle = diff * _scaleRight;
            else finalAngle = diff * _scaleLeft;

            // 4. Glätten
            _currentSmoothedAngle = (finalAngle * SmoothingFactor) + (_currentSmoothedAngle * (1.0 - SmoothingFactor));

            // Clamp (Maximalwerte begrenzen)
            if (_currentSmoothedAngle > 135) _currentSmoothedAngle = 135;
            if (_currentSmoothedAngle < -135) _currentSmoothedAngle = -135;

            // UI & Output
            bool[] btns = GetButtonStates(b1, b2);
            bool[] leds = CalculateLeds(_currentSmoothedAngle);
            OnInputUpdate?.Invoke(_currentSmoothedAngle, btns, leds);

            // Hardware LEDs
            byte ledByte = (byte)((leds[0] ? 0x10 : 0) | (leds[1] ? 0x20 : 0) | (leds[2] ? 0x40 : 0) | (leds[3] ? 0x80 : 0));
            if (ledByte != lastLedState)
            {
                _wiiDevice.Write(new byte[] { 0x11, ledByte });
                lastLedState = ledByte;
            }

            // Xbox
            if (_calibrated && _xbox != null)
            {
                double finalSteer = _currentSmoothedAngle;
                // Deadzone
                if (Math.Abs(finalSteer) < Deadzone) finalSteer = 0;
                else finalSteer = finalSteer > 0 ? finalSteer - Deadzone : finalSteer + Deadzone;

                // Output (-1.0 bis 1.0 bei 90 Grad Drehung)
                double steerFactor = (finalSteer * Sensitivity) / 90.0;
                if (steerFactor > 1.0) steerFactor = 1.0;
                if (steerFactor < -1.0) steerFactor = -1.0;

                _xbox.SetAxisValue(Xbox360Axis.LeftThumbX, (short)(steerFactor * 32767));

                // Buttons
                _xbox.SetSliderValue(Xbox360Slider.RightTrigger, (b2 & 0x01) != 0 ? (byte)255 : (byte)0);
                _xbox.SetSliderValue(Xbox360Slider.LeftTrigger, (b2 & 0x02) != 0 ? (byte)255 : (byte)0);
                _xbox.SetButtonState(Xbox360Button.A, (b2 & 0x04) != 0);
                _xbox.SetButtonState(Xbox360Button.Y, (b2 & 0x08) != 0);
                _xbox.SetButtonState(Xbox360Button.Start, (b1 & 0x10) != 0);
                _xbox.SetButtonState(Xbox360Button.Back, (b2 & 0x10) != 0);
                _xbox.SetButtonState(Xbox360Button.Guide, (b2 & 0x80) != 0);
                _xbox.SetButtonState(Xbox360Button.Left, (b1 & 0x08) != 0);
                _xbox.SetButtonState(Xbox360Button.Right, (b1 & 0x04) != 0);
                _xbox.SetButtonState(Xbox360Button.Down, (b1 & 0x01) != 0);
                _xbox.SetButtonState(Xbox360Button.Up, (b1 & 0x02) != 0);

                _xbox.SubmitReport();
            }
        }
        OnConnectionChanged?.Invoke(false);
        if (_wiiDevice != null) _wiiDevice.CloseDevice();
        _wiiDevice = null;
    }

    private bool[] GetButtonStates(byte b1, byte b2)
    {
        bool[] b = new bool[11];
        b[0] = (b2 & 0x08) != 0; b[1] = (b2 & 0x04) != 0; b[2] = (b2 & 0x02) != 0; b[3] = (b2 & 0x01) != 0;
        b[4] = (b1 & 0x10) != 0; b[5] = (b2 & 0x10) != 0; b[6] = (b2 & 0x80) != 0; b[7] = (b1 & 0x01) != 0;
        b[8] = (b1 & 0x02) != 0; b[9] = (b1 & 0x08) != 0; b[10] = (b1 & 0x04) != 0;
        return b;
    }

    private bool[] CalculateLeds(double angle)
    {
        bool[] l = new bool[4];
        double a = Math.Abs(angle);
        l[0] = true;
        if (a > 20) l[1] = true;
        if (a > 45) l[2] = true;
        if (a > 70) l[3] = true;
        return l;
    }

    public void Stop()
    {
        _running = false;
        try { _wiiDevice?.CloseDevice(); } catch { }
        try { _xbox?.Disconnect(); } catch { }
        try { _client?.Dispose(); } catch { }
    }
}