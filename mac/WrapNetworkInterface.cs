using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Management;
using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;

namespace mac {
    public class WrapNetworkInterface : INotifyPropertyChanged {
        private NetworkInterface _networkInterface;

        public event PropertyChangedEventHandler PropertyChanged;

        private readonly ManagementObject _managementObject;
        private long _lastBytesReceived, _lastBytesSent;
        private DateTime _lastRate, _lastRestart;
        private string _physicalAddress;
        private bool _physicalAdapter;

        public string Name { get { return _networkInterface.Name; } }
        public string Description { get { return _networkInterface.Description; } }
        public string OperationalStatus { get { return _networkInterface.OperationalStatus.ToString(); } }
        public long BytesReceived { get { return _networkInterface.GetIPv4Statistics().BytesReceived; } }
        public long BytesSent { get { return _networkInterface.GetIPv4Statistics().BytesSent; } }
        public long Speed { get { return _networkInterface.Speed; } }
        public DateTime LastRestart { get { return _lastRestart; } set { _lastRestart = value; } }
        public bool PhysicalAdapter { get { return _physicalAdapter; } set { _physicalAdapter = value; } }

        public double[] Rate {
            get {
                var result = new double[2] { 0, 0 };

                DateTime now = DateTime.Now;
                var bytesReceived = BytesReceived;
                var bytesSent = BytesSent;

                if (bytesReceived > _lastBytesReceived && bytesSent > _lastBytesSent) { // skip if counters have flipped
                    result[0] = (double)(bytesReceived - _lastBytesReceived) / (now - _lastRate).TotalSeconds;
                    result[1] = (double)(bytesSent - _lastBytesSent) / (now - _lastRate).TotalSeconds;
                }

                _lastBytesReceived = bytesReceived;
                _lastBytesSent = bytesSent;
                _lastRate = now;

                return result;
            }
        }

        public string PhysicalAddress {
            get {
                // return _physicalAddress; 
                var result = new List<string>();
                for (int i = 0; i < 12; i += 2)
                    result.Add(_physicalAddress.ToString().Substring(i, 2));
                return String.Join("-", result.ToArray());
            }
            set {
                var reg = GetRegistryKeyByInstanceId(_networkInterface.Id);
                if (reg == null)
                    return; // FIXME exception?

                // FIXME why does virtualbox use MAC instead?
                var oldNetworkAddress = (string)reg.GetValue("NetworkAddress");
                var oldMAC = (string)reg.GetValue("MAC");

                if (! string.IsNullOrEmpty(oldNetworkAddress))
                    reg.SetValue("NetworkAddress", value);
                else if (! string.IsNullOrEmpty(oldMAC))
                    reg.SetValue("MAC", value);
                // else exception?

                _physicalAddress = value;
                if (PropertyChanged != null)
                    PropertyChanged(this, new PropertyChangedEventArgs("PhysicalAddress"));
            }
        }

        public string IpAddress {
            get {
                var ipCol = _networkInterface.GetIPProperties().UnicastAddresses;
                if (ipCol == null || ipCol.Count == 1)
                    return "";

                var result = new List<string>();
                foreach (UnicastIPAddressInformation ip in ipCol)
                    result.Add(ip.Address.ToString());

                return string.Join(", ", result);
            }
        }

        private RegistryKey GetRegistryKeyByInstanceId(string id) {
            const string registryPath = "SYSTEM\\CurrentControlSet\\Control\\Class\\{4D36E972-E325-11CE-BFC1-08002BE10318}";
            var reg = Registry.LocalMachine.OpenSubKey(registryPath, false);

            if (reg == null) // is this possible?
                return null;

            foreach (var subKey in reg.GetSubKeyNames()) {
                // if (subKey.ToCharArray()[0] != '0')
                if (subKey.Length != 4) // only want the interfaces
                    continue;

                var subReg = Registry.LocalMachine.OpenSubKey(registryPath + "\\" + subKey, true);
                var netCfgInstanceId = (string)subReg.GetValue("NetCfgInstanceId");

                if (netCfgInstanceId.Equals(id))
                    return subReg;
            }

            return null;
        }

        /// <summary>
        /// Disable then enable the current interface
        /// </summary>
        public void RestartInterface() {
            if (_managementObject == null)
                return;

            _managementObject.InvokeMethod("Disable", null);
            _managementObject.InvokeMethod("Enable", null);

            _lastRestart = DateTime.Now;
        }

        public WrapNetworkInterface(NetworkInterface ni) {
            _networkInterface = ni;

            _physicalAddress = ni.GetPhysicalAddress().ToString();
            _lastRestart = DateTime.Now;

            var search = new ManagementObjectSearcher("SELECT * FROM Win32_NetworkAdapter WHERE GUID = '" + _networkInterface.Id + "'");
            foreach (ManagementObject mo in search.Get())
                _managementObject = mo;

            _physicalAdapter = false;
            try {
                _physicalAdapter = (bool)_managementObject["PhysicalAdapter"];
            }
            catch { 
                // exception if the value doesn't exist, only want True's anyway..
            }
        }
    }
}
