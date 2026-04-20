namespace NotADoctor99.LibreHardwareMonitorPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.NetworkInformation;

    using Loupedeck;

    public class ShowGaugeCommand : PluginDynamicCommand
    {
        private const String NetworkUtilParameterPrefix = "NetworkUtil::";
        private const String GroupMonitor = "Monitors";
        private const String GroupStorage = "Storages";
        private const String GroupNetwork = "Network";

        private readonly Dictionary<String, String> _networkActionParameterBySensorName = new Dictionary<String, String>(StringComparer.InvariantCultureIgnoreCase);

        // Cache of NIC GUID → link speed in KB/s (populated from NetworkInterface.Speed).
        private readonly Dictionary<String, Single> _nicLinkSpeedKBpsByGuid = new Dictionary<String, Single>(StringComparer.OrdinalIgnoreCase);

        private readonly Single[] _lastMonLevel = new Single[(Int32)LHMGaugeType.Count];
        private readonly Single[] _lastLevel = new Single[(Int32)LHMGaugeType.Count];
        private readonly Single[] _lastMinLevel = new Single[(Int32)LHMGaugeType.Count];
        private readonly Single[] _lastMaxLevel = new Single[(Int32)LHMGaugeType.Count];

        public ShowGaugeCommand()
        {
            this.IsWidget = true;
            this.GroupName = "Gauges";
            this.InitGuage();
        }

        protected override Boolean OnLoad()
        {
            this.UpdateParameters();

            LibreHardwareMonitorPlugin.HardwareMonitor.SensorListChanged += this.OnSensorListChanged;
            LibreHardwareMonitorPlugin.HardwareMonitor.SensorValuesChanged += this.OnSensorValuesChanged;
            LibreHardwareMonitorPlugin.HardwareMonitor.GaugeValuesChanged += this.OnGaugeValuesChanged;
            LibreHardwareMonitorPlugin.HardwareMonitor.MonitorValuesChanged += this.OnMonitorValuesChanged;
            LibreHardwareMonitorPlugin.HardwareMonitor.ProcessStarted += this.OnHardwareMonitorProcessStarted;
            LibreHardwareMonitorPlugin.HardwareMonitor.ProcessExited += this.OnHardwareMonitorProcessExited;

            return true;
        }

        protected override Boolean OnUnload()
        {
            LibreHardwareMonitorPlugin.HardwareMonitor.SensorListChanged -= this.OnSensorListChanged;
            LibreHardwareMonitorPlugin.HardwareMonitor.SensorValuesChanged -= this.OnSensorValuesChanged;
            LibreHardwareMonitorPlugin.HardwareMonitor.GaugeValuesChanged -= this.OnGaugeValuesChanged;
            LibreHardwareMonitorPlugin.HardwareMonitor.MonitorValuesChanged -= this.OnMonitorValuesChanged;
            LibreHardwareMonitorPlugin.HardwareMonitor.ProcessStarted -= this.OnHardwareMonitorProcessStarted;
            LibreHardwareMonitorPlugin.HardwareMonitor.ProcessExited -= this.OnHardwareMonitorProcessExited;

            return true;
        }

        protected override void RunCommand(String actionParameter) => LibreHardwareMonitor.ActivateOrRun();

        protected override String GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) => PluginHelpers.GetNotAvailableButtonText();

        private void UpdateParameters()
        {
            this.RemoveAllParameters();
            this.AddDefaultGaugeParameters();
            this.AddNetworkGaugeParameters();
            this.ParametersChanged();
            this.ActionImageChanged(null);
        }

        private void AddDefaultGaugeParameters()
        {
            AddGaugeParameter(LHMGaugeType.CPU_Load, this.GroupName);
            AddGaugeParameter(LHMGaugeType.CPU_Core, this.GroupName);
            AddGaugeParameter(LHMGaugeType.CPU_Package, this.GroupName);
            AddGaugeParameter(LHMGaugeType.CPU_Power, this.GroupName);

            AddGaugeParameter(LHMGaugeType.GPU_Load, this.GroupName);
            AddGaugeParameter(LHMGaugeType.GPU_Core, this.GroupName);
            AddGaugeParameter(LHMGaugeType.GPU_Hotspot, this.GroupName);
            AddGaugeParameter(LHMGaugeType.GPU_Power, this.GroupName);

            AddGaugeParameter(LHMGaugeType.Memory, this.GroupName);
            AddGaugeParameter(LHMGaugeType.Virtual_Memory, this.GroupName);
            AddGaugeParameter(LHMGaugeType.GPU_Memory, this.GroupName);

            AddGaugeParameter(LHMGaugeType.Storage_T_1, GroupStorage);
            AddGaugeParameter(LHMGaugeType.Storage_T_2, GroupStorage);
            AddGaugeParameter(LHMGaugeType.Storage_T_3, GroupStorage);
            AddGaugeParameter(LHMGaugeType.Storage_T_4, GroupStorage);
            AddGaugeParameter(LHMGaugeType.Storage_T_5, GroupStorage);
            AddGaugeParameter(LHMGaugeType.Storage_T_6, GroupStorage);

            AddGaugeParameter(LHMGaugeType.Storage_U_1, GroupStorage);
            AddGaugeParameter(LHMGaugeType.Storage_U_2, GroupStorage);
            AddGaugeParameter(LHMGaugeType.Storage_U_3, GroupStorage);
            AddGaugeParameter(LHMGaugeType.Storage_U_4, GroupStorage);
            AddGaugeParameter(LHMGaugeType.Storage_U_5, GroupStorage);
            AddGaugeParameter(LHMGaugeType.Storage_U_6, GroupStorage);

            AddGaugeParameter(LHMGaugeType.Monitor_CPU, GroupMonitor);
            AddGaugeParameter(LHMGaugeType.Monitor_GPU, GroupMonitor);
            AddGaugeParameter(LHMGaugeType.Monitor_Memory_Load, GroupMonitor);
            AddGaugeParameter(LHMGaugeType.Monitor_Memory, GroupMonitor);
            AddGaugeParameter(LHMGaugeType.Monitor_Storage_T_G1, GroupMonitor);
            AddGaugeParameter(LHMGaugeType.Monitor_Storage_T_G2, GroupMonitor);
            AddGaugeParameter(LHMGaugeType.Monitor_Storage_U_G1, GroupMonitor);
            AddGaugeParameter(LHMGaugeType.Monitor_Storage_U_G2, GroupMonitor);

            AddGaugeParameter(LHMGaugeType.Battery, this.GroupName);

            void AddGaugeParameter(LHMGaugeType gaugeType, String groupName) => this.AddParameter(gaugeType.ToString(), gaugeType.ToString().Replace('_', ' '), groupName);
        }

        private void AddNetworkGaugeParameters()
        {
            this._networkActionParameterBySensorName.Clear();

            var networkSensors = LibreHardwareMonitorPlugin.HardwareMonitor.Sensors
                .Where(this.IsNetworkUtilSensor)
                .OrderBy(this.GetNetworkAdapterDisplayName, StringComparer.InvariantCultureIgnoreCase)
                .ThenBy(sensor => sensor.Identifier, StringComparer.InvariantCultureIgnoreCase);

            foreach (var sensor in networkSensors)
            {
                var actionParameter = NetworkUtilParameterPrefix + sensor.Name;
                var adapterName = this.GetNetworkAdapterDisplayName(sensor);
                this.AddParameter(actionParameter, adapterName + " Net", GroupNetwork);
                this._networkActionParameterBySensorName[sensor.Name] = actionParameter;

                // Also track throughput sensors so their value changes trigger a refresh.
                this.TryGetNicThroughputSensors(sensor, out var downSensor, out var upSensor);
                if (downSensor != null) this._networkActionParameterBySensorName[downSensor.Name] = actionParameter;
                if (upSensor != null)   this._networkActionParameterBySensorName[upSensor.Name]   = actionParameter;
            }
        }

        private Boolean IsNetworkUtilSensor(LibreHardwareMonitorSensor sensor)
            => sensor != null
               && sensor.Identifier.IndexOf("/nic/", StringComparison.OrdinalIgnoreCase) >= 0
               && sensor.Identifier.IndexOf("/load/1", StringComparison.OrdinalIgnoreCase) >= 0;

        private String GetNicIdentifierPrefix(LibreHardwareMonitorSensor utilSensor)
        {
            var id = utilSensor?.Identifier ?? String.Empty;
            var loadIdx = id.IndexOf("/load/", StringComparison.OrdinalIgnoreCase);
            return loadIdx > 0 ? id.Substring(0, loadIdx + 1) : null;
        }

        private Boolean TryGetNicThroughputSensors(
            LibreHardwareMonitorSensor utilSensor,
            out LibreHardwareMonitorSensor downSensor,
            out LibreHardwareMonitorSensor upSensor)
        {
            downSensor = null;
            upSensor = null;
            var prefix = this.GetNicIdentifierPrefix(utilSensor);
            if (prefix == null) return false;

            var throughputSensors = LibreHardwareMonitorPlugin.HardwareMonitor.Sensors
                .Where(s => s.Identifier.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                         && s.Identifier.IndexOf("/throughput/", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            foreach (var s in throughputSensors)
            {
                var combined = (s.Name ?? String.Empty) + " " + (s.DisplayName ?? String.Empty);
                if (combined.IndexOf("receiv", StringComparison.OrdinalIgnoreCase) >= 0
                    || combined.IndexOf("down", StringComparison.OrdinalIgnoreCase) >= 0)
                    downSensor = s;
                else if (combined.IndexOf("sent", StringComparison.OrdinalIgnoreCase) >= 0
                    || combined.IndexOf("up", StringComparison.OrdinalIgnoreCase) >= 0)
                    upSensor = s;
            }

            // Fallback: if name-matching failed but exactly 2 sensors exist, assign by order.
            if (downSensor == null && upSensor == null && throughputSensors.Count >= 2)
            {
                downSensor = throughputSensors[0];
                upSensor   = throughputSensors[1];
            }

            return downSensor != null || upSensor != null;
        }

        /// <summary>
        /// Tries to get the link speed of the NIC associated with <paramref name="utilSensor"/>,
        /// in KB/s, by matching the GUID in the LHM sensor identifier against
        /// <see cref="NetworkInterface.GetAllNetworkInterfaces"/>.
        /// Results are cached so the reflection is only done once per GUID.
        /// Falls back to the user's settings file if detection fails.
        /// </summary>
        private Boolean TryGetNicLinkSpeedKBps(LibreHardwareMonitorSensor utilSensor, out Single kbps)
        {
            kbps = 0f;
            var id = utilSensor?.Identifier ?? String.Empty;

            // Extract GUID from  /nic/{GUID}/load/...
            var nicStart = id.IndexOf("/nic/", StringComparison.OrdinalIgnoreCase);
            if (nicStart < 0) return false;
            var guidStart = nicStart + "/nic/".Length;
            var guidEnd   = id.IndexOf('/', guidStart);
            if (guidEnd <= guidStart) return false;
            var guid = id.Substring(guidStart, guidEnd - guidStart); // e.g. "{B5B72A07-...}"

            if (this._nicLinkSpeedKBpsByGuid.TryGetValue(guid, out kbps))
                return kbps > 0f;

            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    // NetworkInterface.Id is the GUID, sometimes without braces.
                    var nicId = nic.Id.Trim('{', '}');
                    var sensorGuid = guid.Trim('{', '}');
                    if (!String.Equals(nicId, sensorGuid, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Speed is in bits/second; convert to KB/s.
                    var speedKBps = nic.Speed > 0 ? (Single)(nic.Speed / 8.0 / 1024.0) : 0f;
                    this._nicLinkSpeedKBpsByGuid[guid] = speedKBps;
                    PluginLog.Info($"NIC {guid} link speed: {nic.Speed / 1_000_000.0:N0} Mbps → {speedKBps:N0} KB/s");
                    kbps = speedKBps;
                    return kbps > 0f;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, $"Could not query NetworkInterface speed for {guid}");
            }

            this._nicLinkSpeedKBpsByGuid[guid] = 0f; // cache miss so we don't retry every frame
            return false;
        }

        private String GetNetworkAdapterDisplayName(LibreHardwareMonitorSensor sensor)
        {
            var displayName = sensor?.DisplayName ?? String.Empty;
            const String prefix = "[Network ";
            const String suffix = " Load]";
            if (displayName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var end = displayName.IndexOf(suffix, prefix.Length, StringComparison.OrdinalIgnoreCase);
                if (end > prefix.Length)
                {
                    return displayName.Substring(prefix.Length, end - prefix.Length);
                }
            }

            var name = sensor?.Name ?? String.Empty;
            var loadPos = name.IndexOf("-Load-", StringComparison.OrdinalIgnoreCase);
            if (loadPos > 0)
            {
                name = name.Substring(0, loadPos);
            }

            name = name.Replace("Network.", String.Empty, StringComparison.OrdinalIgnoreCase).Replace('.', ' ').Trim();
            return String.IsNullOrWhiteSpace(name) ? "Network" : name;
        }

        private BitmapColor GetColorByLevel(Single level, Single maxLevel, Int32 baseValue, BitmapColor accentColor) => new BitmapColor(level / maxLevel > 0.9 ? new BitmapColor(255, 30, 30) : accentColor, Helpers.MinMax((Int32)(baseValue + (255 - baseValue) * level / maxLevel), 0, 255));
        private BitmapColor GetColorByLevel(Single level, Single maxLevel, BitmapColor accentColor) => this.GetColorByLevel(level, maxLevel, 200, accentColor);

        private Int32[] frOutLine;
        private Int32[] frInLine;
        private Int32[] LeftLine;
        private Int32[] RightLine;

        private void InitGuage(Int32 bitmapWidth = 80)
        {
            var s = bitmapWidth - 2; // drawing area (leave 1px border on each side)
            // Rectangle: x, y, width, height — scale proportionally from original 78x78 design
            this.frOutLine = new Int32[4] { 0, 0, s, s };
            this.frInLine = new Int32[4] { s * 8 / 78, s * 19 / 78, s * 62 / 78, s * 54 / 78 };

            // Line: x1, y1, x2, y2
            var offset = 0;
            this.LeftLine = new Int32[4] { this.frInLine[0] - offset, this.frInLine[1], this.frInLine[0] - offset, this.frInLine[1] + this.frInLine[3] };
            this.RightLine = new Int32[4] { this.frInLine[0] + this.frInLine[2] + offset, this.frInLine[1], this.frInLine[0] + this.frInLine[2] + offset, this.frInLine[1] + this.frInLine[3] };
        }

        private void DrawGuage(BitmapBuilder bitmapBuilder, Single[] curLevel, Single[] maxLevel, Int32 barCount)
        {
            this.InitGuage(bitmapBuilder.Width);
            bitmapBuilder.Clear(BitmapColor.Black);

            var level = maxLevel[0] > 0f ? curLevel[0] / maxLevel[0] : 0f;
            var color1 = this.GetColorByLevel5(level, 255, 120);
            var color2 = this.GetColorByLevel5(level, 100, 120);

            var gray = new BitmapColor(BitmapColor.White, 220);
            var headerH = this.frOutLine[3] * 13 / 78; // proportional to drawing area
            bitmapBuilder.FillRectangle(this.frOutLine[0], this.frOutLine[1], this.frOutLine[2], headerH, color1);
            bitmapBuilder.FillRectangle(this.frOutLine[0], this.frOutLine[1] + headerH + 1, this.frOutLine[2], this.frOutLine[3] - headerH - 1, color2);
            bitmapBuilder.DrawRectangle(this.frOutLine[0], this.frOutLine[1], this.frOutLine[2], this.frOutLine[3], color1);

            //bitmapBuilder.FillRectangle(this.frOutLine[0], this.frOutLine[1], this.frOutLine[2], 1, gray);
            //bitmapBuilder.FillRectangle(this.frOutLine[0], this.frOutLine[1] + 15, this.frOutLine[2], 1, gray);

            var leftLineColor = this.GetColorByLevel5(level, 150, 80);
            var rightLineColor = this.GetColorByLevel5(level, 150, 80);
            this.DrawOutline(bitmapBuilder, curLevel, maxLevel, leftLineColor, rightLineColor);

            if (barCount == 2)
            {
                this.DrawBar2(bitmapBuilder, curLevel, maxLevel);
            }
            else
            {
                this.DrawBar(bitmapBuilder, curLevel, maxLevel);
            }
        }

        private void DrawBar(BitmapBuilder bitmapBuilder, Single[] curLevel, Single[] maxLevel)
        {
            bitmapBuilder.FillRectangle(this.frInLine[0], this.frInLine[1], this.frInLine[2], this.frInLine[3], BitmapColor.Black);
            var gray = new BitmapColor(BitmapColor.White, 255/2);
            var level = maxLevel[0] > 0f ? curLevel[0] / maxLevel[0] : 0f;
            if (level > 0.001)
            {
                var barColor = this.GetColorByLevel3(level, 200, 30);

                var x = this.frInLine[0] + 2;
                var y = this.frInLine[1] + 2;
                var w = this.frInLine[2] - 3;
                var h = this.frInLine[3] - 4;

                this.GetRectangleYHByLevel(level, y, h, out var bottomY, out var bottomH);
                bitmapBuilder.FillRectangle(x, bottomY, w, bottomH, barColor);
                bitmapBuilder.FillRectangle(x, bottomY, w, 1, gray);
                //bitmapBuilder.FillRectangle(x, bottomY + bottomH - 1, w, 1, gray);
            }
            //bitmapBuilder.DrawRectangle(this.frInLine[0], this.frInLine[1], this.frInLine[2], this.frInLine[3], gray);
        }

        private void DrawBar2(BitmapBuilder bitmapBuilder, Single[] curLevel, Single[] maxLevel)
        {
            var x = this.frInLine[0] + 2;
            var y = this.frInLine[1] + 2;
            var w = this.frInLine[2] / 2 - 3 - 1;
            var h = this.frInLine[3] - 4;
            var lx = this.frInLine[0];
            var ly = this.frInLine[1];
            var lw = this.frInLine[2] / 2 - 1;
            var lh = this.frInLine[3];

            bitmapBuilder.FillRectangle(this.frInLine[0], this.frInLine[1], this.frInLine[2], this.frInLine[3], BitmapColor.Black);
            var gray = new BitmapColor(BitmapColor.White, 255/2);

            var leftLevel = maxLevel[0] > 0f ? curLevel[0] / maxLevel[0] : 0f;
            if (leftLevel > 0.001)
            {
                var leftBarColor = this.GetColorByLevel3(leftLevel, 200, 30);
                this.GetRectangleYHByLevel(leftLevel, y, h, out var bottomLY, out var bottomLH);
                bitmapBuilder.FillRectangle(x, bottomLY, w, bottomLH, leftBarColor);
                bitmapBuilder.FillRectangle(x, bottomLY, w, 1, gray);
                //bitmapBuilder.FillRectangle(x, bottomLY + bottomLH - 1, w, 1, gray);
            }
            //bitmapBuilder.DrawRectangle(lx, ly, lw, lh, gray);

            var rightLevel = maxLevel[1] > 0f ? curLevel[1] / maxLevel[1] : 0f;
            if (rightLevel > 0.001)
            {
                var rightBarColor = this.GetColorByLevel3(rightLevel, 200, 30);
                this.GetRectangleYHByLevel(rightLevel, y, h, out var bottomRY, out var bottomRH);
                bitmapBuilder.FillRectangle(x + w + 5, bottomRY, w, bottomRH, rightBarColor);
                bitmapBuilder.FillRectangle(x + w + 5, bottomRY, w, 1, gray);
                //bitmapBuilder.FillRectangle(x + w + 5, bottomRY + bottomRH - 1, w, 1, gray);
            }
            //bitmapBuilder.DrawRectangle(lx + w + 5, ly, lw, lh, gray);
        }

        private BitmapColor GetColorByLevel3(Single level, Int32 alpha, Int32 baseRGB)
        {
            var colorLevel = new Single[] { 0f, 0.2f, 0.4f, 0.6f }; // g b r

            Int32 r = baseRGB, g = baseRGB, b = baseRGB, maxColor = 255 - baseRGB;
            var factor = 0.7f;
            if (level <= colorLevel[1])
            {
                // green
                var n = (level - colorLevel[0]) / (colorLevel[1] - colorLevel[0]);
                g += (Int32)(maxColor * n * factor);
            }
            else if (level <= colorLevel[2])
            {
                // blue
                var n = (level - colorLevel[1]) / (colorLevel[2] - colorLevel[1]);
                g += (Int32)(maxColor * (1 - n) * factor);
                b += (Int32)(maxColor * n * factor);
            }
            else if (level <= colorLevel[3])
            {
                // red
                var n = (level - colorLevel[2]) / (colorLevel[3] - colorLevel[2]);
                b += (Int32)(maxColor * (1 - n) * factor);
                r += (Int32)(maxColor * n * factor);
            }
            else
            {
                r += (Int32)(maxColor * factor);
            }
            return new BitmapColor(Helpers.MinMax(r, 0, 255), Helpers.MinMax(g, 0, 255), Helpers.MinMax(b, 0, 255), alpha);
        }

        private BitmapColor GetColorByLevel5(Single level, Int32 alpha, Int32 baseRGB)
        {
            var colorLevel = new Single[] { 0f, 0.2f, 0.4f, 0.6f, 0.8f, 1.0f }; // g b r

            Int32 r = baseRGB, g = baseRGB, b = baseRGB, maxColor = 255 - baseRGB;
            if (level < colorLevel[1])
            {
                // blue
                var n = (level - colorLevel[0]) / (colorLevel[1] - colorLevel[0]);
                g += (Int32)(maxColor * (1 - n));
                r += (Int32)(maxColor * (1 - n));
                b += (Int32)(baseRGB * n);
            }
            else if (level < colorLevel[2])
            {
                // green
                var n = (level - colorLevel[1]) / (colorLevel[2] - colorLevel[1]);
                r += (Int32)(maxColor * (1 - n));
                b += (Int32)(maxColor * (1 - n));
                g += (Int32)(maxColor * n);
            }
            else if (level < colorLevel[3])
            {
                // yellow(red + green)
                var n = (level - colorLevel[2]) / (colorLevel[3] - colorLevel[2]);
                b += (Int32)(maxColor * (1 - n));
                r += (Int32)(maxColor * n);
                g += (Int32)(maxColor * n);
            }
            else if (level < colorLevel[4])
            {
                // pink(red + blue)
                var n = (level - colorLevel[3]) / (colorLevel[4] - colorLevel[3]);
                g += (Int32)(maxColor * (1 - n));
                r += (Int32)(maxColor * n);
                b += (Int32)(maxColor * n);
            }
            else if (level < colorLevel[5])
            {
                // red
                var n = (level - colorLevel[4]) / (colorLevel[5] - colorLevel[4]);
                g += (Int32)(maxColor * (1 - n));
                b += (Int32)(maxColor * (1 - n));
                r += (Int32)(maxColor * n);
            }
            else
            {
                r += maxColor;
            }
            return new BitmapColor(Helpers.MinMax(r, 0, 255), Helpers.MinMax(g, 0, 255), Helpers.MinMax((Int32)(b * 0.6), 0, 255), alpha);
        }

        private void GetRectangleYHByLevel(Single level, Int32 y, Int32 height, out Int32 bottomY, out Int32 bottomH)
        {
            // y1 = y2 - height;
            // y2 = y1 + height;
            bottomH = (Int32)(height * level);
            bottomY = height + y - bottomH;
        }
        private void GetLineY1Y2ByLevel(Single level, Int32 y1, Int32 y2, out Int32 middleY1, out Int32 middleY2)
        {
            var halfHeight = (y2 - y1) / 2;
            var middleY = y1 + halfHeight;
            var levelH = halfHeight * level;
            middleY1 = (Int32)(middleY - levelH);
            middleY2 = (Int32)(middleY + levelH);
        }

        private void DrawOutline(BitmapBuilder bitmapBuilder, Single[] curLevel, Single[] maxLevel, BitmapColor leftColor, BitmapColor rightColor)
        {
            var gray = new BitmapColor(BitmapColor.White, 225/2);

            // Left Line: x1, y1, x2, y2
            var lx1 = this.LeftLine[0] - 3;
            var ly1 = this.LeftLine[1];
            var lx2 = this.LeftLine[2] - 3;
            var ly2 = this.LeftLine[3];
            this.GetLineY1Y2ByLevel(maxLevel[0] > 0f ? curLevel[0] / maxLevel[0] : 0f, ly1, ly2, out var middleLY1, out var middleLY2);
            bitmapBuilder.DrawLine(lx1, middleLY1, lx2, middleLY2, leftColor, 4);

            bitmapBuilder.DrawLine(lx1 - 2, middleLY1, lx2 + 2, middleLY1, gray, 1);
            bitmapBuilder.DrawLine(lx1 - 2, middleLY2, lx2 + 2, middleLY2, gray, 1);

            // Right Line: x1, y1, x2, y2
            var rx1 = this.RightLine[0] + 4;
            var ry1 = this.RightLine[1];
            var rx2 = this.RightLine[2] + 4;
            var ry2 = this.RightLine[3];
            this.GetLineY1Y2ByLevel(maxLevel[0] > 0f ? curLevel[0] / maxLevel[0] : 0f, ry1, ry2, out var middleRY1, out var middleRY2);
            bitmapBuilder.DrawLine(rx1, middleRY1, rx2, middleRY2, rightColor, 4);

            bitmapBuilder.DrawLine(rx1 - 2, middleRY1, rx1 + 2, middleRY1, gray, 1);
            bitmapBuilder.DrawLine(rx1 - 2, middleRY2, rx1 + 2, middleRY2, gray, 1);
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            PluginLog.Info($"GetCommandImage: {actionParameter} imageSize={imageSize}");
            try
            {
                return this.GetCommandImageImpl(actionParameter, imageSize);
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, $"GetCommandImage exception: {actionParameter} imageSize={imageSize}");
                return null;
            }
        }

        private Boolean TryGetNetworkUtilSensor(String actionParameter, out LibreHardwareMonitorSensor sensor)
        {
            sensor = null;
            if (String.IsNullOrWhiteSpace(actionParameter)
                || !actionParameter.StartsWith(NetworkUtilParameterPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var sensorName = actionParameter.Substring(NetworkUtilParameterPrefix.Length);
            return !String.IsNullOrWhiteSpace(sensorName)
                   && LibreHardwareMonitorPlugin.HardwareMonitor.TryGetSensor(sensorName, out sensor)
                   && this.IsNetworkUtilSensor(sensor);
        }

        private BitmapImage GetNetworkUtilGaugeImage(LibreHardwareMonitorSensor utilSensor, PluginImageSize imageSize)
        {
            if (utilSensor == null)
            {
                return PluginHelpers.GetNotAvailableButtonImage();
            }

            this.TryGetNicThroughputSensors(utilSensor, out var downSensor, out var upSensor);

            using (var bitmapBuilder = new BitmapBuilder(imageSize))
            {
                var scale = bitmapBuilder.Width / 80.0f;
                var w = bitmapBuilder.Width;
                var h = bitmapBuilder.Height;

                bitmapBuilder.Clear(BitmapColor.Black);

                // Header — color driven by utilization level
                var utilLevel = Math.Min(Math.Max(utilSensor.Value / 100f, 0f), 1f);
                var headerColor = this.GetColorByLevel5(utilLevel, 255, 120);
                var bodyColor   = this.GetColorByLevel5(utilLevel, 100, 120);
                var titleH = (Int32)(scale * 13);

                bitmapBuilder.FillRectangle(0, 0, w, titleH, headerColor);
                bitmapBuilder.FillRectangle(0, titleH, w, h - titleH, bodyColor);
                bitmapBuilder.DrawRectangle(0, 0, w, h, headerColor);

                var adapterName = this.GetNetworkAdapterDisplayName(utilSensor);
                if (adapterName.Length > 16) adapterName = adapterName.Substring(0, 16);
                var titleFontSize = (Int32)(scale * (adapterName.Length > 13 ? 11 : adapterName.Length > 9 ? 12 : 13));
                bitmapBuilder.DrawText(adapterName, 0, 1, w, titleH, BitmapColor.Black, titleFontSize);

                // 3 data rows — each with its own horizontal fill bar
                var rowH         = (h - titleH) / 3;
                var monFontSize  = (Int32)(scale * 11);
                var unitFontSize = (Int32)(scale * 9);
                var accentColor  = utilSensor.Color;
                var unitColor    = new BitmapColor(accentColor, 200);
                var sepColor     = new BitmapColor(BitmapColor.White, 40);

                var maxThroughput = this.TryGetNicLinkSpeedKBps(utilSensor, out var detectedKBps)
                    ? detectedKBps
                    : PluginSettings.Current.NetworkMaxSpeedKBps;

                // label centered @ x=10, value centered @ x=42, unit centered @ x=68
                // DrawText(text, x, y, width, height) centers in rect [x, y, x+width, y+height]
                // so: centerX = x + width/2  →  x = centerX - width/2
                var labelX = (Int32)(-30 * scale); // center @ 10
                var valueX = (Int32)(2  * scale);  // center @ 42
                var unitX  = (Int32)(28 * scale);  // center @ 68

                void DrawRow(Int32 idx, String label, String valueStr, String unitStr, Single barLevel)
                {
                    var rowY = titleH + idx * rowH;
                    barLevel = Math.Min(Math.Max(barLevel, 0f), 1f);

                    // Individual bar fill
                    var filledW = (Int32)((w - 2) * barLevel);
                    if (filledW > 0)
                    {
                        var barColor = this.GetColorByLevel3(barLevel, 160, 30);
                        bitmapBuilder.FillRectangle(1, rowY + 1, filledW, rowH - 2, barColor);
                    }

                    // Row separator
                    bitmapBuilder.FillRectangle(0, rowY + rowH - 1, w, 1, sepColor);

                    // Texts — label left, value center, unit right
                    bitmapBuilder.DrawText(label,    labelX, rowY, w, rowH, accentColor,       monFontSize);
                    bitmapBuilder.DrawText(valueStr, valueX, rowY, w, rowH, BitmapColor.White, monFontSize);
                    bitmapBuilder.DrawText(unitStr,  unitX,  rowY, w, rowH, BitmapColor.White, unitFontSize);
                    bitmapBuilder.DrawText(unitStr,  unitX,  rowY, w, rowH, unitColor,         unitFontSize);
                }

                var (downText, downUnit) = FormatNetworkSpeed(downSensor?.Value ?? 0f);
                var downLevel = downSensor != null ? downSensor.Value / maxThroughput : 0f;

                var (upText, upUnit) = FormatNetworkSpeed(upSensor?.Value ?? 0f);
                var upLevel = upSensor != null ? upSensor.Value / maxThroughput : 0f;

                var utilText = utilSensor.Value > 99.9f ? $"{utilSensor.Value:N0}" : $"{utilSensor.Value:N1}";
                DrawRow(0, "U",  utilText, "%",      utilLevel);
                DrawRow(1, "\u2193", downText, downUnit, downLevel);
                DrawRow(2, "\u2191", upText,   upUnit,   upLevel);

                return bitmapBuilder.ToImage();
            }

            static (String text, String unit) FormatNetworkSpeed(Single kbps)
                => kbps >= 1024f
                    ? ($"{kbps / 1024f:N1}", "M")
                    : ($"{kbps:N0}", "K");
        }

        private BitmapImage GetCommandImageImpl(String actionParameter, PluginImageSize imageSize)
        {
            if (this.TryGetNetworkUtilSensor(actionParameter, out var networkSensor))
            {
                return this.GetNetworkUtilGaugeImage(networkSensor, imageSize);
            }

            if (!Enum.TryParse<LHMGaugeType>(actionParameter, out var gaugeType))
            {
                return PluginHelpers.GetNotAvailableButtonImage();
            }
            LibreHardwareMonitorPlugin.HardwareMonitor.TryGetSensor(gaugeType, out var sensor);
            if (sensor == null)
            {
                if (LibreHardwareMonitorPlugin.HardwareMonitor.TryGetSensorList(gaugeType, out var sensorList))
                {
                    sensor = sensorList[0];
                }
                else if (!LibreHardwareMonitor.IsRunning())
                {
                    return PluginHelpers.GetNotAvailableButtonImage();
                }
                // else: LHM is running but this sensor type isn't exposed by the hardware — render with 0 values
            }

            using (var bitmapBuilder = new BitmapBuilder(imageSize))
            {
                var displayName = gaugeType.ToString().Replace('_', ' ');
                var defaultAccentColor = new BitmapColor(100, 100, 100);
                var accentColor = (gaugeType == LHMGaugeType.Monitor_Memory
                                   || gaugeType == LHMGaugeType.Monitor_Memory_Load) ? new BitmapColor(255, 10, 90) : sensor?.Color ?? defaultAccentColor;
                var titleColor = BitmapColor.Black;
                var valueColor = BitmapColor.White;
                var unitColor = new BitmapColor(accentColor, 180);
                var monTitleColor = accentColor;
                var monValueColor = BitmapColor.White;

                var maxLevel = new[] { this._lastMaxLevel[(Int32)gaugeType], this._lastMaxLevel[(Int32)gaugeType], this._lastMaxLevel[(Int32)gaugeType] };
                var curLevel = new[] { this._lastLevel[(Int32)gaugeType], this._lastLevel[(Int32)gaugeType], this._lastLevel[(Int32)gaugeType] };
                var monType = new[] { (Int32)gaugeType, (Int32)gaugeType, (Int32)gaugeType };

                var scale = bitmapBuilder.Width / 80.0f;

                String[] titleText;
                var titleX = new[] { 0, -18 };
                var titleY = 1;

                String valueText;
                var valueX = new[] { -1, -15, +15, +3 };
                var valueY1 = new[] { (Int32)(scale * (6 + 16 * 1)), (Int32)(scale * (6 + 16 * 2)), (Int32)(scale * (6 + 16 * 3)), (Int32)(scale * (6 + 16 * 4)) };
                var valueY2 = new[] { (Int32)(scale * (6 + 22 * 1)), (Int32)(scale * (6 + 22 * 2)) };

                String[] unitText;
                var unitX = new[] { 28, -8, 24, 21 };
                var unitY1 = new[] { valueY1[0] + 2, valueY1[1] + 0, valueY1[2] + 0 };
                var unitY2 = new[] { valueY2[0] + 0, valueY2[1] + 0 };

                var titleFontSize = (Int32)(scale * (displayName.Length > 13 ? 11 : displayName.Length > 9 ? 12 : 13));
                var valueFontSize = (Int32)(scale * 15);
                var valueDualFontSize = (Int32)(scale * 14);
                var monFontSize = (Int32)(scale * 12);
                var unitFontSize = (Int32)(scale * 10);

                var width = bitmapBuilder.Width;
                var height = (Int32)(scale * 11);

                Boolean Avail(Int32 idx) => this._lastMaxLevel[idx] > 0;
                void DrawGuage1(String dname)
                {
                    this.DrawGuage(bitmapBuilder, curLevel, maxLevel, 1);
                    bitmapBuilder.DrawText(dname, titleX[0], titleY, width, height, titleColor, titleFontSize);
                }
                void DrawGuage2(String dname)
                {
                    this.DrawGuage(bitmapBuilder, curLevel, maxLevel, 2);
                    bitmapBuilder.DrawText(dname, titleX[0], titleY, width, height, titleColor, titleFontSize);
                }
                void DrawValueXY(String vt, Int32 x, Int32 y, Int32 fontType) => bitmapBuilder.DrawText(vt, x, y, width, height, valueColor, fontType == 1 ? valueFontSize : valueDualFontSize);
                void DrawValue(String vt) => bitmapBuilder.DrawText(vt, valueX[0], valueY1[1], width, height, valueColor, valueFontSize);

                void DrawUnitXY(String ut, Int32 x, Int32 y)
                {
                    bitmapBuilder.DrawText(ut, x, y, width, height, BitmapColor.White, unitFontSize);
                    bitmapBuilder.DrawText(ut, x, y, width, height, unitColor, unitFontSize);
                }

                void DrawUnit(String ut)
                {
                    bitmapBuilder.DrawText(ut, unitX[0], unitY1[1], width, height, BitmapColor.White, unitFontSize);
                    bitmapBuilder.DrawText(ut, unitX[0], unitY1[1], width, height, unitColor, unitFontSize);
                }

                var i = 0;
                switch (gaugeType)
                {
                    case LHMGaugeType.GPU_Load:
                    case LHMGaugeType.CPU_Load:
                        DrawGuage1(displayName);
                        DrawValue(curLevel[0] > 99.9 ? $"{curLevel[0]:N0}" : $"{curLevel[0]:N1}");
                        DrawUnit("%");
                        break;

                    case LHMGaugeType.GPU_Core:
                    case LHMGaugeType.CPU_Core:
                        for (i = 0; i < 2; i++)
                        {
                            monType[i] = (Int32)gaugeType + i;
                            curLevel[i] = this._lastLevel[monType[i]];
                            maxLevel[i] = this._lastMaxLevel[monType[i]];
                        }
                        DrawGuage2(displayName);
                        for (i = 0; i < 2; i++)
                        {
                            DrawValueXY($"{curLevel[i]:N0}", valueX[i + 1], valueY1[1], 2);
                            DrawUnitXY("℃", unitX[i + 1], unitY1[0]);
                        }
                        break;

                    case LHMGaugeType.GPU_Hotspot:
                    case LHMGaugeType.CPU_Package:
                        DrawGuage1(displayName);
                        DrawValue($"{curLevel[0]:N1}");
                        DrawUnit("℃");
                        break;

                    case LHMGaugeType.CPU_Power:
                    case LHMGaugeType.GPU_Power:
                        DrawGuage1(displayName);
                        DrawValue($"{curLevel[0]:N1}");
                        DrawUnit("W");
                        break;

                    case LHMGaugeType.Virtual_Memory:
                    case LHMGaugeType.Memory:
                    case LHMGaugeType.GPU_Memory:
                        monType[0] -= 4;
                        for (i = 0; i < 3; i++)
                        {
                            maxLevel[i] = this._lastMaxLevel[monType[0]];
                            curLevel[i] = this._lastLevel[monType[0]];
                        }
                        DrawGuage1(displayName);
                        for (i = 0; i < 2; i++)
                        {
                            maxLevel[i] = this._lastMaxLevel[monType[i]];
                            curLevel[i] = this._lastLevel[monType[i]];
                        }
                        // GPU_Memory value is in MB from /smalldata/ paths but in GB from /vram/ or /data/ paths
                        var gpuMemInMB = sensor?.Identifier.IndexOf("/smalldata/", StringComparison.OrdinalIgnoreCase) >= 0;
                        var memGbVal = gaugeType == LHMGaugeType.GPU_Memory ? (gpuMemInMB ? curLevel[1] / 1024 : curLevel[1]) : curLevel[1];
                        DrawValueXY(Avail(monType[1]) ? $"{memGbVal:N1}" : "---", titleX[0], valueY2[0], 1);
                        if (Avail(monType[1])) DrawUnitXY("G", unitX[0], unitY2[0]);
                        DrawValueXY(Avail(monType[0]) ? $"{curLevel[0]:N1}" : "---", titleX[0], valueY2[1], 1);
                        if (Avail(monType[0])) DrawUnitXY("%", unitX[0], unitY2[1]);
                        break;

                    case LHMGaugeType.Storage_T_1:
                    case LHMGaugeType.Storage_T_2:
                    case LHMGaugeType.Storage_T_3:
                    case LHMGaugeType.Storage_T_4:
                    case LHMGaugeType.Storage_T_5:
                    case LHMGaugeType.Storage_T_6:
                    case LHMGaugeType.Storage_U_1:
                    case LHMGaugeType.Storage_U_2:
                    case LHMGaugeType.Storage_U_3:
                    case LHMGaugeType.Storage_U_4:
                    case LHMGaugeType.Storage_U_5:
                    case LHMGaugeType.Storage_U_6:
                        DrawGuage1(displayName);
                        DrawValue(gaugeType >= LHMGaugeType.Storage_U_1 ? $"{curLevel[0]:N1}" : $"{curLevel[0]:N0}");
                        DrawUnit(gaugeType >= LHMGaugeType.Storage_U_1 ? "%" : "℃");
                        break;

                    // Monitors
                    case LHMGaugeType.Monitor_CPU:
                    case LHMGaugeType.Monitor_GPU:
                        monType[0] += 1;
                        monType[1] += 2;
                        monType[2] += 4;
                        for (i = 0; i < 3; i++)
                        {
                            maxLevel[i] = this._lastMaxLevel[monType[i]];
                            curLevel[i] = this._lastMonLevel[monType[i]];
                        }
                        displayName = gaugeType == LHMGaugeType.Monitor_CPU ? "CPU Monitor" : "GPU Monitor";
                        DrawGuage1(displayName);
                        titleText = new[] { "L", "C", "P" };
                        unitText = new[] { "%", "℃", "W" };
                        for (i = 0; i < 3; i++)
                        {
                            maxLevel[i] = this._lastMaxLevel[monType[i]];
                            curLevel[i] = this._lastMonLevel[monType[i]];
                            valueText = i == 0 && curLevel[i] > 99.9 ? $"{curLevel[i]:N0}" : $"{curLevel[i]:N1}";
                            bitmapBuilder.DrawText(titleText[i], titleX[1], valueY1[i], width, height, monTitleColor, monFontSize);
                            bitmapBuilder.DrawText(valueText, valueX[3], valueY1[i], width, height, this.GetColorByLevel(curLevel[i], maxLevel[i], monValueColor), monFontSize);
                            bitmapBuilder.DrawText(unitText[i], unitX[3], unitY1[i], width, height, BitmapColor.White, unitFontSize);
                            bitmapBuilder.DrawText(unitText[i], unitX[3], unitY1[i], width, height, unitColor, unitFontSize);
                        }
                        break;

                    case LHMGaugeType.Monitor_Memory_Load:
                        monType[0] = (Int32)LHMGaugeType.Memory_Load;
                        monType[1] = (Int32)LHMGaugeType.Virtual_Memory_Load;
                        monType[2] = (Int32)LHMGaugeType.GPU_Memory_Load;
                        for (i = 0; i < 3; i++)
                        {
                            maxLevel[i] = this._lastMaxLevel[monType[i]];
                            curLevel[i] = this._lastMonLevel[monType[i]];
                        }
                        displayName = "Memory Load";
                        DrawGuage1(displayName);
                        titleText = new[] { "S", "V", "G" };
                        unitText = new[] { "%", "%", "%" };
                        for (i = 0; i < 3; i++)
                        {
                            valueText = $"{curLevel[i]:N1}";
                            bitmapBuilder.DrawText(titleText[i], titleX[1], valueY1[i], width, height, monTitleColor, monFontSize);
                            bitmapBuilder.DrawText(valueText, valueX[3], valueY1[i], width, height, this.GetColorByLevel(curLevel[i], maxLevel[i], monValueColor), monFontSize);
                            bitmapBuilder.DrawText(unitText[i], unitX[3], unitY1[i], width, height, BitmapColor.White, unitFontSize);
                            bitmapBuilder.DrawText(unitText[i], unitX[3], unitY1[i], width, height, unitColor, unitFontSize);
                        }
                        break;

                    case LHMGaugeType.Monitor_Memory:
                        monType[0] = (Int32)LHMGaugeType.Memory_Load;
                        monType[1] = (Int32)LHMGaugeType.Virtual_Memory_Load;
                        monType[2] = (Int32)LHMGaugeType.GPU_Memory_Load;
                        for (i = 0; i < 3; i++)
                        {
                            maxLevel[i] = this._lastMaxLevel[monType[i]];
                            curLevel[i] = this._lastLevel[monType[i]];
                        }
                        displayName = "Memory";
                        DrawGuage1(displayName);
                        monType[0] = (Int32)LHMGaugeType.Memory;
                        monType[1] = (Int32)LHMGaugeType.Virtual_Memory;
                        monType[2] = (Int32)LHMGaugeType.GPU_Memory;
                        titleText = new[] { "S", "V", "G" };
                        unitText = new[] { "G", "G", "G" };
                        for (i = 0; i < 3; i++)
                        {
                            maxLevel[i] = this._lastMaxLevel[monType[i]];
                            curLevel[i] = i == 2 ? this._lastMonLevel[monType[i]] / 1024 : this._lastMonLevel[monType[i]];
                            valueText = $"{curLevel[i]:N1}";
                            bitmapBuilder.DrawText(titleText[i], titleX[1], valueY1[i], width, height, monTitleColor, monFontSize);
                            bitmapBuilder.DrawText(valueText, valueX[3], valueY1[i], width, height, this.GetColorByLevel(curLevel[i], maxLevel[i], monValueColor), monFontSize);
                            bitmapBuilder.DrawText(unitText[i], unitX[3], unitY1[i], width, height, BitmapColor.White, unitFontSize);
                            bitmapBuilder.DrawText(unitText[i], unitX[3], unitY1[i], width, height, unitColor, unitFontSize);
                        }
                        break;

                    case LHMGaugeType.Monitor_Storage_T_G1:
                    case LHMGaugeType.Monitor_Storage_T_G2:
                    case LHMGaugeType.Monitor_Storage_U_G1:
                    case LHMGaugeType.Monitor_Storage_U_G2:
                        var uname = gaugeType == LHMGaugeType.Monitor_Storage_T_G1 || gaugeType == LHMGaugeType.Monitor_Storage_T_G2 ? "℃" : "%";
                        var dname = gaugeType == LHMGaugeType.Monitor_Storage_T_G1 || gaugeType == LHMGaugeType.Monitor_Storage_U_G1 ? "Storage G1" : "Storage G2";
                        titleText = new String[3];
                        unitText = new String[3];
                        for (i = 0; i < 3; i++)
                        {
                            monType[i] = (Int32)gaugeType + i + 1;
                            titleText[i] = (gaugeType == LHMGaugeType.Monitor_Storage_T_G1 || gaugeType == LHMGaugeType.Monitor_Storage_T_G2 ? $"T" : $"U")
                                         + (gaugeType == LHMGaugeType.Monitor_Storage_T_G1 || gaugeType == LHMGaugeType.Monitor_Storage_U_G1 ? $"{i + 1}" : $"{i + 4}");
                            unitText[i] = uname;
                            maxLevel[i] = this._lastMaxLevel[monType[i]];
                            curLevel[i] = this._lastMonLevel[monType[i]];
                            if (curLevel[i] > 0)
                            {
                                if (i == 0)
                                {
                                    DrawGuage1(dname);
                                }
                                valueText = $"{curLevel[i]:N0}";
                                bitmapBuilder.DrawText(titleText[i], titleX[1], valueY1[i], width, height, monTitleColor, monFontSize);
                                bitmapBuilder.DrawText(valueText, valueX[3], valueY1[i], width, height, this.GetColorByLevel(curLevel[i], maxLevel[i], monValueColor), monFontSize);
                                bitmapBuilder.DrawText(unitText[i], unitX[3], unitY1[i], width, height, BitmapColor.White, unitFontSize);
                                bitmapBuilder.DrawText(unitText[i], unitX[3], unitY1[i], width, height, unitColor, unitFontSize);
                            }
                        }
                        break;

                    case LHMGaugeType.Battery:
                        DrawGuage1(displayName);
                        DrawValue($"{curLevel[0]:N1}");
                        DrawUnit("%");
                        break;
                }

                return bitmapBuilder.ToImage();
            }
        }

        private void OnSensorValuesChanged(Object sender, LibreHardwareMonitorSensorValuesChangedEventArgs e)
        {
            foreach (var sensorName in e.SensorNames)
            {
                if (this._networkActionParameterBySensorName.TryGetValue(sensorName, out var actionParameter))
                {
                    this.ActionImageChanged(actionParameter);
                }
            }
        }

        private void OnGaugeValuesChanged(Object sender, LibreHardwareMonitorGaugeValueChangedEventArgs e)
        {
            foreach (var gaugeType in e.GaugeTypes)
            {
                if (this.UpdateGaugeIndex(gaugeType))
                {
                    this.ActionImageChanged(gaugeType.ToString());
                }
            }
        }
        private void OnMonitorValuesChanged(Object sender, LibreHardwareMonitorMonitorValueChangedEventArgs e)
        {
            foreach (var monitorType in e.MonitorTypes)
            {
                if (this.UpdateMonitorIndex(monitorType))
                {
                    this.ActionImageChanged(monitorType.ToString());
                }
            }
        }

        private void OnSensorListChanged(Object sender, EventArgs e)
        {
            this._nicLinkSpeedKBpsByGuid.Clear(); // re-detect link speeds in case adapters changed
            this.UpdateParameters();

            this.UpdateGaugeIndex(LHMGaugeType.CPU_Load);
            this.UpdateGaugeIndex(LHMGaugeType.CPU_Core);
            this.UpdateGaugeIndex(LHMGaugeType.CPU_Package);
            this.UpdateGaugeIndex(LHMGaugeType.CPU_Power);

            this.UpdateGaugeIndex(LHMGaugeType.GPU_Load);
            this.UpdateGaugeIndex(LHMGaugeType.GPU_Core);
            this.UpdateGaugeIndex(LHMGaugeType.GPU_Hotspot);
            this.UpdateGaugeIndex(LHMGaugeType.GPU_Power);

            this.UpdateGaugeIndex(LHMGaugeType.Memory_Load);
            this.UpdateGaugeIndex(LHMGaugeType.Virtual_Memory_Load);
            this.UpdateGaugeIndex(LHMGaugeType.GPU_Memory_Load);

            this.UpdateGaugeIndex(LHMGaugeType.Memory);
            this.UpdateGaugeIndex(LHMGaugeType.Virtual_Memory);
            this.UpdateGaugeIndex(LHMGaugeType.GPU_Memory);

            this.UpdateGaugeIndex(LHMGaugeType.Storage_T_1);
            this.UpdateGaugeIndex(LHMGaugeType.Storage_T_2);
            this.UpdateGaugeIndex(LHMGaugeType.Storage_T_3);
            this.UpdateGaugeIndex(LHMGaugeType.Storage_T_4);
            this.UpdateGaugeIndex(LHMGaugeType.Storage_T_5);

            this.UpdateGaugeIndex(LHMGaugeType.Storage_U_1);
            this.UpdateGaugeIndex(LHMGaugeType.Storage_U_2);
            this.UpdateGaugeIndex(LHMGaugeType.Storage_U_3);
            this.UpdateGaugeIndex(LHMGaugeType.Storage_U_4);
            this.UpdateGaugeIndex(LHMGaugeType.Storage_U_5);

            this.UpdateGaugeIndex(LHMGaugeType.Battery);

            this.UpdateMonitorIndex(LHMGaugeType.Monitor_CPU);
            this.UpdateMonitorIndex(LHMGaugeType.Monitor_GPU);
            this.UpdateMonitorIndex(LHMGaugeType.Monitor_Memory_Load);
            this.UpdateMonitorIndex(LHMGaugeType.Monitor_Memory);
            this.UpdateMonitorIndex(LHMGaugeType.Monitor_Storage_T_G1);
            this.UpdateMonitorIndex(LHMGaugeType.Monitor_Storage_T_G2);
            this.UpdateMonitorIndex(LHMGaugeType.Monitor_Storage_U_G1);
            this.UpdateMonitorIndex(LHMGaugeType.Monitor_Storage_U_G2);

            this.ActionImageChanged(null);
        }

        private Boolean UpdateGaugeIndex(LHMGaugeType gaugeType)
        {
            if (!LibreHardwareMonitorPlugin.HardwareMonitor.TryGetSensor(gaugeType, out var sensor))
            {
                return false;
            }

            // Always update min/max so DrawGuage never divides by zero (NaN/Int32.MinValue crash).
            this._lastMinLevel[(Int32)gaugeType] = sensor.MinValue;
            this._lastMaxLevel[(Int32)gaugeType] = sensor.MaxValue;

            if (!this._lastLevel[(Int32)gaugeType].Equals(sensor.Value))
            {
                this._lastLevel[(Int32)gaugeType] = sensor.Value;
                return true;
            }

            return false;
        }
        private Boolean UpdateMonitorIndex(LHMGaugeType monitorType)
        {
            if (!LibreHardwareMonitorPlugin.HardwareMonitor.TryGetSensorList(monitorType, out var sensorList))
            {
                return false;
            }

            var ret = false;
            foreach (var sensor in sensorList)
            {
                // Always update min/max so DrawGuage never divides by zero.
                this._lastMinLevel[(Int32)sensor.GaugeType] = sensor.MinValue;
                this._lastMaxLevel[(Int32)sensor.GaugeType] = sensor.MaxValue;

                if (!this._lastMonLevel[(Int32)sensor.GaugeType].Equals(sensor.Value))
                {
                    //PluginLog.Info($"{sensor.GaugeType}: " + sensor.Value);
                    this._lastMonLevel[(Int32)sensor.GaugeType] = sensor.Value;
                    ret = true;
                }
            }

            return ret;
        }

        private void OnHardwareMonitorProcessStarted(Object sender, EventArgs e) => this.UpdateParameters();

        private void OnHardwareMonitorProcessExited(Object sender, EventArgs e) => this.UpdateParameters();
    }
}
