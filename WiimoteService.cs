using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HidLibrary;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace WiiMote_To_XBox360.Services;

// Ein Daten-Paket, um alles auf einmal an die UI zu schicken
public record WiimoteState(double Angle, bool[] Buttons, bool[] Leds, bool IsConnected);

public class WiimoteService : IDisposable
{
    private const int VID_NINTENDO = 0x057e;
    private const int PID_WIIMOTE_1 = 0x0306;
    private const int PID_WIIMOTE_2 = 0x0330;

    private ViGEmClient? _client;
    private IXbox360Controller? _xbox;
    private HidDevice? _wiiDevice;
    private CancellationTokenSource? _cts;

    // Events für Log und Updates
    public event Action<string>? OnLog;
    public event Action<WiimoteState>? OnStateChanged;

    // Einstellungen
    public double Sensitivity { get; set; } = 1.0;
    public double Deadzone { get; set; } = 2.0;
    public double SmoothingFactor { get; set; } = 0.5;

    // Interne Berechnung
    private double _angleLeft;
    private double _steeringCenter;
    private bool _isCalibrated;
    private double _currentSmoothedAngle;
    private byte _lastLedState;

    public void Initialize()
    {
        try
        {
            _client = new ViGEmClient();
            _xbox = _client.CreateXbox360Controller();
            _xbox.AutoSubmitReport = false;
            _xbox.Connect();
            Log("Treiber bereit (ViGEm).");
        }
        catch (Exception ex)
        {
            Log($"FEHLER Treiber Init: {ex.Message}");
        }
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        // Starte den Loop im Hintergrund
        Task.Factory.StartNew(() => Loop(_cts.Token), TaskCreationOptions.LongRunning);
    }

    public void Stop()
    {
        _cts?.Cancel();
        Dispose();
    }

    // --- Kalibrierung ---
    public void SetLeftLimit()
    {
        if (TryReadRawAngle(out double angle))
        {
            _angleLeft = angle;
            Log($"Links gespeichert: {angle:F1}°");
        }
    }

    public void SetRightLimitAndCalibrate()
    {
        if (TryReadRawAngle(out double angleRight))
        {
            // Automatische Center-Berechnung
            double diff = angleRight - _angleLeft;
            if (diff > 180) diff -= 360;
            if (diff < -180) diff += 360;

            _steeringCenter = NormalizeAngle(_angleLeft + (diff / 2.0));
            _isCalibrated = true;
            Log($"Kalibrierung fertig! Center bei: {_steeringCenter:F1}°");
        }
    }

    // --- Hauptschleife ---
    private async Task Loop(CancellationToken token)
    {
        Log("Suche Wiimote...");

        while (!token.IsCancellationRequested)
        {
            // 1. Verbindungsaufbau
            if (_wiiDevice == null || !_wiiDevice.IsConnected)
            {
                var dev = HidDevices.Enumerate(VID_NINTENDO, PID_WIIMOTE_1).FirstOrDefault() ??
                          HidDevices.Enumerate(VID_NINTENDO, PID_WIIMOTE_2).FirstOrDefault();

                if (dev != null)
                {
                    try
                    {
                        _wiiDevice = dev;
                        _wiiDevice.OpenDevice();
                        _wiiDevice.Write(new byte[] { 0x12, 0x00, 0x31 }); // Reporting Mode
                        Log("Wiimote verbunden.");
                    }
                    catch { _wiiDevice = null; }
                }
                else
                {
                    // Update UI: Nicht verbunden
                    OnStateChanged?.Invoke(new WiimoteState(0, new bool[11], new bool[4], false));
                    await Task.Delay(1000, token);
                    continue;
                }
            }

            // 2. Daten lesen (High Performance Loop)
            while (!token.IsCancellationRequested && _wiiDevice != null && _wiiDevice.IsConnected)
            {
                var report = _wiiDevice.Read(20); // 20ms Timeout
                if (report.Status == HidDeviceData.ReadStatus.Success)
                {
                    ProcessData(report.Data);
                }
                else if (report.Status != HidDeviceData.ReadStatus.WaitTimedOut)
                {
                    break; // Verbindung verloren
                }
            }

            _wiiDevice?.CloseDevice();
            _wiiDevice = null;
            Log("Wiimote getrennt.");
        }
    }

    private void ProcessData(byte[] data)
    {
        if (data.Length < 6) return;

        // 1. Winkel berechnen
        double raw = CalculateRawAngle(data);
        double steer = NormalizeAngle(raw - _steeringCenter);

        // 2. Glätten
        _currentSmoothedAngle = (steer * SmoothingFactor) + (_currentSmoothedAngle * (1.0 - SmoothingFactor));

        // 3. Buttons & LEDs vorbereiten
        var btns = GetButtons(data[1], data[2]);
        var leds = CalculateLeds(_currentSmoothedAngle);

        // 4. UI Update senden
        OnStateChanged?.Invoke(new WiimoteState(_currentSmoothedAngle, btns, leds, true));

        // 5. Hardware Update (Wiimote LEDs)
        UpdateHardwareLeds(leds);

        // 6. Xbox Emulation
        if (_isCalibrated && _xbox != null)
        {
            MapToXbox(_currentSmoothedAngle, data[1], data[2]);
        }
    }

    private void MapToXbox(double angle, byte b1, byte b2)
    {
        // Deadzone
        double val = angle;
        if (Math.Abs(val) < Deadzone) val = 0;
        else val = (val > 0) ? val - Deadzone : val + Deadzone;

        // Range (Wir nehmen an 90° ist Vollausschlag falls Kalibrierung spinnt)
        double range = Math.Abs(NormalizeAngle(_steeringCenter - _angleLeft));
        if (range < 10) range = 90;

        double factor = Math.Clamp((val / range) * Sensitivity, -1.0, 1.0);

        _xbox!.SetAxisValue(Xbox360Axis.LeftThumbX, (short)(-factor * 32767));

        // Button Mapping
        _xbox.SetButtonState(Xbox360Button.A, (b2 & 0x04) != 0); // A
        _xbox.SetSliderValue(Xbox360Slider.RightTrigger, (b2 & 0x01) != 0 ? (byte)255 : (byte)0); // B
        _xbox.SetSliderValue(Xbox360Slider.LeftTrigger, (b2 & 0x02) != 0 ? (byte)255 : (byte)0); // 1
        _xbox.SetButtonState(Xbox360Button.Y, (b2 & 0x08) != 0); // 2
        _xbox.SetButtonState(Xbox360Button.Start, (b1 & 0x10) != 0); // +
        _xbox.SetButtonState(Xbox360Button.Back, (b2 & 0x10) != 0); // -
        _xbox.SetButtonState(Xbox360Button.Guide, (b2 & 0x80) != 0); // Home

        // D-Pad
        _xbox.SetButtonState(Xbox360Button.Left, (b1 & 0x08) != 0);
        _xbox.SetButtonState(Xbox360Button.Right, (b1 & 0x04) != 0);
        _xbox.SetButtonState(Xbox360Button.Up, (b1 & 0x02) != 0);
        _xbox.SetButtonState(Xbox360Button.Down, (b1 & 0x01) != 0);

        _xbox.SubmitReport();
    }

    private void UpdateHardwareLeds(bool[] leds)
    {
        byte ledByte = (byte)((leds[0] ? 0x10 : 0) | (leds[1] ? 0x20 : 0) | (leds[2] ? 0x40 : 0) | (leds[3] ? 0x80 : 0));
        if (ledByte != _lastLedState && _wiiDevice != null)
        {
            try { _wiiDevice.Write(new byte[] { 0x11, ledByte }); } catch { }
            _lastLedState = ledByte;
        }
    }

    // Hilfsfunktionen
    private bool TryReadRawAngle(out double angle)
    {
        angle = 0;
        if (_wiiDevice?.Read() is { Status: HidDeviceData.ReadStatus.Success } report && report.Data.Length >= 5)
        {
            angle = CalculateRawAngle(report.Data);
            return true;
        }
        return false;
    }

    private static double CalculateRawAngle(byte[] data)
    {
        double x = data[3] - 128.0;
        double y = data[4] - 128.0;
        return Math.Atan2(y, x) * (180.0 / Math.PI);
    }

    private static double NormalizeAngle(double angle)
    {
        while (angle <= -180) angle += 360;
        while (angle > 180) angle -= 360;
        return angle;
    }

    private static bool[] GetButtons(byte b1, byte b2) => new[] {
        (b2 & 0x08) != 0, (b2 & 0x04) != 0, (b2 & 0x02) != 0, (b2 & 0x01) != 0, // 0-3: A, B, 1, 2
        (b1 & 0x10) != 0, (b2 & 0x10) != 0, (b2 & 0x80) != 0,                   // 4-6: +, -, Home
        (b1 & 0x08) != 0, (b1 & 0x04) != 0, (b1 & 0x02) != 0, (b1 & 0x01) != 0  // 7-10: Left, Right, Up, Down (Achtung Reihenfolge UI)
    };

    private static bool[] CalculateLeds(double angle)
    {
        double a = Math.Abs(angle);
        return new[] { true, a > 15, a > 45, a > 80 };
    }

    private void Log(string msg) => OnLog?.Invoke(msg);

    public void Dispose()
    {
        _cts?.Cancel();
        _wiiDevice?.CloseDevice();
        if (_xbox != null) { _xbox.Disconnect(); _client?.Dispose(); }
        GC.SuppressFinalize(this);
    }
}