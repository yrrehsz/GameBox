using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace GameStartMenu
{
    public enum ControllerType
    {
        None,
        Xbox,
        PlayStation,
        Nintendo,
        Generic
    }

    public class ControllerInfo
    {
        public string UniqueId { get; set; } = ""; // ID stabile basato su VID/PID (uguale sia cablato che bluetooth)
        public int PlayerIndex { get; set; } // 1, 2, 3 o 4
        public uint XInputSlot { get; set; } = 0; // Slot XInput reale associato
        public ControllerType Type { get; set; } = ControllerType.None;
        public string Name { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public string IconImageSource { get; set; } = "";
        public string BadgeColor { get; set; } = "#22FFFFFF";

        // Colori dei 4 LED stile Wii
        public string Led1 => PlayerIndex == 1 ? "#00E5FF" : "#33FFFFFF";
        public string Led2 => PlayerIndex == 2 ? "#00E5FF" : "#33FFFFFF";
        public string Led3 => PlayerIndex == 3 ? "#00E5FF" : "#33FFFFFF";
        public string Led4 => PlayerIndex == 4 ? "#00E5FF" : "#33FFFFFF";
    }

    public class ControllerDetector
    {
        private readonly System.Timers.Timer _pollTimer;
        private string _lastStateSignature = "INIT";
        private readonly Dictionary<string, string> _customIcons;

        public event Action<List<ControllerInfo>>? OnControllersChanged;

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_VIBRATION
        {
            public ushort wLeftMotorSpeed;
            public ushort wRightMotorSpeed;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern uint XInputGetState(uint dwUserIndex, out IntPtr pState);

        [DllImport("xinput1_4.dll", EntryPoint = "XInputSetState")]
        private static extern uint XInputSetState(uint dwUserIndex, ref XINPUT_VIBRATION pVibration);

        [DllImport("xinput1_4.dll", EntryPoint = "#103")]
        private static extern uint XInputPowerOffController(uint dwUserIndex);

        public ControllerDetector(Dictionary<string, string>? customIcons = null)
        {
            _customIcons = customIcons ?? new Dictionary<string, string>();
            _pollTimer = new System.Timers.Timer(2000);
            _pollTimer.Elapsed += (s, e) => CheckControllers();
        }

        public void Start()
        {
            CheckControllers();
            _pollTimer.Start();
        }

        public void SetControllerIcon(string uniqueId, string iconPackUri)
        {
            _customIcons[uniqueId] = iconPackUri;
            _lastStateSignature = "";
            CheckControllers();
        }

        private void CheckControllers()
        {
            var controllers = DetectConnectedControllers();

            string signature = string.Join("|", controllers.Select(c => $"{c.PlayerIndex}:{c.UniqueId}"));
            if (signature != _lastStateSignature)
            {
                _lastStateSignature = signature;
                OnControllersChanged?.Invoke(controllers);
            }
        }

        // VIBRAZIONE STABILE DIRETTAMENTE SULLO SLOT XINPUT CORRETTO
        public async Task IdentifyControllerAsync(ControllerInfo controller)
        {
            uint slot = controller.XInputSlot;

            var zero = new XINPUT_VIBRATION { wLeftMotorSpeed = 0, wRightMotorSpeed = 0 };
            var heavyPulse = new XINPUT_VIBRATION { wLeftMotorSpeed = 55000, wRightMotorSpeed = 30000 };
            var lightPulse = new XINPUT_VIBRATION { wLeftMotorSpeed = 25000, wRightMotorSpeed = 60000 };

            try
            {
                XInputSetState(slot, ref zero);
                await Task.Delay(25);

                XInputSetState(slot, ref heavyPulse);
                await Task.Delay(180);

                XInputSetState(slot, ref zero);
                await Task.Delay(90);

                XInputSetState(slot, ref lightPulse);
                await Task.Delay(200);

                XInputSetState(slot, ref zero);
            }
            catch { }
        }

        public void TurnOffController(ControllerInfo controller)
        {
            try
            {
                XInputPowerOffController(controller.XInputSlot);
            }
            catch { }
        }

        public List<ControllerInfo> DetectConnectedControllers()
        {
            var rawList = new List<ControllerInfo>();
            var seenHardwareIds = new HashSet<string>();

            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, DeviceID FROM Win32_PnPEntity WHERE " +
                    "DeviceID LIKE '%VID_054C%' OR " + // Sony
                    "DeviceID LIKE '%VID_045E%' OR " + // Microsoft
                    "DeviceID LIKE '%VID_057E%' OR " + // Nintendo
                    "DeviceID LIKE '%054C%' OR " +     // Bluetooth Sony
                    "DeviceID LIKE '%045E%'");         // Bluetooth Microsoft

                foreach (ManagementObject device in searcher.Get())
                {
                    string rawDevId = device["DeviceID"]?.ToString() ?? "";
                    string devId = rawDevId.ToUpperInvariant();
                    string name = (device["Name"]?.ToString() ?? "").ToLowerInvariant();

                    if (!devId.StartsWith("HID\\"))
                        continue;

                    if (devId.Contains("&COL") && !devId.Contains("&COL01"))
                        continue;

                    ControllerInfo? info = null;
                    string uniqueKey = "";
                    string defaultIcon = "";

                    // Estrae il VID e PID esatto (es. VID_045E&PID_0B12) così è IDENTICO sia via cavo che via Bluetooth!
                    //string vidPid = ExtractVidPid(devId);

                    // 1. SONY PLAYSTATION
                    if (devId.Contains("054C"))
                    {
                        string padKey = ExtractUniquePadKey(devId, rawDevId);
                        uniqueKey = "PS_" + padKey;
                        defaultIcon = "pack://application:,,,/assets/controllers/dualsense/regular/white.png";

                        info = new ControllerInfo
                        {
                            UniqueId = uniqueKey,
                            Type = ControllerType.PlayStation,
                            Name = devId.Contains("0CE6") || devId.Contains("0DF2") ? "DualSense" : "DualShock",
                            DeviceId = rawDevId,
                            BadgeColor = "#440070D1"
                        };
                    }
                    // 2. NINTENDO SWITCH
                    else if (devId.Contains("057E"))
                    {
                        string padKey = ExtractUniquePadKey(devId, rawDevId);
                        uniqueKey = "NSW_" + padKey;
                        defaultIcon = "pack://application:,,,/assets/controllers/switch/regular/red.png";

                        info = new ControllerInfo
                        {
                            UniqueId = uniqueKey,
                            Type = ControllerType.Nintendo,
                            Name = "Switch",
                            DeviceId = rawDevId,
                            BadgeColor = "#44E60012"
                        };
                    }
                    // 3. MICROSOFT XBOX
                    else if (devId.Contains("045E") && (devId.Contains("IG_") || name.Contains("xbox") || name.Contains("controller") || name.Contains("gamepad")))
                    {
                        string padKey = ExtractUniquePadKey(devId, rawDevId);
                        uniqueKey = "XBOX_" + padKey;
                        defaultIcon = "pack://application:,,,/assets/controllers/xbox/regular/black.png";

                        info = new ControllerInfo
                        {
                            UniqueId = uniqueKey,
                            Type = ControllerType.Xbox,
                            Name = "Xbox Controller",
                            DeviceId = rawDevId,
                            BadgeColor = "#44107C10"
                        };
                    }

                    if (info != null)
                    {
                        if (seenHardwareIds.Contains(uniqueKey))
                            continue;

                        seenHardwareIds.Add(uniqueKey);

                        info.IconImageSource = _customIcons.TryGetValue(uniqueKey, out string? customIcon) && !string.IsNullOrEmpty(customIcon)
                            ? customIcon
                            : defaultIcon;

                        rawList.Add(info);
                        if (rawList.Count == 4) break;
                    }
                }
            }
            catch { }

            // ORDINAMENTO STABILE: Ordina i pad in base all'ID univoco per evitare che si scambino di posto casualmente ad ogni ciclo!
            var sortedList = rawList.OrderBy(c => c.UniqueId).ToList();

            // Assegna indici di giocatore e mappa i reali slot XInput attivi (0 a 3)
            for (int i = 0; i < sortedList.Count; i++)
            {
                sortedList[i].PlayerIndex = i + 1;
                
                // Cerca quale slot XInput risponde a questo controller
                for (uint slot = 0; slot < 4; slot++)
                {
                    if (XInputGetState(slot, out _) == 0)
                    {
                        sortedList[i].XInputSlot = slot;
                        break;
                    }
                }
            }

            return sortedList;
        }

        // Estrae solo la porzione VID e PID (es. VID_045E&PID_0B12) ignorando se è USB o Bluetooth
        private static string ExtractUniquePadKey(string devId, string rawDevId)
        {
            var match = Regex.Match(devId, @"VID_[0-9A-F]{4}&PID_[0-9A-F]{4}");
            string vidPid = match.Success ? match.Value : "GENERIC_PAD";

            // Prende gli ultimi caratteri dell'istanza hardware specifica di Windows per isolare i pad multipli
            string instanceSuffix = "";
            var parts = rawDevId.Split('\\');
            if (parts.Length >= 3)
            {
                string tail = parts[2];
                // Prende gli ultimi blocchi distinti dell'ID (es. il seriale o l'indirizzo Bluetooth parziale)
                var subParts = tail.Split('&');
                instanceSuffix = subParts.Length > 1 ? subParts.Last() : tail;
            }

            return $"{vidPid}_{instanceSuffix}";
        }
    }
}