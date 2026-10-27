using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

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
        public int PlayerIndex { get; set; } // 1, 2, 3 o 4
        public ControllerType Type { get; set; } = ControllerType.None;
        public string Name { get; set; } = "";
        
        // SOSTITUITO IconPathData con IconImageSource
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

        public event Action<List<ControllerInfo>>? OnControllersChanged;

        public ControllerDetector()
        {
            _pollTimer = new System.Timers.Timer(2000);
            _pollTimer.Elapsed += (s, e) => CheckControllers();
        }

        public void Start()
        {
            CheckControllers();
            _pollTimer.Start();
        }

        private void CheckControllers()
        {
            var controllers = DetectConnectedControllers();
            
            string signature = string.Join("|", controllers.Select(c => $"{c.PlayerIndex}:{c.Type}:{c.Name}"));
            if (signature != _lastStateSignature)
            {
                _lastStateSignature = signature;
                OnControllersChanged?.Invoke(controllers);
            }
        }

        public List<ControllerInfo> DetectConnectedControllers()
        {
            var list = new List<ControllerInfo>();
            var seenHardwareIds = new HashSet<string>();

            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, DeviceID FROM Win32_PnPEntity WHERE " +
                    "DeviceID LIKE '%VID_054C%' OR " + // Sony
                    "DeviceID LIKE '%VID_045E%' OR " + // Microsoft
                    "DeviceID LIKE '%VID_057E%' OR " + // Nintendo
                    "DeviceID LIKE '%IG_%'");          // XInput

                foreach (ManagementObject device in searcher.Get())
                {
                    string devId = (device["DeviceID"]?.ToString() ?? "").ToUpperInvariant();
                    string name = (device["Name"]?.ToString() ?? "").ToLowerInvariant();

                    if (!devId.StartsWith("HID\\") && !devId.Contains("IG_"))
                        continue;

                    string uniqueKey = devId.Split('\\').LastOrDefault() ?? devId;
                    if (seenHardwareIds.Contains(uniqueKey))
                        continue;

                    ControllerInfo? info = null;

                    // 1. SONY PLAYSTATION
                    if (devId.Contains("VID_054C"))
                    {
                        info = new ControllerInfo
                        {
                            Type = ControllerType.PlayStation,
                            Name = devId.Contains("PID_0CE6") || devId.Contains("PID_0DF2") ? "DualSense" : "DualShock",
                            BadgeColor = "#440070D1",
                            IconImageSource = "pack://application:,,,/assets/controllers/dualsense.png"
                        };
                    }
                    // 2. NINTENDO SWITCH
                    else if (devId.Contains("VID_057E"))
                    {
                        info = new ControllerInfo
                        {
                            Type = ControllerType.Nintendo,
                            Name = "Switch",
                            BadgeColor = "#44E60012",
                            IconImageSource = "pack://application:,,,/Assets/controller_switch.png"
                        };
                    }
                    // 3. MICROSOFT XBOX
                    else if (devId.Contains("VID_045E") && (devId.Contains("IG_") || name.Contains("xbox") || name.Contains("controller") || name.Contains("gamepad")))
                    {
                        info = new ControllerInfo
                        {
                            Type = ControllerType.Xbox,
                            Name = "Xbox Controller",
                            BadgeColor = "#44107C10",
                            IconImageSource = "pack://application:,,,/assets/controllers/xbox/special/fh5.png"
                        };
                    }

                    if (info != null)
                    {
                        seenHardwareIds.Add(uniqueKey);
                        info.PlayerIndex = list.Count + 1;
                        list.Add(info);

                        if (list.Count == 4) break;
                    }
                }
            }
            catch { }

            return list;
        }
    }
}