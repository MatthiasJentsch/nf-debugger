﻿//
// Copyright (c) 2017 The nanoFramework project contributors
// See LICENSE file in the project root for full license information.
//
using nanoFramework.Tools.Debugger.Extensions;
using nanoFramework.Tools.Debugger.Serial;
using nanoFramework.Tools.Debugger.WireProtocol;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.SerialCommunication;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace nanoFramework.Tools.Debugger.PortSerial
{
    public partial class SerialPort : PortBase, IPort
    {
        // dictionary with mapping between Serial device watcher and the device ID
        private Dictionary<DeviceWatcher, string> _mapDeviceWatchersToDeviceSelector;

        // Serial device watchers suspended flag
        private bool _watchersSuspended = false;

        // Serial device watchers started flag
        private bool _watchersStarted = false;

        // counter of device watchers completed
        private int _deviceWatchersCompletedCount = 0;

        /// <summary>
        /// Internal list with the actual nF Serial devices
        /// </summary>
        List<SerialDeviceInformation> _serialDevices;

        /// <summary>
        /// Internal list of the tentative devices to be checked as valid nanoFramework devices
        /// </summary>
        private List<NanoDeviceBase> _tentativeNanoFrameworkDevices = new List<NanoDeviceBase>();

        /// <summary>
        /// Creates an Serial debug client
        /// </summary>
        public SerialPort(object callerApp)
        {
            _mapDeviceWatchersToDeviceSelector = new Dictionary<DeviceWatcher, String>();
            NanoFrameworkDevices = new ObservableCollection<NanoDeviceBase>();
            _serialDevices = new List<Serial.SerialDeviceInformation>();

            // set caller app property, if any
            if (callerApp != null)
            {

#if WINDOWS_UWP
                EventHandlerForSerialDevice.CallerApp = callerApp as Windows.UI.Xaml.Application;
#else
                EventHandlerForSerialDevice.CallerApp = callerApp as System.Windows.Application;
#endif
            };

            Task.Factory.StartNew(() => {
                StartSerialDeviceWatchers();
            });
        }


        #region Device watchers initialization

        /*////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
        Add a device watcher initialization method for each supported device that should be watched.
        That initialization method must be called from the InitializeDeviceWatchers() method above so the watcher is actually started.
        ///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////*/

        /// <summary>
        /// Registers for Added, Removed, and Enumerated events on the provided deviceWatcher before adding it to an internal list.
        /// </summary>
        /// <param name="deviceWatcher">The device watcher to subscribe the events</param>
        /// <param name="deviceSelector">The AQS used to create the device watcher</param>
        private void AddDeviceWatcher(DeviceWatcher deviceWatcher, String deviceSelector)
        {
            deviceWatcher.Added += new TypedEventHandler<DeviceWatcher, DeviceInformation>(OnDeviceAdded);
            deviceWatcher.Removed += new TypedEventHandler<DeviceWatcher, DeviceInformationUpdate>(OnDeviceRemoved);
            deviceWatcher.EnumerationCompleted += new TypedEventHandler<DeviceWatcher, object>(OnDeviceEnumerationCompleteAsync);

            _mapDeviceWatchersToDeviceSelector.Add(deviceWatcher, deviceSelector);
        }

        #endregion


        #region Device watcher management and host app status handling

        /// <summary>
        /// Initialize device watchers. Must call here the initialization methods for all devices that we want to set watch.
        /// </summary>
        private void InitializeDeviceWatchers()
        {
            // Target all Serial Devices present on the system
            var deviceSelector = SerialDevice.GetDeviceSelector();

            // Other variations of GetDeviceSelector() usage are commented for reference
            //
            // Target a specific Serial Device using its VID and PID 
            // var deviceSelector = SerialDevice.GetDeviceSelectorFromUsbVidPid(0x2341, 0x0043);
            //
            // Target a specific Serial Device by its COM PORT Name - "COM3"
            // var deviceSelector = SerialDevice.GetDeviceSelector("COM3");
            //
            // Target a specific UART based Serial Device by its COM PORT Name (usually defined in ACPI) - "UART1"
            // var deviceSelector = SerialDevice.GetDeviceSelector("UART1");
            //

            // Create a device watcher to look for instances of the Serial Device that match the device selector
            // used earlier.

            var deviceWatcher = DeviceInformation.CreateWatcher(deviceSelector);

            // Allow the EventHandlerForDevice to handle device watcher events that relates or effects our device (i.e. device removal, addition, app suspension/resume)
            AddDeviceWatcher(deviceWatcher, deviceSelector);
        }

        public void StartSerialDeviceWatchers()
        {
            // Initialize the Serial device watchers to be notified when devices are connected/removed
            InitializeDeviceWatchers();
            StartDeviceWatchers();
        }

        /// <summary>
        /// Starts all device watchers including ones that have been individually stopped.
        /// </summary>
        private void StartDeviceWatchers()
        {
            // Start all device watchers
            _watchersStarted = true;
            _deviceWatchersCompletedCount = 0;
            IsDevicesEnumerationComplete = false;

            foreach (DeviceWatcher deviceWatcher in _mapDeviceWatchersToDeviceSelector.Keys)
            {
                if ((deviceWatcher.Status != DeviceWatcherStatus.Started)
                    && (deviceWatcher.Status != DeviceWatcherStatus.EnumerationCompleted))
                {
                    deviceWatcher.Start();
                }
            }
        }

        /// <summary>
        /// Should be called on host app OnAppSuspension() event to properly handle that status.
        /// The DeviceWatchers must be stopped because device watchers will continue to raise events even if
        /// the app is in suspension, which is not desired (drains battery). The device watchers will be resumed once the app resumes too.
        /// </summary>
        public void AppSuspending()
        {
            if (_watchersStarted)
            {
                _watchersSuspended = true;
                StopDeviceWatchers();
            }
            else
            {
                _watchersSuspended = false;
            }
        }

        /// <summary>
        /// Should be called on host app OnAppResume() event to properly handle that status.
        /// See AppSuspending for why we are starting the device watchers again.
        /// </summary>
        public void AppResumed()
        {
            if (_watchersSuspended)
            {
                _watchersSuspended = false;
                StartDeviceWatchers();
            }
        }

        /// <summary>
        /// Stops all device watchers.
        /// </summary>
        private void StopDeviceWatchers()
        {
            // Stop all device watchers
            foreach (DeviceWatcher deviceWatcher in _mapDeviceWatchersToDeviceSelector.Keys)
            {
                if ((deviceWatcher.Status == DeviceWatcherStatus.Started)
                    || (deviceWatcher.Status == DeviceWatcherStatus.EnumerationCompleted))
                {
                    deviceWatcher.Stop();
                }
            }

            // Clear the list of devices so we don't have potentially disconnected devices around
            ClearDeviceEntries();

            _watchersStarted = false;
        }

        #endregion


        #region Methods to manage device list add, remove, etc

        /// <summary>
        /// Creates a DeviceListEntry for a device and adds it to the list of devices
        /// </summary>
        /// <param name="deviceInformation">DeviceInformation on the device to be added to the list</param>
        /// <param name="deviceSelector">The AQS used to find this device</param>
        private async void AddDeviceToList(DeviceInformation deviceInformation, String deviceSelector)
        {
            // discard known system and unusable devices
            if (
               deviceInformation.Id.StartsWith(@"\\?\ACPI") ||

               // reported in https://github.com/nanoframework/Home/issues/332
               // COM ports from Broadcom 20702 Bluetooth adapter
               deviceInformation.Id.Contains(@"VID_0A5C+PID_21E1")
               
               )
            {
                // don't even bother with these
                return;
            }

            // search the device list for a device with a matching interface ID
            var serialMatch = FindDevice(deviceInformation.Id);

            // Add the device if it's new
            if (serialMatch == null)
            {
                var serialDevice = new SerialDeviceInformation(deviceInformation, deviceSelector);
                _serialDevices.Add(serialDevice);

                Debug.WriteLine("New Serial device: " + deviceInformation.Id);

                // search the nanoFramework device list for a device with a matching interface ID
                var nanoFrameworkDeviceMatch = FindNanoFrameworkDevice(deviceInformation.Id);

                if (nanoFrameworkDeviceMatch == null)
                {
                    // Create a new element for this device and...
                    var newNanoFrameworkDevice = new NanoDevice<NanoSerialDevice>();
                    newNanoFrameworkDevice.Device.DeviceInformation = new SerialDeviceInformation(deviceInformation, deviceSelector);
                    newNanoFrameworkDevice.Parent = this;
                    newNanoFrameworkDevice.Transport = TransportType.Serial;

                    // ... add it to the collection of tentative devices
                    _tentativeNanoFrameworkDevices.Add(newNanoFrameworkDevice as NanoDeviceBase);

                    // perform check for valid nanoFramework device is this is not the initial enumeration
                    if (IsDevicesEnumerationComplete)
                    {
                        // try opening the device to check for a valid nanoFramework device
                        if (await ConnectSerialDeviceAsync(newNanoFrameworkDevice.Device.DeviceInformation))
                        {
                            // hack to get the port name here
                            newNanoFrameworkDevice.Description = EventHandlerForSerialDevice.Current.Device.PortName;

                            if (await CheckValidNanoFrameworkSerialDeviceAsync(newNanoFrameworkDevice.Device.DeviceInformation))
                            {
                                // the device info was updated above, need to get it from the tentative devices collection

                                //add device to the collection
                                NanoFrameworkDevices.Add(FindNanoFrameworkDevice(newNanoFrameworkDevice.Device.DeviceInformation.DeviceInformation.Id));
                                Debug.WriteLine($"New Serial device: {newNanoFrameworkDevice.Description} {newNanoFrameworkDevice.Device.DeviceInformation.DeviceInformation.Id}");

                                // done here, clear tentative list
                                _tentativeNanoFrameworkDevices.Clear();

                                // done here
                                return;
                            }
                            else
                            {
                                Debug.WriteLine("Invalid serial device: " + deviceInformation.Id);
                                newNanoFrameworkDevice.Disconnect();
                            }
                        }
                        else
                        {
                            Debug.WriteLine("Couldn't connect to serial device: " + deviceInformation.Id);
                            newNanoFrameworkDevice.Disconnect();
                        }

                        // clear tentative list
                        _tentativeNanoFrameworkDevices.Clear();

                        Debug.WriteLine("Serial device removed: " + deviceInformation.Id);

                        _serialDevices.Remove(serialDevice);
                    }
                }
            }
        }

        private void RemoveDeviceFromList(string deviceId)
        {
            Debug.WriteLine("trying to find serial device: " + deviceId);

            // Removes the device entry from the internal list; therefore the UI
            var deviceEntry = FindDevice(deviceId);

            Debug.WriteLine("Serial device removed: " + deviceId);

            _serialDevices.Remove(deviceEntry);

            // get device...
            var device = FindNanoFrameworkDevice(deviceId);

            // ... and remove it from collection
            NanoFrameworkDevices.Remove(device);

            device?.DebugEngine?.Disconnect();
            device?.DebugEngine?.Dispose();
        }

        private void ClearDeviceEntries()
        {
            _serialDevices.Clear();
        }

        /// <summary>
        /// Searches through the existing list of devices for the first DeviceListEntry that has
        /// the specified device Id.
        /// </summary>
        /// <param name="deviceId">Id of the device that is being searched for</param>
        /// <returns>DeviceListEntry that has the provided Id; else a nullptr</returns>
        private SerialDeviceInformation FindDevice(String deviceId)
        {
            if (deviceId != null)
            {
                foreach (SerialDeviceInformation entry in _serialDevices)
                {
                    if (entry.DeviceInformation.Id == deviceId)
                    {
                        return entry;
                    }
                }
            }

            return null;
        }

        private NanoDeviceBase FindNanoFrameworkDevice(string deviceId)
        {
            if (deviceId != null)
            {
                // SerialMatch.Device.DeviceInformation
                var device = NanoFrameworkDevices.FirstOrDefault(d => ((d as NanoDevice<NanoSerialDevice>).Device.DeviceInformation as SerialDeviceInformation).DeviceInformation.Id == deviceId);

                if (device == null)
                {
                    // try now in tentative list
                    return _tentativeNanoFrameworkDevices.FirstOrDefault(d => ((d as NanoDevice<NanoSerialDevice>).Device.DeviceInformation as SerialDeviceInformation).DeviceInformation.Id == deviceId);
                }
                else
                {
                    return device;
                }
            }

            return null;
        }

        /// <summary>
        /// Remove the device from the device list 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="deviceInformationUpdate"></param>
        private void OnDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate deviceInformationUpdate)
        {
            RemoveDeviceFromList(deviceInformationUpdate.Id);
        }

        /// <summary>
        /// This function will add the device to the listOfDevices
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="deviceInformation"></param>
        private void OnDeviceAdded(DeviceWatcher sender, DeviceInformation deviceInformation)
        {
            AddDeviceToList(deviceInformation, _mapDeviceWatchersToDeviceSelector[sender]);
        }

        #endregion


        #region Handlers and events for Device Enumeration Complete 

        private async void OnDeviceEnumerationCompleteAsync(DeviceWatcher sender, object args)
        {
            // add another device watcher completed
            _deviceWatchersCompletedCount++;

            if (_deviceWatchersCompletedCount == _mapDeviceWatchersToDeviceSelector.Count)
            {
                // prepare a list of devices that are to be removed if they are deemed as not valid nanoFramework devices
                var devicesToRemove = new List<NanoDeviceBase>();

                foreach (NanoDeviceBase device in _tentativeNanoFrameworkDevices)
                {
                    Debug.WriteLine($"Checking device: {(((NanoDevice<NanoSerialDevice>)device).Device.DeviceInformation.DeviceInformation.Id)}");

                    // connect to the device (as Task to get rid of the await)
                    var connectResult = await ConnectDeviceAsync(device).ConfigureAwait(true);

                    if (connectResult)
                    {
                        var nFDeviceIsValid = await CheckValidNanoFrameworkSerialDeviceAsync(((NanoDevice<NanoSerialDevice>)device).Device.DeviceInformation).ConfigureAwait(true);

                        if (nFDeviceIsValid)
                        {
                            Debug.WriteLine($"New Serial device: {device.Description} {(((NanoDevice<NanoSerialDevice>)device).Device.DeviceInformation.DeviceInformation.Id)}");

                            NanoFrameworkDevices.Add(device);
                        }
                        else
                        {
                            ((NanoDevice<NanoSerialDevice>)device).Disconnect();
                        }
                    }
                    else
                    {
                        // couldn't open device
                        Debug.WriteLine($"Checking device: {(((NanoDevice<NanoSerialDevice>)device).Device.DeviceInformation.DeviceInformation.Id)} FAILED");
                        ((NanoDevice<NanoSerialDevice>)device).Disconnect();
                    }
                }

                // all watchers have completed enumeration
                IsDevicesEnumerationComplete = true;

                // clean list of tentative nanoFramework Devices
                _tentativeNanoFrameworkDevices.Clear();

                Debug.WriteLine($"Serial device enumeration completed. Found {NanoFrameworkDevices.Count} devices");

                // fire event that Serial enumeration is complete 
                OnDeviceEnumerationCompleted();
            }
        }

        private async Task<bool> CheckValidNanoFrameworkSerialDeviceAsync(SerialDeviceInformation deviceInformation)
        {
            // get name
            var name = deviceInformation.DeviceInformation.Name;
            var serialNumber = GetSerialNumber(deviceInformation.DeviceInformation.Id);

            if (serialNumber != null && serialNumber.Contains("NANO_"))
            {
                var device = FindNanoFrameworkDevice(deviceInformation.DeviceInformation.Id);

                if (device != null)
                {
                    device.Description = serialNumber + " @ " + device.Description;

                    // should be a valid nanoFramework device, done here
                    return true;
                }
                else
                {
                    Debug.WriteLine($"Couldn't find nano device {EventHandlerForSerialDevice.Current.DeviceInformation.Id} with serial {serialNumber}");
                }
            }
            else
            {
                // need an extra check on this because this can be 'just' a regular COM port without any nanoFramework device behind

                // fill in description for this device
                var device = FindNanoFrameworkDevice(deviceInformation.DeviceInformation.Id);

                // need an extra check on this because this can be 'just' a regular COM port without any nanoFramework device behind
                var connectionResult = await PingDeviceLocalAsync();

                if (connectionResult)
                {
                    // should be a valid nanoFramework device
                    device.Description = name + " @ " + device.Description;

                    // done here
                    return true;
                }
                else
                {
                    // doesn't look like a nanoFramework device
                    return false;
                }

            }

            // default to false
            return false;
        }

        private async Task<bool> PingDeviceLocalAsync()
        {
            try
            {
                // fake Ping header
                byte[] pingHeader = new byte[] {
                78,
                70,
                80,
                75,
                84,
                86,
                49,
                0,
                240,
                240,
                187,
                218,
                148,
                185,
                67,
                183,
                0,
                0,
                0,
                0,
                191,
                130,
                0,
                0,
                0,
                32,
                0,
                0,
                8,
                0,
                0,
                0,
            };

                // fake Ping payload
                byte[] pingPayload = new byte[] {
                0x02,
                0x00,
                0x00,
                0x00,
                0x00,
                0x00,
                0x00,
                0x00,
            };

                var cts = new CancellationTokenSource();

                await SendBufferAsync(pingHeader, new TimeSpan(0, 0, 2), cts.Token);

                await SendBufferAsync(pingPayload, new TimeSpan(0, 0, 2), cts.Token);

                byte[] pingResponseHeader = await ReadBufferAsync(32, new TimeSpan(0, 0, 1), cts.Token);

                return (pingResponseHeader.Length == 32);
            }
            catch { }

            // default to false
            return false;
        }

        protected virtual void OnDeviceEnumerationCompleted()
        {
            DeviceEnumerationCompleted?.Invoke(this, new EventArgs());
        }

        /// <summary>
        /// Event that is raised when enumeration of all watched devices is complete.
        /// </summary>
        public override event EventHandler DeviceEnumerationCompleted;

        #endregion


        public async Task<bool> ConnectDeviceAsync(NanoDeviceBase device)
        {
            if (await ConnectSerialDeviceAsync((device as NanoDevice<NanoSerialDevice>).Device.DeviceInformation as SerialDeviceInformation, device.DeviceBase as SerialDevice))
            {
                if (device.DeviceBase == null)
                {
                    // save for later
                    device.DeviceBase = EventHandlerForSerialDevice.Current.Device;

                    // update the description only if it's empty
                    if (string.IsNullOrEmpty(device.Description))
                    {
                        // hack to get the port name here
                        device.Description = EventHandlerForSerialDevice.Current.Device.PortName;
                    }
                }
            }

            return await ConnectSerialDeviceAsync((device as NanoDevice<NanoSerialDevice>).Device.DeviceInformation as SerialDeviceInformation);
        }

        private async Task<bool> ConnectSerialDeviceAsync(SerialDeviceInformation serialDeviceInfo, SerialDevice existingDevice = null)
        {
            // try to determine if we already have this device opened.
            if (EventHandlerForSerialDevice.Current.Device != null)
            {
                // device matches
                if (EventHandlerForSerialDevice.Current.DeviceInformation == serialDeviceInfo.DeviceInformation)
                {
                    return true;
                }
            }

            return await EventHandlerForSerialDevice.Current.OpenDeviceAsync(serialDeviceInfo.DeviceInformation, serialDeviceInfo.DeviceSelector, existingDevice);
        }

        public void DisconnectDevice(NanoDeviceBase device)
        {
            if (FindDevice(((device as NanoDevice<NanoSerialDevice>).Device.DeviceInformation as SerialDeviceInformation).DeviceInformation.Id) != null)
            {
                // remove SerialDevice from NanoDeviceBase
                device.DeviceBase = null;

                EventHandlerForSerialDevice.Current.CloseDevice();
            }
        }

        public static string GetSerialNumber(string value)
        {
            // typical ID string is \\?\USB#VID_0483&PID_5740#NANO_3267335D#{86e0d1e0-8089-11d0-9ce4-08003e301f73}

            int startIndex = value.IndexOf("USB");

            int endIndex = value.LastIndexOf("#");

            // sanity check
            if (startIndex < 0 || endIndex < 0)
            {
                return null;
            }

            // get device ID portion
            var deviceIDCollection = value.Substring(startIndex, endIndex - startIndex).Split('#');

            return deviceIDCollection?.GetValue(2) as string;
        }

        #region Interface implementations

        public DateTime LastActivity { get; set; }

        public void DisconnectDevice(SerialDevice device)
        {
            EventHandlerForSerialDevice.Current.CloseDevice();
        }

        public async Task<uint> SendBufferAsync(byte[] buffer, TimeSpan waiTimeout, CancellationToken cancellationToken)
        {
            // device must be connected
            if (EventHandlerForSerialDevice.Current.IsDeviceConnected && !cancellationToken.IsCancellationRequested)
            {
                DataWriter outputStreamWriter = new DataWriter(EventHandlerForSerialDevice.Current.Device.OutputStream);

                try
                {
                    // write buffer to device
                    outputStreamWriter.WriteBytes(buffer);

                    Task<UInt32> storeAsyncTask = outputStreamWriter.StoreAsync().AsTask(cancellationToken.AddTimeout(waiTimeout));

                    return await storeAsyncTask;
                }
                catch (TaskCanceledException)
                {
                    // this is expected to happen, don't do anything with this
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"SendRawBufferAsync-Serial-Exception occurred: {ex.Message}\r\n {ex.StackTrace}");
                    throw ex;
                }
                finally
                {
                    // detach stream
                    outputStreamWriter?.DetachStream();
                    outputStreamWriter = null;
                }
            }
            else
            {
                throw new DeviceNotConnectedException();
            }

            return 0;
        }

        public async Task<byte[]> ReadBufferAsync(uint bytesToRead, TimeSpan waiTimeout, CancellationToken cancellationToken)
        {
            // device must be connected
            if (EventHandlerForSerialDevice.Current.IsDeviceConnected && !cancellationToken.IsCancellationRequested)
            {
                DataReader inputStreamReader = new DataReader(EventHandlerForSerialDevice.Current.Device.InputStream);

                try
                {
                    Task<UInt32> loadAsyncTask = inputStreamReader.LoadAsync(bytesToRead).AsTask(cancellationToken.AddTimeout(waiTimeout));

                    UInt32 bytesRead = await loadAsyncTask;

                    if (bytesRead > 0)
                    {
                        byte[] readBuffer = new byte[bytesRead];
                        inputStreamReader?.ReadBytes(readBuffer);

                        return readBuffer;
                    }
                }
                catch (TaskCanceledException)
                {
                    // this is expected to happen, don't do anything with it
                }
                catch (NullReferenceException)
                {
                    // this is expected to happen when there is anything to read, don't do anything with it
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ReadBufferAsync-Serial-Exception occurred: {ex.Message}\r\n {ex.StackTrace}");
                    throw ex;
                }
                finally
                {
                    // detach stream
                    inputStreamReader?.DetachStream();
                    inputStreamReader = null;
                }
            }
            else
            {
                Debug.WriteLine("NotifyDeviceNotConnected");
                throw new DeviceNotConnectedException();
            }

            // return empty byte array
            return new byte[0];
        }

        #endregion
    }
}
