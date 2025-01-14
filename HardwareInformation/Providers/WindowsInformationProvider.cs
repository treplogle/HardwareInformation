﻿#region using

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using HardwareInformation.Information;
using Microsoft.Extensions.Logging;

#endregion

namespace HardwareInformation.Providers
{
    #region Win32API

    // Taken from https://github.com/pruggitorg/detect-windows-version
    // ReSharper disable InconsistentNaming
    // ReSharper disable FieldCanBeMadeReadOnly.Global
    // ReSharper disable MemberCanBePrivate.Global
    // ReSharper disable IdentifierTypo
    internal enum NTSTATUS : uint
    {
        /// <summary>
        ///     The operation completed successfully.
        /// </summary>
        STATUS_SUCCESS = 0x00000000
    }

    // Taken from https://github.com/pruggitorg/detect-windows-version
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct OSVERSIONINFOEX
    {
        // The OSVersionInfoSize field must be set to Marshal.SizeOf(typeof(OSVERSIONINFOEX))
        public int OSVersionInfoSize;
        public int MajorVersion;
        public int MinorVersion;
        public int BuildNumber;
        public int PlatformId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string CSDVersion;

        public ushort ServicePackMajor;
        public ushort ServicePackMinor;
        public ushort SuiteMask;
        public byte ProductType;
        public byte Reserved;
    }

    internal static class Win32APIProvider
    {
        private const string NTDLL = "ntdll.dll";

        [SecurityCritical]
        [DllImport(NTDLL, EntryPoint = "RtlGetVersion", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern NTSTATUS ntdll_RtlGetVersion(ref OSVERSIONINFOEX versionInfo);
    }
    // ReSharper restore InconsistentNaming
    // ReSharper restore FieldCanBeMadeReadOnly.Global
    // ReSharper restore MemberCanBePrivate.Global
    // ReSharper restore IdentifierTypo

    #endregion

    internal class WindowsInformationProvider : InformationProvider
    {
        private bool win10;

        public override void GatherGeneralSystemInformation(ref MachineInformation information)
        {
            try
            {
                var osVersionInfoEx = new OSVERSIONINFOEX();
                var success = Win32APIProvider.ntdll_RtlGetVersion(ref osVersionInfoEx);

                if (success == NTSTATUS.STATUS_SUCCESS)
                {
                    if (osVersionInfoEx.MajorVersion >= 10)
                    {
                        win10 = true;
                    }
                }
            }
            catch (Exception e)
            {
                MachineInformationGatherer.Logger.LogError(e, "Encountered while parsing OS Version info");
            }
        }

        public override bool Available(MachineInformation information)
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        }

        public override void GatherUsbInformation(ref MachineInformation information)
        {
            using var mos = new ManagementObjectSearcher("select DeviceID from Win32_PnPEntity");
            var mbos = new ArrayList(mos.Get());
            var data = new Dictionary<string, string[]>();

            for (var i = 0; i < mbos.Count; i++)
            {
                var managementBaseObject = mbos[i] as ManagementBaseObject;

                if (managementBaseObject is null)
                {
                    continue;
                }

                var deviceId = managementBaseObject.Properties["DeviceID"].Value as string;

                if (deviceId is null || !deviceId.StartsWith("USB"))
                {
                    continue;
                }

                if (!data.ContainsKey(deviceId))
                {
                    data.Add(deviceId, new string[8]);
                }
                else if (data.ContainsKey(deviceId))
                {
                    continue;
                }

                // Win32_PnpDeviceProperty is only available with Windows 10
                if (!win10)
                {
                    continue;
                }

                var mo = managementBaseObject as ManagementObject;
                var inParams = mo.GetMethodParameters("GetDeviceProperties");

                var result = mo.InvokeMethod(
                    "GetDeviceProperties",
                    inParams,
                    new InvokeMethodOptions()
                );

                if (result?.Properties["deviceProperties"].Value is null)
                {
                    continue;
                }

                foreach (var deviceProperties in result.Properties["deviceProperties"].Value as ManagementBaseObject[])
                {
                    var keyName = deviceProperties.Properties["KeyName"].Value as string;
                    var value = deviceProperties.Properties["Data"].Value as string;

                    if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(keyName))
                    {
                        MachineInformationGatherer.Logger.LogTrace(
                            $"KeyName {keyName} or Value {value} was null or whitespace for device ID {deviceId}");
                        continue;
                    }

                    switch (keyName)
                    {
                        case "DEVPKEY_Device_BusReportedDeviceDesc":
                        {
                            data[deviceId][0] = value;
                            break;
                        }
                        case "DEVPKEY_Device_DriverDesc":
                        {
                            data[deviceId][1] = value;
                            break;
                        }
                        case "DEVPKEY_Device_DriverVersion":
                        {
                            data[deviceId][2] = value;
                            break;
                        }
                        case "DEVPKEY_Device_DriverDate":
                        {
                            var year = int.Parse(value.Substring(0, 4));
                            var month = int.Parse(value.Substring(4, 2));
                            var day = int.Parse(value.Substring(6, 2));
                            var hour = int.Parse(value.Substring(8, 2));
                            var minute = int.Parse(value.Substring(10, 2));
                            var second = int.Parse(value.Substring(12, 2));

                            data[deviceId][3] =
                                new DateTime(year, month, day, hour, minute, second).ToString();
                            break;
                        }
                        case "DEVPKEY_Device_Class":
                        {
                            data[deviceId][4] = value;
                            break;
                        }
                        case "DEVPKEY_Device_DriverProvider":
                        {
                            data[deviceId][5] = value;
                            break;
                        }
                        case "DEVPKEY_NAME":
                        {
                            data[deviceId][6] = value;
                            break;
                        }
                        case "DEVPKEY_Device_Manufacturer":
                        {
                            data[deviceId][7] = value;
                            break;
                        }
                        case "DEVPKEY_Device_Children":
                        {
                            var children = deviceProperties.Properties["DEVPKEY_Device_Children"];
                            if (children.Value is not null)
                            {
                                if (children.IsArray)
                                {
                                    foreach (var child in children.Value as string[])
                                    {
                                        mos.Query = new ObjectQuery(
                                            $"select * from Win32_PnPEntity where DeviceID = {child}");
                                        var childs = mos.Get();

                                        foreach (var child1 in childs)
                                        {
                                            mbos.Add(child1);
                                        }
                                    }
                                }
                            }

                            break;
                        }
                    }
                }
            }

            var realData = new Dictionary<string, USBDevice>();

            foreach (var stringse in data)
            {
                var deviceDesc = stringse.Value[0];
                var driverDesc = stringse.Value[1];

                if (deviceDesc is null || driverDesc is null)
                {
                    continue;
                }

                if (!realData.ContainsKey(deviceDesc))
                {
                    var (vid, pid) = GetVidAndPid(stringse.Key);
                    var (vendorName, productName) = USBVendorList.GetVendorAndProductName(vid, pid);

                    realData.Add(deviceDesc,
                        new USBDevice
                        {
                            Name = stringse.Value[6], BusReportedName = deviceDesc, DriverName = driverDesc,
                            DriverVersion = stringse.Value[2], DriverDate = DateTime.Parse(stringse.Value[3]),
                            Class = stringse.Value[4], DriverProvider = stringse.Value[5],
                            Manufacturer = stringse.Value[7],
                            DeviceID = stringse.Key, VendorID = vid, ProductID = pid,
                            VendorName = vendorName, ProductName = productName
                        });
                }
                else
                {
                    var device = realData[deviceDesc];
                    var replace = false;

                    // Prefer composite over solely input
                    if (device.DriverName == "USB Input Device" && driverDesc == "USB Composite Device")
                    {
                        replace = true;
                    }
                    // Prefer composite over solely output
                    else if (device.DriverName == "USB Output Device" &&
                             driverDesc == "USB Composite Device")
                    {
                        replace = true;
                    }
                    // Prefer composite over solely audio
                    else if (device.DriverName == "USB Audio Device" &&
                             driverDesc == "USB Composite Device")
                    {
                        replace = true;
                    }
                    // Prefer different DriverProvider over Microsoft (standard)
                    else if (device.DriverProvider == "Microsoft" && stringse.Value[5] != "Microsoft")
                    {
                        replace = true;
                    }
                    // Prefer different (or any) Manufacturer over Microsoft
                    else if (device.Manufacturer is null or "Microsoft" &&
                             stringse.Value[7] is not null and not "Microsoft")
                    {
                        replace = true;
                    }
                    // Prefer custom description over windows-default
                    else if (driverDesc is not "USB Input Device" and not "USB Composite Device" and not
                                 "USB Output Device" and not "USB Audio Device" and not "USB Mass Storage Device"
                                 and not "Disk Drive" and not "USB Attached SCSI (UAS) Mass Storage Device"
                             && !driverDesc.StartsWith("Generic"))
                    {
                        replace = true;
                    }

                    if (replace)
                    {
                        device.Name = stringse.Value[6];
                        device.BusReportedName = deviceDesc;
                        device.DriverName = driverDesc;
                        device.DriverVersion = stringse.Value[2];
                        device.DriverDate = DateTime.Parse(stringse.Value[3]);
                        device.Class = stringse.Value[4];
                        device.DriverProvider = stringse.Value[5];
                        device.Manufacturer = stringse.Value[7];

                        if (stringse.Key != device.DeviceID)
                        {
                            var (vid, pid) = GetVidAndPid(stringse.Key);

                            if ((vid == null || vid == device.VendorID) && (pid == null || pid == device.ProductID))
                            {
                                continue;
                            }

                            var (vendorName, productName) = USBVendorList.GetVendorAndProductName(vid, pid);
                            device.VendorID ??= vid;
                            device.ProductID ??= pid;
                            device.VendorName ??= vendorName;
                            device.ProductName ??= productName;
                        }
                    }
                }
            }

            information.UsbDevices = new List<USBDevice>(realData.Values).AsReadOnly();
        }

        private static Tuple<string, string> GetVidAndPid(string deviceId)
        {
            var vidPid = deviceId.Split('\\')[1];
            var vid = vidPid.StartsWith("VID_") ? vidPid.Split('&')[0].Replace("VID_", "") : null;
            var pid = vidPid.StartsWith("VID_") ? vidPid.Split('&')[1].Replace("PID_", "") : null;

            return Tuple.Create(vid, pid);
        }

        public override void GatherMainboardInformation(ref MachineInformation information)
        {
            using (var mos = new ManagementObjectSearcher("select Product,Manufacturer,Version from Win32_BaseBoard"))
            {
                foreach (var managementBaseObject in mos.Get())
                {
                    if (managementBaseObject?.Properties == null || managementBaseObject.Properties.Count == 0)
                    {
                        continue;
                    }

                    information.SmBios.BoardName = managementBaseObject.Properties["Product"].Value.ToString();
                    information.SmBios.BoardVendor = managementBaseObject.Properties["Manufacturer"].Value.ToString();
                    information.SmBios.BoardVersion = managementBaseObject.Properties["Version"].Value.ToString();
                }
            }

            using (var mos = new ManagementObjectSearcher("select Name,Manufacturer,Version from Win32_BIOS"))
            {
                foreach (var managementBaseObject in mos.Get())
                {
                    if (managementBaseObject?.Properties == null || managementBaseObject.Properties.Count == 0)
                    {
                        continue;
                    }

                    information.SmBios.BIOSCodename = managementBaseObject.Properties["Name"].Value.ToString();
                    information.SmBios.BIOSVendor = managementBaseObject.Properties["Manufacturer"].Value.ToString();
                    information.SmBios.BIOSVersion = managementBaseObject.Properties["Version"].Value.ToString();
                }
            }
        }

        public override void GatherDiskInformation(ref MachineInformation information)
        {
            using var mos = new ManagementObjectSearcher("select Model,Size,Caption,DeviceID,Index,InterfaceType,SerialNumber from Win32_DiskDrive");
            var disks = new List<Disk>();

            foreach (var managementBaseObject in mos.Get())
            {
                var disk = new Disk
                {
                    Model = managementBaseObject.Properties["Model"].Value.ToString(),
                    Capacity = ulong.Parse(managementBaseObject.Properties["Size"].Value.ToString()),
                    Caption = managementBaseObject.Properties["Caption"].Value.ToString(),
                    DeviceID = managementBaseObject.Properties["DeviceID"].Value.ToString(),
                    DriveIndex = int.Parse(managementBaseObject.Properties["Index"].Value.ToString()),
                    InterfaceType = managementBaseObject.Properties["InterfaceType"].Value.ToString(),
                    SerialNumber = managementBaseObject.Properties["SerialNumber"].Value.ToString().Trim()
                };
                disk.CapacityHRF = Util.FormatBytes(disk.Capacity);

                disks.Add(disk);
            }

            information.Disks = disks.AsReadOnly();
        }

        public override void GatherGpuInformation(ref MachineInformation information)
        {
            using var mos = new ManagementObjectSearcher(
                "select AdapterCompatibility,Caption,Description,DriverDate,DriverVersion,Name,Status,DeviceID from Win32_VideoController");
            var gpus = new List<GPU>();

            foreach (var managementBaseObject in mos.Get())
            {
                var gpu = new GPU
                {
                    Vendor = managementBaseObject.Properties["AdapterCompatibility"].Value.ToString(),
                    Caption = managementBaseObject.Properties["Caption"].Value.ToString(),
                    DriverDate = managementBaseObject.Properties["DriverDate"].Value.ToString(),
                    DriverVersion = managementBaseObject.Properties["DriverVersion"].Value.ToString(),
                    Description = managementBaseObject.Properties["Description"].Value.ToString(),
                    Name = managementBaseObject.Properties["Name"].Value.ToString(),
                    Status = managementBaseObject.Properties["Status"].Value.ToString(),
                    DeviceID = managementBaseObject.Properties["DeviceID"].Value.ToString()
                };

                gpus.Add(gpu);
            }

            if (information.Gpus.Count == 0)
            {
                information.Gpus = gpus.AsReadOnly();
            }
            else
            {
                foreach (var gpu in information.Gpus)
                {
                    var wmiGpu = gpus.FirstOrDefault(g => g.Name == gpu.Name);

                    if (wmiGpu is not null)
                    {
                        // Currently only DriverDate, Status and Description cannot be queried via Vulkan
                        gpu.DriverDate = wmiGpu.DriverDate;
                        gpu.Status = wmiGpu.Status;
                        gpu.Description = wmiGpu.Description;
                    }
                }
            }
        }

        public override void GatherMonitorInformation(ref MachineInformation information)
        {
            using var mos = new ManagementObjectSearcher("root\\wmi",
                "select ManufacturerName,UserFriendlyName,SerialNumberID from WmiMonitorID");
            var displays = new List<Display>();

            foreach (var managementBaseObject in mos.Get())
            {
                try
                {
                    var display = new Display
                    {
                        Manufacturer = string.Join("",
                            ((IEnumerable<ushort>) managementBaseObject.Properties["ManufacturerName"].Value)
                            .Select(u => char.ConvertFromUtf32(u)).Where(s => s != "\u0000").ToList()),
                        Name = string.Join("",
                            ((IEnumerable<ushort>) managementBaseObject.Properties["UserFriendlyName"].Value)
                            .Select(u => char.ConvertFromUtf32(u)).Where(s => s != "\u0000").ToList()),
                        SerialNumber = string.Join("",
                            ((IEnumerable<ushort>)managementBaseObject.Properties["SerialNumberID"].Value)
                            .Select(u => char.ConvertFromUtf32(u)).Where(s => s != "\u0000").ToList())
                    };

                    displays.Add(display);
                }
                catch
                {
                    // Intentionally left blank
                }
            }

            information.Displays = displays.AsReadOnly();
        }
        
        public override void GatherCpuInformation(ref MachineInformation information)
        {
            string query;

            if (win10)
            {
                query =
                    "select Name,NumberOfEnabledCore,NumberOfLogicalProcessors,SocketDesignation,MaxClockSpeed from Win32_Processor";
            }
            else
            {
                query = "select Name,NumberOfLogicalProcessors,SocketDesignation,MaxClockSpeed from Win32_Processor";
            }

            using var mos = new ManagementObjectSearcher(query);

            foreach (var managementBaseObject in mos.Get())
            {
                if (managementBaseObject?.Properties == null || managementBaseObject.Properties.Count == 0)
                {
                    continue;
                }

                foreach (var propertyData in managementBaseObject.Properties)
                {
                    if (propertyData?.Value == null || propertyData.Name == null)
                    {
                        continue;
                    }

                    switch (propertyData.Name)
                    {
                        case "Name":
                        {
                            if (information.Cpu.Name == default || information.Cpu.Name == information.Cpu.Caption)
                            {
                                information.Cpu.Name = propertyData.Value.ToString().Trim();
                            }

                            break;
                        }

                        // MIND THE SSSSSSSS
                        case "NumberOfEnabledCore":
                        {
                            var val = uint.Parse(propertyData.Value.ToString());

                            // Safety check
                            if (information.Cpu.PhysicalCores == default ||
                                information.Cpu.PhysicalCores == information.Cpu.LogicalCores ||
                                val != 0 && val != information.Cpu.PhysicalCores)
                            {
                                information.Cpu.PhysicalCores = val;
                            }

                            break;
                        }

                        case "NumberOfLogicalProcessors":
                        {
                            var val = uint.Parse(propertyData.Value.ToString());

                            if (information.Cpu.LogicalCores == default ||
                                val != 0 && val != information.Cpu.LogicalCores)
                            {
                                information.Cpu.LogicalCores = val;
                            }

                            break;
                        }

                        case "SocketDesignation":
                        {
                            if (information.Cpu.Socket == default)
                            {
                                information.Cpu.Socket = propertyData.Value.ToString().Trim();
                            }

                            break;
                        }

                        case "MaxClockSpeed":
                        {
                            if (information.Cpu.NormalClockSpeed == default)
                            {
                                information.Cpu.NormalClockSpeed = uint.Parse(propertyData.Value.ToString());
                            }

                            break;
                        }
                    }
                }
            }
        }

        public override void GatherRamInformation(ref MachineInformation information)
        {
            string query;
            var ramSticks = new List<RAM>();

            if (win10)
            {
                query =
                    "select ConfiguredClockSpeed,Manufacturer,Capacity,DeviceLocator,PartNumber,FormFactor,BankLabel,Tag from Win32_PhysicalMemory";
            }
            else
            {
                query = "select Manufacturer,Capacity,DeviceLocator,PartNumber,FormFactor from Win32_PhysicalMemory";
            }

            using var mos = new ManagementObjectSearcher(query);

            // There is currently no other way to gather RAM information so we don't need to check if it's already set
            foreach (var managementBaseObject in mos.Get())
            {
                if (managementBaseObject?.Properties == null || managementBaseObject.Properties.Count == 0)
                {
                    continue;
                }

                var ram = new RAM();

                foreach (var propertyData in managementBaseObject.Properties)
                {
                    if (propertyData?.Value == null)
                    {
                        continue;
                    }

                    switch (propertyData.Name)
                    {
                        case "ConfiguredClockSpeed":
                        {
                            ram.Speed = uint.Parse(propertyData.Value.ToString());

                            break;
                        }

                        case "Manufacturer":
                        {
                            ram.Manufacturer = propertyData.Value.ToString();

                            break;
                        }

                        case "Capacity":
                        {
                            ram.Capacity += ulong.Parse(propertyData.Value.ToString());

                            break;
                        }

                        case "DeviceLocator":
                        {
                            ram.DeviceLocator = propertyData.Value.ToString();

                            break;
                        }

                        case "PartNumber":
                        {
                            ram.Name = propertyData.Value.ToString();

                            break;
                        }

                        case "FormFactor":
                        {
                            ram.FormFactor = (RAM.FormFactors) Enum.Parse(
                                typeof(RAM.FormFactors), propertyData.Value.ToString());

                            break;
                        }

                        case "BankLabel":
                        {
                            ram.BankLabel = propertyData.Value.ToString();

                            break;
                        }

                        case "Tag":
                        {
                            ram.Tag = propertyData.Value.ToString();

                            break;
                        }
                    }
                }

                ram.CapacityHRF = Util.FormatBytes(ram.Capacity);

                ramSticks.Add(ram);
            }

            information.RAMSticks = ramSticks.AsReadOnly();
        }
    }
}