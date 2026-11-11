using System;
using System.Collections.Generic;
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

    public enum ControllerButton
    {
        DPadUp,
        DPadDown,
        DPadLeft,
        DPadRight,
        A,
        B,
        X,
        Y
    }

    public class ControllerInfo
    {
        public string UniqueId { get; set; } = "";
        public int PlayerIndex { get; set; }
        public uint XInputSlot { get; set; } = 0;
        public ulong BluetoothAddress { get; set; } = 0;
        public ControllerType Type { get; set; } = ControllerType.None;
        public string Name { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public string IconImageSource { get; set; } = "";
        public string BadgeColor { get; set; } = "#22FFFFFF";
    }

    public class ControllerDetector
    {
        private const int DPadInitialRepeatDelayMs = 360;
        private const int DPadRepeatIntervalMs = 105;

        private const ushort XINPUT_GAMEPAD_DPAD_UP    = 0x0001;
        private const ushort XINPUT_GAMEPAD_DPAD_DOWN  = 0x0002;
        private const ushort XINPUT_GAMEPAD_DPAD_LEFT  = 0x0004;
        private const ushort XINPUT_GAMEPAD_DPAD_RIGHT = 0x0008;
        private const ushort XINPUT_GAMEPAD_START      = 0x0010;
        private const ushort XINPUT_GAMEPAD_BACK       = 0x0020;
        private const ushort XINPUT_GAMEPAD_GUIDE      = 0x0400;
        private const ushort XINPUT_GAMEPAD_A          = 0x1000;
        private const ushort XINPUT_GAMEPAD_B          = 0x2000;
        private const ushort XINPUT_GAMEPAD_X          = 0x4000;
        private const ushort XINPUT_GAMEPAD_Y          = 0x8000;

        private static readonly (ushort Mask, ControllerButton Button, bool Repeatable)[] MappedButtons =
        {
            (XINPUT_GAMEPAD_DPAD_UP,    ControllerButton.DPadUp,    true),
            (XINPUT_GAMEPAD_DPAD_DOWN,  ControllerButton.DPadDown,  true),
            (XINPUT_GAMEPAD_DPAD_LEFT,  ControllerButton.DPadLeft,  true),
            (XINPUT_GAMEPAD_DPAD_RIGHT, ControllerButton.DPadRight, true),
            (XINPUT_GAMEPAD_A,          ControllerButton.A,         false),
            (XINPUT_GAMEPAD_B,          ControllerButton.B,         false),
            (XINPUT_GAMEPAD_X,          ControllerButton.X,         false),
            (XINPUT_GAMEPAD_Y,          ControllerButton.Y,         false),
        };

        private readonly System.Timers.Timer _pollTimer;
        private readonly System.Timers.Timer _inputTimer;
        private readonly Dictionary<string, string> _customIcons;
        private readonly Dictionary<ControllerButton, DateTime> _dpadNextRepeatUtc = new();
        private readonly object _controllersLock = new();

        private bool _wasComboPressed = false;
        private bool _anyXInputActive = false;
        private ushort _previousButtons = 0;
        private int _isPollingInput = 0;
        private string _lastStateSignature = "INIT";

        public event Action? OnToggleMenuShortcutPressed;
        public event Action<ControllerButton>? OnButtonPressed;
        public event Action<List<ControllerInfo>>? OnControllersChanged;

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_GAMEPAD
        {
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "#100")]
        private static extern uint XInputGetStateEx(uint dwUserIndex, out XINPUT_STATE pState);

        // --- P/INVOKE XINPUT ---
        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_VIBRATION
        {
            public ushort wLeftMotorSpeed;
            public ushort wRightMotorSpeed;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_STATE
        {
            public uint dwPacketNumber;
            public XINPUT_GAMEPAD Gamepad;
            
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern uint XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);

        [DllImport("xinput1_4.dll", EntryPoint = "XInputSetState")]
        private static extern uint XInputSetState(uint dwUserIndex, ref XINPUT_VIBRATION pVibration);

        [DllImport("xinput1_4.dll", EntryPoint = "#103")]
        private static extern uint XInputPowerOffController(uint dwUserIndex);

        // --- P/INVOKE BLUETOOTH ---
        private static readonly Guid HID_SERVICE_GUID = new Guid("00001124-0000-1000-8000-00805f9b34fb");
        private const uint BLUETOOTH_SERVICE_DISABLE = 0x00;
        private const uint BLUETOOTH_SERVICE_ENABLE = 0x01;

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEMTIME
        {
            public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct BLUETOOTH_DEVICE_INFO
        {
            public int dwSize;
            public ulong Address;
            public uint ulClassofDevice;
            [MarshalAs(UnmanagedType.Bool)] public bool fConnected;
            [MarshalAs(UnmanagedType.Bool)] public bool fRemembered;
            [MarshalAs(UnmanagedType.Bool)] public bool fAuthenticated;
            public SYSTEMTIME stLastSeen;
            public SYSTEMTIME stLastUsed;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
            public string szName;
        }

        [DllImport("BluetoothAPIs.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int BluetoothGetDeviceInfo(IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO pbtdi);

        [DllImport("BluetoothAPIs.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int BluetoothSetServiceState(IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO pbtdi, ref Guid pGuidService, uint dwServiceFlags);

        public ControllerDetector(Dictionary<string, string>? customIcons = null)
        {
            _customIcons = customIcons ?? new Dictionary<string, string>();
            _pollTimer = new System.Timers.Timer(2000);
            _pollTimer.Elapsed += (s, e) => CheckControllers();

            _inputTimer = new System.Timers.Timer(45);
            _inputTimer.Elapsed += (s, e) => CheckControllerShortcuts();
        }

        public void Start()
        {
            CheckControllers();
            _pollTimer.Start();
            _inputTimer.Start();
        }

        private void CheckControllers()
        {
            var controllers = DetectConnectedControllers();

            bool changed = false;
            lock (_controllersLock)
            {
                string signature = string.Join("|", controllers.Select(c => $"{c.UniqueId}:{c.XInputSlot}"));
                if (signature != _lastStateSignature)
                {
                    _lastStateSignature = signature;
                    changed = true;
                }
            }

            if (changed)
            {
                OnControllersChanged?.Invoke(controllers);
            }
        }

        // 1. VIBRAZIONE SUL CONTROLLER SELEZIONATO
        public async Task IdentifyControllerAsync(ControllerInfo controller)
        {
            uint slot = controller.XInputSlot;

            var zero = new XINPUT_VIBRATION { wLeftMotorSpeed = 0, wRightMotorSpeed = 0 };
            var heavyPulse = new XINPUT_VIBRATION { wLeftMotorSpeed = 55000, wRightMotorSpeed = 30000 };
            var lightPulse = new XINPUT_VIBRATION { wLeftMotorSpeed = 25000, wRightMotorSpeed = 60000 };

            try
            {
                XInputGetState(slot, out _);
                XInputSetState(slot, ref zero);
                await Task.Delay(25);

                XInputGetState(slot, out _);
                XInputSetState(slot, ref heavyPulse);
                await Task.Delay(180);

                XInputSetState(slot, ref zero);
                await Task.Delay(90);

                XInputGetState(slot, out _);
                XInputSetState(slot, ref lightPulse);
                await Task.Delay(200);

                XInputGetState(slot, out _);
                XInputSetState(slot, ref zero);
            }
            catch { }
        }

        // 2. SPEGNIMENTO DEL CONTROLLER SELEZIONATO
        public void TurnOffController(ControllerInfo controller)
        {
            Task.Run(async () =>
            {
                // A) Spegne via XInput (#103) lo specifico slot
                try
                {
                    XInputPowerOffController(controller.XInputSlot);
                }
                catch { }

                // B) Se è collegato via Bluetooth e abbiamo il suo indirizzo MAC, disconnette solo lui
                if (controller.BluetoothAddress != 0)
                {
                    try
                    {
                        var btInfo = new BLUETOOTH_DEVICE_INFO
                        {
                            dwSize = Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>(),
                            Address = controller.BluetoothAddress
                        };

                        if (BluetoothGetDeviceInfo(IntPtr.Zero, ref btInfo) == 0 && btInfo.fConnected)
                        {
                            Guid hidGuid = HID_SERVICE_GUID;
                            BluetoothSetServiceState(IntPtr.Zero, ref btInfo, ref hidGuid, BLUETOOTH_SERVICE_DISABLE);
                            await Task.Delay(1200);
                            BluetoothSetServiceState(IntPtr.Zero, ref btInfo, ref hidGuid, BLUETOOTH_SERVICE_ENABLE);
                        }
                    }
                    catch { }
                }
            });
        }

        public List<ControllerInfo> DetectConnectedControllers()
        {
            var rawList = new List<ControllerInfo>();
            var seenHardwareIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                // Query WMI valida (senza ContainerID che faceva fallire la query!)
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, DeviceID FROM Win32_PnPEntity WHERE " +
                    "DeviceID LIKE '%VID_054C%' OR " + // Sony
                    "DeviceID LIKE '%VID_045E%' OR " + // Microsoft
                    "DeviceID LIKE '%VID_057E%' OR " + // Nintendo
                    "DeviceID LIKE '%054C%' OR " +     // Bluetooth Sony
                    "DeviceID LIKE '%045E%' OR " +     // Bluetooth Microsoft
                    "DeviceID LIKE '%IG_%'");          // XInput

                foreach (ManagementObject device in searcher.Get())
                {
                    string rawDevId = device["DeviceID"]?.ToString() ?? "";
                    string devId = rawDevId.ToUpperInvariant();
                    string name = (device["Name"]?.ToString() ?? "").ToLowerInvariant();

                    if (!devId.StartsWith("HID\\"))
                        continue;

                    if (devId.Contains("&COL") && !devId.Contains("&COL01"))
                        continue;

                    // Chiave di deduplica che rimuove &IG_00 così non duplica col cavo USB
                    string cleanDevId = Regex.Replace(devId, @"&(IG|MI|COL)_[0-9A-F]{2}", "");
                    string tailKey = cleanDevId.Split('\\').LastOrDefault() ?? cleanDevId;

                    if (seenHardwareIds.Contains(tailKey))
                        continue;

                    ControllerInfo? info = null;
                    ulong btMac = ExtractBluetoothMac(devId);

                    // Legge eventuale slot XInput esplicito (IG_00 -> 0, IG_01 -> 1...)
                    uint? explicitSlot = null;
                    var igMatch = Regex.Match(devId, @"IG_0([0-3])");
                    if (igMatch.Success && uint.TryParse(igMatch.Groups[1].Value, out uint s))
                        explicitSlot = s;

                    // 1. SONY PLAYSTATION
                    if (devId.Contains("054C"))
                    {
                        info = new ControllerInfo
                        {
                            UniqueId = "PS_" + tailKey,
                            Type = ControllerType.PlayStation,
                            Name = devId.Contains("0CE6") || devId.Contains("0DF2") ? "DualSense" : "DualShock",
                            DeviceId = rawDevId,
                            BluetoothAddress = btMac,
                            XInputSlot = explicitSlot ?? 99,
                            BadgeColor = "#440070D1",
                            IconImageSource = "pack://application:,,,/assets/controllers/fluent/ps5.png"
                        };
                    }
                    // 2. NINTENDO SWITCH
                    else if (devId.Contains("057E"))
                    {
                        info = new ControllerInfo
                        {
                            UniqueId = "NSW_" + tailKey,
                            Type = ControllerType.Nintendo,
                            Name = "Switch",
                            DeviceId = rawDevId,
                            BluetoothAddress = btMac,
                            XInputSlot = explicitSlot ?? 99,
                            BadgeColor = "#44E60012",
                            IconImageSource = "pack://application:,,,/assets/controllers/fluent/procontroller.png"
                        };
                    }
                    // 3. MICROSOFT XBOX
                    else if (devId.Contains("045E") && (devId.Contains("IG_") || name.Contains("xbox") || name.Contains("controller") || name.Contains("gamepad")))
                    {
                        info = new ControllerInfo
                        {
                            UniqueId = "XBOX_" + tailKey,
                            Type = ControllerType.Xbox,
                            Name = "Xbox Controller",
                            DeviceId = rawDevId,
                            BluetoothAddress = btMac,
                            XInputSlot = explicitSlot ?? 99,
                            BadgeColor = "#44107C10",
                            IconImageSource = "pack://application:,,,/assets/controllers/fluent/xbox.png"
                        };
                    }

                    if (info != null)
                    {
                        seenHardwareIds.Add(tailKey);
                        rawList.Add(info);
                        if (rawList.Count == 4) break;
                    }
                }
            }
            catch { }

            var sortedList = rawList.OrderBy(c => c.UniqueId).ToList();

            // Trova tutti gli slot XInput (0..3) realmente attivi in questo momento
            var activeSlots = new List<uint>();
            for (uint slot = 0; slot < 4; slot++)
            {
                if (XInputGetState(slot, out _) == 0)
                    activeSlots.Add(slot);
            }

            var usedSlots = new HashSet<uint>();

            // Prima assegna chi ha già uno slot IG_0X esplicito
            foreach (var ctrl in sortedList.Where(c => c.XInputSlot < 4))
            {
                usedSlots.Add(ctrl.XInputSlot);
            }

            // Poi assegna agli altri (es. Bluetooth) il primo slot attivo libero, senza mai dare lo stesso slot a due controller!
            for (int i = 0; i < sortedList.Count; i++)
            {
                sortedList[i].PlayerIndex = i + 1;

                if (sortedList[i].XInputSlot >= 4)
                {
                    uint freeSlot = activeSlots.FirstOrDefault(s => !usedSlots.Contains(s));
                    sortedList[i].XInputSlot = freeSlot;
                    usedSlots.Add(freeSlot);
                }
            }

            return sortedList;
        }

        private void CheckControllerShortcuts()
        {
            if (Interlocked.Exchange(ref _isPollingInput, 1) == 1) return;

            try
            {
                bool isShortcutActiveNow = false;
                bool anySlotConnected = false;
                ushort combinedButtons = 0;

                for (uint slot = 0; slot < 4; slot++)
                {
                    if (XInputGetStateEx(slot, out XINPUT_STATE state) == 0)
                    {
                        anySlotConnected = true;
                        ushort buttons = state.Gamepad.wButtons;
                        combinedButtons |= buttons;

                        // Option A: Start + Select held simultaneously
                        bool startAndSelect = (buttons & XINPUT_GAMEPAD_START) != 0 &&
                                              (buttons & XINPUT_GAMEPAD_BACK) != 0;

                        // Option B: Xbox / PlayStation Guide button (if needed: || guideButton)
                        bool guideButton = (buttons & XINPUT_GAMEPAD_GUIDE) != 0;

                        if (startAndSelect)
                        {
                            isShortcutActiveNow = true;
                        }
                    }
                }

                if (anySlotConnected != _anyXInputActive)
                {
                    _anyXInputActive = anySlotConnected;
                    _ = Task.Run(CheckControllers);
                }

                // Trigger menu toggle only once on the rising edge of the shortcut combo.
                if (isShortcutActiveNow && !_wasComboPressed)
                {
                    _wasComboPressed = true;
                    _previousButtons = combinedButtons;
                    _dpadNextRepeatUtc.Clear();
                    OnToggleMenuShortcutPressed?.Invoke();
                    return;
                }
                else if (!isShortcutActiveNow)
                {
                    _wasComboPressed = false;
                }

                if (!isShortcutActiveNow)
                {
                    ProcessButtonTransitions(combinedButtons);
                }

                _previousButtons = combinedButtons;
            }
            finally
            {
                Interlocked.Exchange(ref _isPollingInput, 0);
            }
        }

        private void ProcessButtonTransitions(ushort currentButtons)
        {
            DateTime now = DateTime.UtcNow;

            foreach (var (mask, button, repeatable) in MappedButtons)
            {
                bool isPressed = (currentButtons & mask) != 0;
                bool wasPressed = (_previousButtons & mask) != 0;

                if (!isPressed)
                {
                    if (repeatable)
                    {
                        _dpadNextRepeatUtc.Remove(button);
                    }
                    continue;
                }

                if (!wasPressed)
                {
                    if (repeatable)
                    {
                        _dpadNextRepeatUtc[button] = now.AddMilliseconds(DPadInitialRepeatDelayMs);
                    }
                    OnButtonPressed?.Invoke(button);
                }
                else if (repeatable &&
                         _dpadNextRepeatUtc.TryGetValue(button, out DateTime nextRepeatAt) &&
                         now >= nextRepeatAt)
                {
                    _dpadNextRepeatUtc[button] = now.AddMilliseconds(DPadRepeatIntervalMs);
                    OnButtonPressed?.Invoke(button);
                }
            }
        }

        private static ulong ExtractBluetoothMac(string devId)
        {
            var match = Regex.Match(devId, @"([0-9A-F]{12})(?:&|_|\b)");
            if (match.Success && ulong.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out ulong mac) && mac != 0)
            {
                return mac;
            }
            return 0;
        }
    }
}