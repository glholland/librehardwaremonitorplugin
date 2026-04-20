namespace NotADoctor99.LibreHardwareMonitorPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Net.Http;
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using System.Timers;

    using Loupedeck;

    using Microsoft.Win32;

    public sealed class LibreHardwareMonitor : IDisposable
    {
        private const String DataEndpointUrl = "http://localhost:8085/data.json";

        private readonly Timer _periodicTimer = new Timer();
        private readonly HttpClient _httpClient;

        private DateTime _nextSensorListRefreshUtc = DateTime.MinValue;

        public LibreHardwareMonitor()
        {
            this._periodicTimer.Interval = 500;
            this._periodicTimer.AutoReset = false; // prevent re-entrancy; restarted in finally
            this._periodicTimer.Elapsed += this.OnPeriodicTimerElapsed;

            this._httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMilliseconds(800),
            };
        }

        public void Dispose()
        {
            this._periodicTimer.Stop();
            this._periodicTimer.Elapsed -= this.OnPeriodicTimerElapsed;
            this._periodicTimer.Dispose();

            this._httpClient.Dispose();
        }

        public void StartMonitoring()
        {
            this._isRunning = LibreHardwareMonitor.IsRunning();

            if (this._isRunning)
            {
                this.GetAvailableSensors();
            }

            this._periodicTimer.Start();
        }

        public void StopMonitoring() => this._periodicTimer.Stop();

        // process
        private Boolean _isRunning = false;

        public event EventHandler<EventArgs> ProcessStarted;

        public event EventHandler<EventArgs> ProcessExited;

        private void OnPeriodicTimerElapsed(Object sender, ElapsedEventArgs e)
        {
            try
            {
                var isRunning = LibreHardwareMonitor.IsRunning();

                if (!this._isRunning && isRunning)
                {
                    this._isRunning = true;
                    this.ProcessStarted?.Invoke(this, new EventArgs());

                    this.GetAvailableSensors();
                }
                else if (this._isRunning && !isRunning)
                {
                    this._isRunning = false;
                    this.ClearSensors();

                    this.ProcessExited?.Invoke(this, new EventArgs());
                }
                else if (this._isRunning)
                {
                    // Keep the sensor list up to date because HTTP mode has no WMI change events.
                    if (DateTime.UtcNow >= this._nextSensorListRefreshUtc)
                    {
                        this.GetAvailableSensors();
                    }

                    this.UpdateSensorValues();
                }
            }
            finally
            {
                // Restart only after the callback completes — prevents concurrent executions.
                this._periodicTimer.Start();
            }
        }

        // sensors

        public Boolean IsMonitoringStarted { get; private set; }

        public event EventHandler<EventArgs> SensorListChanged;

        private readonly Dictionary<String, LibreHardwareMonitorSensor> _sensorsByName = new Dictionary<String, LibreHardwareMonitorSensor>(StringComparer.InvariantCultureIgnoreCase);
        private readonly Dictionary<String, LibreHardwareMonitorSensor> _sensorsById = new Dictionary<String, LibreHardwareMonitorSensor>(StringComparer.InvariantCultureIgnoreCase);
        private readonly Dictionary<String, LibreHardwareMonitorSensor> _sensorsByIdentifier = new Dictionary<String, LibreHardwareMonitorSensor>(StringComparer.InvariantCultureIgnoreCase);
        private readonly Dictionary<LHMGaugeType, LibreHardwareMonitorSensor> _sensorsByGaugeType = new Dictionary<LHMGaugeType, LibreHardwareMonitorSensor>();
        private readonly Dictionary<LHMGaugeType, List<LibreHardwareMonitorSensor>> _sensorListByGaugeType = new Dictionary<LHMGaugeType, List<LibreHardwareMonitorSensor>>();

        public IReadOnlyCollection<LibreHardwareMonitorSensor> Sensors => this._sensorsByName.Values;

        public event EventHandler<LibreHardwareMonitorSensorValuesChangedEventArgs> SensorValuesChanged;
        public event EventHandler<LibreHardwareMonitorGaugeValueChangedEventArgs> GaugeValuesChanged;
        public event EventHandler<LibreHardwareMonitorMonitorValueChangedEventArgs> MonitorValuesChanged;

        public Boolean TryGetSensor(String sensorName, out LibreHardwareMonitorSensor sensor)
        {
            sensor = null;
            return this._isRunning && this._sensorsByName.TryGetValueSafe(sensorName, out sensor);
        }

        public Boolean TryGetSensor(LHMGaugeType gaugeType, out LibreHardwareMonitorSensor sensor)
        {
            sensor = null;
            return this._isRunning && this._sensorsByGaugeType.TryGetValueSafe(gaugeType, out sensor);
        }

        public Boolean TryGetSensorList(LHMGaugeType gaugeType, out List<LibreHardwareMonitorSensor> sensorList)
        {
            sensorList = null;
            return this._isRunning && this._sensorListByGaugeType.TryGetValueSafe(gaugeType, out sensorList);
        }

        private void ClearSensors()
        {
            lock (this._sensorsByName)
            {
                this._sensorsByName.Clear();
                this._sensorsById.Clear();
                this._sensorsByIdentifier.Clear();
                this._sensorsByGaugeType.Clear();
                this._sensorListByGaugeType.Clear();
            }
        }

        private Int32 GetAvailableSensors()
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            // HTTP fetch outside the lock — avoids holding the lock during network I/O.
            if (!this.TryGetSensorSnapshots(out var snapshots))
            {
                PluginLog.Error($"Cannot get sensors list from {DataEndpointUrl}. Is LibreHardwareMonitor running with web server enabled?");
                return 0;
            }

            var newSensors = new List<LibreHardwareMonitorSensor>();
            try
            {

                    var defaultColor = new BitmapColor(255, 10, 90);
                    var intelColor = new BitmapColor(0, 170, 230);
                    var amdColor = new BitmapColor(255, 36, 36);
                    var nvidiaColor = new BitmapColor(100, 200, 60);
                    var cpuColor = intelColor;
                    var gpuColor = nvidiaColor;

                    var storageMap = new Dictionary<String, Int32>(StringComparer.InvariantCultureIgnoreCase);

                    foreach (var snapshot in snapshots)
                    {
                        if (!storageMap.ContainsKey(snapshot.HardwareId)
                            && this.GetHardwareType(snapshot.HardwareId).Equals("storage", StringComparison.OrdinalIgnoreCase))
                        {
                            storageMap[snapshot.HardwareId] = storageMap.Count + 1;
                        }

                        if (snapshot.HardwareId.IndexOf("intel", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (this.GetHardwareType(snapshot.HardwareId).Equals("cpu", StringComparison.OrdinalIgnoreCase))
                            {
                                cpuColor = intelColor;
                            }

                            if (this.GetHardwareType(snapshot.HardwareId).Equals("gpu", StringComparison.OrdinalIgnoreCase))
                            {
                                gpuColor = intelColor;
                            }
                        }
                        else if (snapshot.HardwareId.IndexOf("amd", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (this.GetHardwareType(snapshot.HardwareId).Equals("cpu", StringComparison.OrdinalIgnoreCase))
                            {
                                cpuColor = intelColor;
                            }

                            if (this.GetHardwareType(snapshot.HardwareId).Equals("gpu", StringComparison.OrdinalIgnoreCase))
                            {
                                gpuColor = amdColor;
                            }
                        }
                        else if (snapshot.HardwareId.IndexOf("nvidia", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (this.GetHardwareType(snapshot.HardwareId).Equals("cpu", StringComparison.OrdinalIgnoreCase))
                            {
                                cpuColor = nvidiaColor;
                            }

                            if (this.GetHardwareType(snapshot.HardwareId).Equals("gpu", StringComparison.OrdinalIgnoreCase))
                            {
                                gpuColor = nvidiaColor;
                            }
                        }
                    }

                    foreach (var snapshot in snapshots)
                    {
                        var hardwareType = this.GetHardwareType(snapshot.HardwareId);
                        var sensorType = snapshot.SensorType;
                        var displayName = snapshot.DisplayName;

                        if (displayName.IndexOf(" #", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            continue;
                        }

                        if (!this.TryCreateSensorDefinition(hardwareType, sensorType, storageMap, snapshot, out var parentName, out var formatString))
                        {
                            continue;
                        }

                        var maxValue = Helpers.MinMax((snapshot.Value + 25) * 2.0f, 50, 120);
                        var sensorColor = defaultColor;

                        this.ResolveGaugeMetadata(snapshot.SensorId, displayName, cpuColor, gpuColor, defaultColor, ref maxValue, ref sensorColor, out var gaugeType, out var monitorType);

                        var name = $"{parentName}-{sensorType}-{displayName}".Replace(' ', '.');
                        var itemFormatString = formatString.Replace("{-}", displayName);
                        var itemDisplayName = $"[{parentName} {sensorType}] {displayName}";
                        var instanceId = String.IsNullOrWhiteSpace(snapshot.InstanceId) ? snapshot.SensorId : snapshot.InstanceId;

                        var sensor = new LibreHardwareMonitorSensor(name, instanceId, snapshot.SensorId, itemDisplayName, itemFormatString, snapshot.Value, maxValue, gaugeType, monitorType, sensorColor);

                        newSensors.Add(sensor);
                        if (sensor.GaugeType != LHMGaugeType.None)
                        {
                            PluginLog.Info("[" + sensor.GaugeType + "] " + snapshot.SensorId + " | " + displayName + " | " + itemDisplayName + " | " + itemFormatString);
                        }
                    }
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "Error getting sensor list");
            }

            // Lock only for the dictionary update — keep it as short as possible.
            Int32 count;
            lock (this._sensorsByName)
            {
                this._sensorsByName.Clear();
                this._sensorsById.Clear();
                this._sensorsByIdentifier.Clear();
                this._sensorsByGaugeType.Clear();
                this._sensorListByGaugeType.Clear();

                foreach (var sensor in newSensors)
                {
                    if (this._sensorsByName.ContainsKey(sensor.Name) || this._sensorsById.ContainsKey(sensor.Id))
                    {
                        continue;
                    }

                    this._sensorsByName[sensor.Name] = sensor;
                    this._sensorsById[sensor.Id] = sensor;
                    this._sensorsByIdentifier[sensor.Identifier] = sensor;

                    if (sensor.GaugeType != LHMGaugeType.None && !this._sensorsByGaugeType.ContainsKey(sensor.GaugeType))
                    {
                        this._sensorsByGaugeType[sensor.GaugeType] = sensor;
                    }

                    if (sensor.MonitorType != LHMGaugeType.None)
                    {
                        if (!this._sensorListByGaugeType.ContainsKey(sensor.MonitorType))
                        {
                            this._sensorListByGaugeType[sensor.MonitorType] = new List<LibreHardwareMonitorSensor>();
                        }

                        this._sensorListByGaugeType[sensor.MonitorType].Add(sensor);
                    }
                }

                count = this._sensorsByName.Count;
            }

            stopwatch.Stop();
            PluginLog.Info($"{count}/{count}/{this._sensorsByGaugeType.Count} sensors found in {stopwatch.Elapsed.TotalMilliseconds:N0} ms");

            this._nextSensorListRefreshUtc = DateTime.UtcNow.AddSeconds(5);
            // Fire outside the lock — never call event handlers while holding a lock.
            this.SensorListChanged?.Invoke(this, new EventArgs());

            return count;
        }

        private readonly List<LHMGaugeType> _modifiedGaugeTypes = new List<LHMGaugeType>();
        private readonly List<LHMGaugeType> _modifiedMonitorTypes = new List<LHMGaugeType>();
        private readonly List<String> _modifiedSensorNames = new List<String>();

        private void UpdateSensorValues()
        {
            try
            {
                if (!this.TryGetSensorSnapshots(out var snapshots))
                {
                    return;
                }

                this._modifiedGaugeTypes.Clear();
                this._modifiedMonitorTypes.Clear();
                this._modifiedSensorNames.Clear();

                lock (this._sensorsByName)
                {
                    foreach (var snapshot in snapshots)
                    {
                        if (!this._sensorsByIdentifier.TryGetValue(snapshot.SensorId, out var sensor))
                        {
                            continue;
                        }

                        if (!sensor.SetValue(snapshot.Value))
                        {
                            continue;
                        }

                        if (sensor.GaugeType != LHMGaugeType.None)
                        {
                            this._modifiedGaugeTypes.Add(sensor.GaugeType);
                        }

                        if (sensor.MonitorType != LHMGaugeType.None)
                        {
                            this._modifiedMonitorTypes.Add(sensor.MonitorType);
                        }

                        this._modifiedSensorNames.Add(sensor.Name);
                    }
                }

                if (this._modifiedGaugeTypes.Count > 0)
                {
                    this.GaugeValuesChanged?.Invoke(this, new LibreHardwareMonitorGaugeValueChangedEventArgs(this._modifiedGaugeTypes));
                }

                if (this._modifiedMonitorTypes.Count > 0)
                {
                    this.MonitorValuesChanged?.Invoke(this, new LibreHardwareMonitorMonitorValueChangedEventArgs(this._modifiedMonitorTypes));
                }

                if (this._modifiedSensorNames.Count > 0)
                {
                    this.SensorValuesChanged?.Invoke(this, new LibreHardwareMonitorSensorValuesChangedEventArgs(this._modifiedSensorNames));
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "Error updating sensor values");
            }
        }

        private Boolean TryGetSensorSnapshots(out List<LibreHardwareMonitorSensorSnapshot> snapshots)
        {
            snapshots = new List<LibreHardwareMonitorSensorSnapshot>();

            try
            {
                var json = this._httpClient.GetStringAsync(DataEndpointUrl).ConfigureAwait(false).GetAwaiter().GetResult();
                using (var document = JsonDocument.Parse(json))
                {
                    if (!document.RootElement.TryGetProperty("Children", out var machineNodes) || machineNodes.ValueKind != JsonValueKind.Array)
                    {
                        return false;
                    }

                    foreach (var machineNode in machineNodes.EnumerateArray())
                    {
                        if (!machineNode.TryGetProperty("Children", out var hardwareNodes) || hardwareNodes.ValueKind != JsonValueKind.Array)
                        {
                            continue;
                        }

                        foreach (var hardwareNode in hardwareNodes.EnumerateArray())
                        {
                            var hardwareId = this.GetStringValue(hardwareNode, "HardwareId");
                            if (String.IsNullOrWhiteSpace(hardwareId))
                            {
                                continue;
                            }

                            var hardwareName = this.GetStringValue(hardwareNode, "Text");
                            this.CollectSensorsFromNode(hardwareNode, hardwareId, hardwareName, snapshots);
                        }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, $"Cannot fetch {DataEndpointUrl}");
                return false;
            }
        }

        private void CollectSensorsFromNode(JsonElement node, String hardwareId, String hardwareName, List<LibreHardwareMonitorSensorSnapshot> snapshots)
        {
            var sensorId = this.GetStringValue(node, "SensorId");
            if (!String.IsNullOrWhiteSpace(sensorId)
                && this.TryGetSingleValue(node, "RawValue", out var rawValue))
            {
                var sensorType = this.GetStringValue(node, "Type");
                var displayName = this.GetStringValue(node, "Text");
                var instanceId = this.GetStringValue(node, "id");

                snapshots.Add(new LibreHardwareMonitorSensorSnapshot
                {
                    HardwareId = hardwareId,
                    HardwareName = hardwareName,
                    SensorId = sensorId,
                    SensorType = sensorType,
                    DisplayName = displayName,
                    InstanceId = instanceId,
                    Value = rawValue,
                });
            }

            if (!node.TryGetProperty("Children", out var childNodes) || childNodes.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var childNode in childNodes.EnumerateArray())
            {
                this.CollectSensorsFromNode(childNode, hardwareId, hardwareName, snapshots);
            }
        }

        private String GetStringValue(JsonElement element, String propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var valueElement))
            {
                return String.Empty;
            }

            switch (valueElement.ValueKind)
            {
                case JsonValueKind.String:
                    return valueElement.GetString() ?? String.Empty;
                case JsonValueKind.Number:
                    return valueElement.ToString();
                default:
                    return String.Empty;
            }
        }

        private Boolean TryGetSingleValue(JsonElement element, String propertyName, out Single value)
        {
            if (!element.TryGetProperty(propertyName, out var valueElement))
            {
                value = 0;
                return false;
            }

            if (valueElement.ValueKind == JsonValueKind.Number)
            {
                if (valueElement.TryGetSingle(out value))
                {
                    return true;
                }

                if (valueElement.TryGetDouble(out var valueAsDouble))
                {
                    value = (Single)valueAsDouble;
                    return true;
                }
            }

            if (valueElement.ValueKind == JsonValueKind.String
                && Single.TryParse(valueElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }

            value = 0;
            return false;
        }

        private String GetHardwareType(String hardwareId)
        {
            if (String.IsNullOrWhiteSpace(hardwareId))
            {
                return String.Empty;
            }

            if (hardwareId.IndexOf("cpu", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "cpu";
            }

            if (hardwareId.IndexOf("/gpu", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "gpu";
            }

            if (hardwareId.IndexOf("/ram", StringComparison.OrdinalIgnoreCase) >= 0 || hardwareId.IndexOf("/vram", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "memory";
            }

            if (hardwareId.IndexOf("/nic", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "network";
            }

            if (hardwareId.IndexOf("battery", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "battery";
            }

            if (hardwareId.IndexOf("/nvme", StringComparison.OrdinalIgnoreCase) >= 0
                || hardwareId.IndexOf("/hdd", StringComparison.OrdinalIgnoreCase) >= 0
                || hardwareId.IndexOf("/ssd", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "storage";
            }

            return String.Empty;
        }

        private Boolean TryCreateSensorDefinition(
            String hardwareType,
            String sensorType,
            Dictionary<String, Int32> storageMap,
            LibreHardwareMonitorSensorSnapshot snapshot,
            out String parentName,
            out String formatString)
        {
            parentName = String.Empty;
            formatString = String.Empty;

            switch (hardwareType.ToLowerInvariant())
            {
                case "cpu":
                    parentName = "CPU";
                    formatString = sensorType switch
                    {
                        "Clock" => @"{-}\n{0:N1} MHz",
                        "Load" => @"{-}\n{0:N1} %",
                        "Power" => @"{-}\n{0:N1} W",
                        "Temperature" => @"{-}\n{0:N1} °C",
                        "Voltage" => @"{-}\n{0:N3} V",
                        _ => String.Empty,
                    };
                    return formatString.Length > 0;

                case "gpu":
                    parentName = "GPU";
                    formatString = sensorType switch
                    {
                        "Clock" => @"{-}\n{0:N1} MHz",
                        "Load" => @"{-}\n{0:N1} %",
                        "Power" => @"{-}\n{0:N1} W",
                        "Temperature" => @"{-}\n{0:N1} °C",
                        "SmallData" => @"{-}\n{0:N0} MB",
                        "Fan" => @"{-}\n{0:N0} RPM",
                        "Control" => @"{-}\n{0:N1} %",
                        _ => String.Empty,
                    };
                    return formatString.Length > 0;

                case "memory":
                    // Distinguish RAM (/ram) from Virtual Memory (/vram) so sensor names don't collide.
                    parentName = snapshot.HardwareId.IndexOf("/vram", StringComparison.OrdinalIgnoreCase) >= 0 ? "VirtMem" : "RAM";
                    formatString = sensorType switch
                    {
                        "Load" => @"{-}\n{0:N1} %",
                        "Data" => @"{-}\n{0:N1} GB",
                        _ => String.Empty,
                    };
                    return formatString.Length > 0;

                case "network":
                    // Include NIC name so multiple adapters don't collide on identical sensor captions.
                    parentName = String.IsNullOrWhiteSpace(snapshot.HardwareName) ? "Network" : $"Network {snapshot.HardwareName}";
                    formatString = sensorType switch
                    {
                        "Load" => @"{-}\n{0:N1} %",
                        "Data" => @"{-}\n{0:N1} GB",
                        "Throughput" => @"{-}\n{0:N1} KB/s",
                        _ => String.Empty,
                    };
                    return formatString.Length > 0;

                case "battery":
                    parentName = "Battery";
                    formatString = sensorType switch
                    {
                        "Level" => @"{-}\n{0:N1} %",
                        "Voltage" => @"{-}\n{0:N1} V",
                        _ => String.Empty,
                    };
                    return formatString.Length > 0;

                case "storage":
                    if (!storageMap.TryGetValue(snapshot.HardwareId, out var storageIndex))
                    {
                        storageIndex = 1;
                    }

                    parentName = "Storage " + storageIndex;
                    formatString = sensorType switch
                    {
                        "Temperature" => "Storage " + storageIndex + @"\n{0:N1} °C",
                        "Load" => @"{-}\n{0:N1} %",
                        _ => String.Empty,
                    };
                    return formatString.Length > 0;

                default:
                    return false;
            }
        }

        private void ResolveGaugeMetadata(
            String identifier,
            String displayName,
            BitmapColor cpuColor,
            BitmapColor gpuColor,
            BitmapColor defaultColor,
            ref Single maxValue,
            ref BitmapColor sensorColor,
            out LHMGaugeType gaugeType,
            out LHMGaugeType monitorType)
        {
            gaugeType = LHMGaugeType.None;
            monitorType = LHMGaugeType.None;

            if (identifier.IndexOf("cpu/", StringComparison.OrdinalIgnoreCase) != -1)
            {
                sensorColor = cpuColor;
                if (identifier.IndexOf("/power/0", StringComparison.OrdinalIgnoreCase) != -1)
                {
                    gaugeType = LHMGaugeType.CPU_Power;
                    monitorType = LHMGaugeType.Monitor_CPU;
                    maxValue = 120;
                }
                else if (identifier.IndexOf("/load/0", StringComparison.OrdinalIgnoreCase) != -1)
                {
                    gaugeType = LHMGaugeType.CPU_Load;
                    monitorType = LHMGaugeType.Monitor_CPU;
                    maxValue = 100;
                }
                else if (identifier.IndexOf("/temperature", StringComparison.OrdinalIgnoreCase) != -1)
                {
                    maxValue = 85;
                    if (identifier.IndexOf("/amdcpu", StringComparison.OrdinalIgnoreCase) != -1)
                    {
                        if (identifier.IndexOf("/temperature/2", StringComparison.OrdinalIgnoreCase) != -1)
                        {
                            gaugeType = LHMGaugeType.CPU_Package;
                        }
                        else if (identifier.IndexOf("/temperature/3", StringComparison.OrdinalIgnoreCase) != -1)
                        {
                            gaugeType = LHMGaugeType.CPU_Core;
                            monitorType = LHMGaugeType.Monitor_CPU;
                        }
                    }
                    else if (identifier.IndexOf("/intelcpu", StringComparison.OrdinalIgnoreCase) != -1)
                    {
                        if (identifier.IndexOf("/temperature/0", StringComparison.OrdinalIgnoreCase) != -1)
                        {
                            gaugeType = LHMGaugeType.CPU_Core;
                            monitorType = LHMGaugeType.Monitor_CPU;
                        }
                        else if (identifier.IndexOf("/temperature/1", StringComparison.OrdinalIgnoreCase) != -1)
                        {
                            gaugeType = LHMGaugeType.CPU_Package;
                        }
                    }
                    else if (identifier.IndexOf("/qualcommcpu", StringComparison.OrdinalIgnoreCase) != -1
                             && identifier.IndexOf("/temperature/0", StringComparison.OrdinalIgnoreCase) != -1)
                    {
                        // Qualcomm CPUs usually expose a single package-like temperature sensor.
                        gaugeType = LHMGaugeType.CPU_Package;
                        monitorType = LHMGaugeType.Monitor_CPU;
                    }
                    else if (identifier.IndexOf("/qualcommcpu", StringComparison.OrdinalIgnoreCase) != -1
                             && identifier.IndexOf("/temperature/1", StringComparison.OrdinalIgnoreCase) != -1)
                    {
                        gaugeType = LHMGaugeType.CPU_Core;
                        monitorType = LHMGaugeType.Monitor_CPU;
                    }
                }
            }
            else if (identifier.IndexOf("/gpu", StringComparison.OrdinalIgnoreCase) != -1)
            {
                sensorColor = gpuColor;
                if (identifier.IndexOf("/power/0", StringComparison.OrdinalIgnoreCase) != -1)
                {
                    gaugeType = LHMGaugeType.GPU_Power;
                    monitorType = LHMGaugeType.Monitor_GPU;
                    maxValue = 400;
                }
                else if (identifier.IndexOf("/load", StringComparison.OrdinalIgnoreCase) != -1)
                {
                    maxValue = 100;
                    if (identifier.IndexOf("/load/0", StringComparison.OrdinalIgnoreCase) != -1)
                    {
                        gaugeType = LHMGaugeType.GPU_Load;
                        monitorType = LHMGaugeType.Monitor_GPU;
                    }
                    else if (identifier.IndexOf("/load/3", StringComparison.OrdinalIgnoreCase) != -1
                            && displayName.IndexOf("Memory", StringComparison.OrdinalIgnoreCase) != -1)
                    {
                        gaugeType = LHMGaugeType.GPU_Memory_Load;
                        monitorType = LHMGaugeType.Monitor_Memory_Load;
                    }
                    else if (displayName.IndexOf("Memory", StringComparison.OrdinalIgnoreCase) != -1)
                    {
                        gaugeType = LHMGaugeType.GPU_Memory_Load;
                        monitorType = LHMGaugeType.Monitor_Memory_Load;
                    }
                }
                else if (identifier.IndexOf("/temperature/0", StringComparison.OrdinalIgnoreCase) != -1)
                {
                    gaugeType = LHMGaugeType.GPU_Core;
                    monitorType = LHMGaugeType.Monitor_GPU;
                    maxValue = 83;
                }
                else if (identifier.IndexOf("/temperature/2", StringComparison.OrdinalIgnoreCase) != -1)
                {
                    gaugeType = LHMGaugeType.GPU_Hotspot;
                    maxValue = 83;
                }
                else if (identifier.IndexOf("/smalldata/3", StringComparison.OrdinalIgnoreCase) != -1)
                {
                    gaugeType = LHMGaugeType.GPU_Memory;
                    monitorType = LHMGaugeType.Monitor_Memory;
                }
                else if (identifier.IndexOf("/smalldata/1", StringComparison.OrdinalIgnoreCase) != -1
                         && displayName.IndexOf("Used", StringComparison.OrdinalIgnoreCase) != -1)
                {
                    gaugeType = LHMGaugeType.GPU_Memory;
                    monitorType = LHMGaugeType.Monitor_Memory;
                }
            }
            else if (identifier.IndexOf("/ram/", StringComparison.OrdinalIgnoreCase) != -1)
            {
                sensorColor = cpuColor;
                if (identifier.IndexOf("/load/0", StringComparison.OrdinalIgnoreCase) != -1)
                {
                    gaugeType = LHMGaugeType.Memory_Load;
                    monitorType = LHMGaugeType.Monitor_Memory_Load;
                    maxValue = 100;
                }
                else if (identifier.IndexOf("/load/1", StringComparison.OrdinalIgnoreCase) != -1)
                {
                    gaugeType = LHMGaugeType.Virtual_Memory_Load;
                    monitorType = LHMGaugeType.Monitor_Memory_Load;
                    maxValue = 100;
                }
                else if (identifier.IndexOf("/data/0", StringComparison.OrdinalIgnoreCase) != -1)
                {
                    gaugeType = LHMGaugeType.Memory;
                    monitorType = LHMGaugeType.Monitor_Memory;
                }
                else if (identifier.IndexOf("/data/2", StringComparison.OrdinalIgnoreCase) != -1)
                {
                    gaugeType = LHMGaugeType.Virtual_Memory;
                    monitorType = LHMGaugeType.Monitor_Memory;
                }
            }
            else if (identifier.IndexOf("/vram/", StringComparison.OrdinalIgnoreCase) != -1)
            {
                // In LHM, /vram/ is always Windows Virtual Memory (page file), on every platform.
                // GPU VRAM sensors always appear under /gpu-nvidia/, /gpu-amd/, /gpu-qualcomm/, etc.
                // and are handled in the /gpu branch above.
                sensorColor = cpuColor;
                if (identifier.IndexOf("/load/1", StringComparison.OrdinalIgnoreCase) != -1)
                {
                    gaugeType = LHMGaugeType.Virtual_Memory_Load;
                    monitorType = LHMGaugeType.Monitor_Memory_Load;
                    maxValue = 100;
                }
                else if (identifier.IndexOf("/data/2", StringComparison.OrdinalIgnoreCase) != -1)
                {
                    gaugeType = LHMGaugeType.Virtual_Memory;
                    monitorType = LHMGaugeType.Monitor_Memory;
                }
            }
            else if (identifier.IndexOf("/nic/", StringComparison.OrdinalIgnoreCase) != -1)
            {
                sensorColor = defaultColor;
                if (identifier.IndexOf("/load/1", StringComparison.OrdinalIgnoreCase) != -1)
                {
                    // NIC utilization is a percentage value.
                    maxValue = 100;
                }
            }
            else if (identifier.IndexOf("/nvme/", StringComparison.OrdinalIgnoreCase) != -1
                    || identifier.IndexOf("/hdd/", StringComparison.OrdinalIgnoreCase) != -1
                    || identifier.IndexOf("/ssd/", StringComparison.OrdinalIgnoreCase) != -1)
            {
                sensorColor = defaultColor;

                var storageMatch = Regex.Match(identifier, @"^/(?:nvme|hdd|ssd)/(\d+)/(temperature|load)/(\d+)$", RegexOptions.IgnoreCase);
                if (storageMatch.Success && Int32.TryParse(storageMatch.Groups[1].Value, out var diskIndex))
                {
                    var kind = storageMatch.Groups[2].Value;
                    var metricIndex = storageMatch.Groups[3].Value;

                    if (kind.Equals("temperature", StringComparison.OrdinalIgnoreCase) && metricIndex.Equals("0", StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryMapStorageTempGauge(diskIndex, out gaugeType, out monitorType))
                        {
                            maxValue = 80;
                            return;
                        }
                    }

                    if (kind.Equals("load", StringComparison.OrdinalIgnoreCase)
                        && (metricIndex.Equals("0", StringComparison.OrdinalIgnoreCase)
                            || metricIndex.Equals("30", StringComparison.OrdinalIgnoreCase)
                            || metricIndex.Equals("53", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (TryMapStorageUsageGauge(diskIndex, out gaugeType, out monitorType))
                        {
                            maxValue = 100;
                            return;
                        }
                    }
                }

                if (identifier.IndexOf("/0/temperature/0", StringComparison.OrdinalIgnoreCase) != -1)
                {
                    gaugeType = LHMGaugeType.Storage_T_1;
                    monitorType = LHMGaugeType.Monitor_Storage_T_G1;
                    maxValue = 80;
                }
                else if (identifier.IndexOf("/1/temperature/0", StringComparison.OrdinalIgnoreCase) != -1)
                {
                    gaugeType = LHMGaugeType.Storage_T_2;
                    monitorType = LHMGaugeType.Monitor_Storage_T_G1;
                    maxValue = 80;
                }
                else if (identifier.IndexOf("/2/temperature/0", StringComparison.OrdinalIgnoreCase) != -1)
                {
                    gaugeType = LHMGaugeType.Storage_T_3;
                    monitorType = LHMGaugeType.Monitor_Storage_T_G1;
                    maxValue = 80;
                }
                else if (identifier.IndexOf("/3/temperature/0", StringComparison.OrdinalIgnoreCase) != -1)
                {
                    gaugeType = LHMGaugeType.Storage_T_4;
                    monitorType = LHMGaugeType.Monitor_Storage_T_G2;
                    maxValue = 80;
                }
                else if (identifier.IndexOf("/4/temperature/0", StringComparison.OrdinalIgnoreCase) != -1)
                {
                    gaugeType = LHMGaugeType.Storage_T_4;
                    monitorType = LHMGaugeType.Monitor_Storage_T_G2;
                    maxValue = 80;
                }
                else if (identifier.IndexOf("/5/temperature/0", StringComparison.OrdinalIgnoreCase) != -1)
                {
                    gaugeType = LHMGaugeType.Storage_T_5;
                    monitorType = LHMGaugeType.Monitor_Storage_T_G2;
                    maxValue = 80;
                }
                else if (identifier.IndexOf("/0/load/0", StringComparison.OrdinalIgnoreCase) != -1)
                {
                    gaugeType = LHMGaugeType.Storage_U_1;
                    monitorType = LHMGaugeType.Monitor_Storage_U_G1;
                    maxValue = 100;
                }
                else if (identifier.IndexOf("/1/load/0", StringComparison.OrdinalIgnoreCase) != -1)
                {
                    gaugeType = LHMGaugeType.Storage_U_2;
                    monitorType = LHMGaugeType.Monitor_Storage_U_G1;
                    maxValue = 100;
                }
                else if (identifier.IndexOf("/2/load/0", StringComparison.OrdinalIgnoreCase) != -1)
                {
                    gaugeType = LHMGaugeType.Storage_U_3;
                    monitorType = LHMGaugeType.Monitor_Storage_U_G1;
                    maxValue = 100;
                }
                else if (identifier.IndexOf("/3/load/0", StringComparison.OrdinalIgnoreCase) != -1)
                {
                    gaugeType = LHMGaugeType.Storage_U_4;
                    monitorType = LHMGaugeType.Monitor_Storage_U_G2;
                    maxValue = 100;
                }
                else if (identifier.IndexOf("/4/load/0", StringComparison.OrdinalIgnoreCase) != -1)
                {
                    gaugeType = LHMGaugeType.Storage_U_4;
                    monitorType = LHMGaugeType.Monitor_Storage_U_G2;
                    maxValue = 100;
                }
                else if (identifier.IndexOf("/5/load/0", StringComparison.OrdinalIgnoreCase) != -1)
                {
                    gaugeType = LHMGaugeType.Storage_U_5;
                    monitorType = LHMGaugeType.Monitor_Storage_U_G2;
                    maxValue = 100;
                }
            }
                else if (identifier.IndexOf("/battery/", StringComparison.OrdinalIgnoreCase) != -1
                    && identifier.IndexOf("/level/0", StringComparison.OrdinalIgnoreCase) != -1
                    && displayName.Equals("Charge Level", StringComparison.OrdinalIgnoreCase))
            {
                gaugeType = LHMGaugeType.Battery;
                maxValue = 100;
            }
        }

        private static Boolean TryMapStorageTempGauge(Int32 diskIndex, out LHMGaugeType gaugeType, out LHMGaugeType monitorType)
        {
            gaugeType = LHMGaugeType.None;
            monitorType = LHMGaugeType.None;

            switch (diskIndex)
            {
                case 0:
                    gaugeType = LHMGaugeType.Storage_T_1;
                    monitorType = LHMGaugeType.Monitor_Storage_T_G1;
                    return true;
                case 1:
                    gaugeType = LHMGaugeType.Storage_T_2;
                    monitorType = LHMGaugeType.Monitor_Storage_T_G1;
                    return true;
                case 2:
                    gaugeType = LHMGaugeType.Storage_T_3;
                    monitorType = LHMGaugeType.Monitor_Storage_T_G1;
                    return true;
                case 3:
                case 4:
                    gaugeType = LHMGaugeType.Storage_T_4;
                    monitorType = LHMGaugeType.Monitor_Storage_T_G2;
                    return true;
                case 5:
                    gaugeType = LHMGaugeType.Storage_T_5;
                    monitorType = LHMGaugeType.Monitor_Storage_T_G2;
                    return true;
                default:
                    return false;
            }
        }

        private static Boolean TryMapStorageUsageGauge(Int32 diskIndex, out LHMGaugeType gaugeType, out LHMGaugeType monitorType)
        {
            gaugeType = LHMGaugeType.None;
            monitorType = LHMGaugeType.None;

            switch (diskIndex)
            {
                case 0:
                    gaugeType = LHMGaugeType.Storage_U_1;
                    monitorType = LHMGaugeType.Monitor_Storage_U_G1;
                    return true;
                case 1:
                    gaugeType = LHMGaugeType.Storage_U_2;
                    monitorType = LHMGaugeType.Monitor_Storage_U_G1;
                    return true;
                case 2:
                    gaugeType = LHMGaugeType.Storage_U_3;
                    monitorType = LHMGaugeType.Monitor_Storage_U_G1;
                    return true;
                case 3:
                case 4:
                    gaugeType = LHMGaugeType.Storage_U_4;
                    monitorType = LHMGaugeType.Monitor_Storage_U_G2;
                    return true;
                case 5:
                    gaugeType = LHMGaugeType.Storage_U_5;
                    monitorType = LHMGaugeType.Monitor_Storage_U_G2;
                    return true;
                default:
                    return false;
            }
        }

        // static methods

        public static Boolean IsInstalled() => LibreHardwareMonitor.TryFindExecutableFilePath(out _);

        public static Boolean IsRunning() => LibreHardwareMonitor.TryGetProcesses(out _);

        public static Boolean Run()
        {
            try
            {
                if (LibreHardwareMonitor.IsRunning())
                {
                    return true;
                }

                if (LibreHardwareMonitor.TryFindExecutableFilePath(out var executableFilePath))
                {
                    Process.Start(executableFilePath);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "Error starting LibreHardwareMonitor");
                return false;
            }
        }

        public static Boolean Activate()
        {
            try
            {
                if (LibreHardwareMonitor.TryGetProcesses(out var processes))
                {
                    foreach (var process in processes)
                    {
                        NativeMethods.SetForegroundWindow(process.MainWindowHandle);
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "Error activating LibreHardwareMonitor");
            }

            return false;
        }

        public static Boolean ActivateOrRun()
        {
            return LibreHardwareMonitor.IsRunning()
                ? LibreHardwareMonitor.Activate()
                : LibreHardwareMonitor.IsInstalled() ? LibreHardwareMonitor.Run() : false;
        }

        // static helpers

        private const String ExecutableFileName = "LibreHardwareMonitor.exe";
        private const String ProcessName = "LibreHardwareMonitor";

        private static Boolean TryGetProcesses(out Process[] processes)
        {
            try
            {
                processes = Process.GetProcessesByName(LibreHardwareMonitor.ProcessName);
                if (processes.Length > 0)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "Error getting LibreHardwareMonitor process");
            }

            processes = null;
            return false;
        }

        private static Boolean TryFindExecutableFilePath(out String executableFilePath)
        {
            try
            {
                using (var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
                {
                    using (var appSwitched = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\AppSwitched"))
                    {
                        foreach (var appSwitchedFilePath in appSwitched.GetValueNames())
                        {
                            if (appSwitchedFilePath.EndsWith(LibreHardwareMonitor.ExecutableFileName, StringComparison.InvariantCultureIgnoreCase) && File.Exists(appSwitchedFilePath))
                            {
                                executableFilePath = appSwitchedFilePath;
                                return true;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "Error finding LibreHardwareMonitor executable in Registry");
            }

            var programFilesDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            executableFilePath = Path.Combine(programFilesDirectory, "NotADoctor99", "Libre Hardware Monitor", "LibreHardwareMonitor.exe");

            if (File.Exists(executableFilePath))
            {
                return true;
            }

            PluginLog.Warning("Cannot find LibreHardwareMonitor executable");
            return false;
        }

        private sealed class LibreHardwareMonitorSensorSnapshot
        {
            public String HardwareId { get; set; }

            public String HardwareName { get; set; }

            public String SensorId { get; set; }

            public String SensorType { get; set; }

            public String DisplayName { get; set; }

            public String InstanceId { get; set; }

            public Single Value { get; set; }
        }
    }
}
