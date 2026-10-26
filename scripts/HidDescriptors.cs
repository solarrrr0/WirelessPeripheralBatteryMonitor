using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Phase0 {
    public sealed class HidDescriptor {
        public string Path;
        public string InterfaceNumber;
        public string CollectionNumber;
        public string UsagePage;
        public string Usage;
        public ushort InputBytes;
        public ushort OutputBytes;
        public ushort FeatureBytes;
        public string Error;
    }

    public static class HidDescriptors {
        [StructLayout(LayoutKind.Sequential)]
        private struct InterfaceData {
            public int Size;
            public Guid ClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct HidCaps {
            public ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst=17)] public ushort[] Reserved;
            public ushort LinkCollections, InputButtonCaps, InputValueCaps, InputDataIndices;
            public ushort OutputButtonCaps, OutputValueCaps, OutputDataIndices;
            public ushort FeatureButtonCaps, FeatureValueCaps, FeatureDataIndices;
        }
        [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError=true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref InterfaceData data);
        [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, uint size, out uint required, IntPtr info);
        [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("hid.dll", SetLastError=true)]
        [return:MarshalAs(UnmanagedType.U1)]
        private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
        [DllImport("hid.dll")]
        [return:MarshalAs(UnmanagedType.U1)]
        private static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out HidCaps caps);

        public static HidDescriptor[] Read(string vendorId, string productId) {
            string filter="vid_"+vendorId+"&pid_"+productId;
            var results=new List<HidDescriptor>();
            Guid guid; HidD_GetHidGuid(out guid);
            IntPtr set=SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12);
            if(set==new IntPtr(-1)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try {
                for(uint index=0;;index++) {
                    var item=new InterfaceData {Size=Marshal.SizeOf(typeof(InterfaceData))};
                    if(!SetupDiEnumDeviceInterfaces(set,IntPtr.Zero,ref guid,index,ref item)) {
                        int error=Marshal.GetLastWin32Error();
                        if(error==259) break;
                        throw new System.ComponentModel.Win32Exception(error);
                    }
                    uint required;
                    SetupDiGetDeviceInterfaceDetail(set,ref item,IntPtr.Zero,0,out required,IntPtr.Zero);
                    if(required<6 || required>65536) throw new InvalidOperationException("Unexpected interface detail size.");
                    IntPtr detail=Marshal.AllocHGlobal((int)required);
                    try {
                        Marshal.WriteInt32(detail,IntPtr.Size==8?8:6);
                        if(!SetupDiGetDeviceInterfaceDetail(set,ref item,detail,required,out required,IntPtr.Zero))
                            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                        string path=Marshal.PtrToStringUni(IntPtr.Add(detail,4));
                        if(path==null || path.IndexOf(filter,StringComparison.OrdinalIgnoreCase)<0) continue;
                        var row=new HidDescriptor {Path=path};
                        row.InterfaceNumber=Regex.Match(path,@"&mi_([0-9a-f]{2})",RegexOptions.IgnoreCase).Groups[1].Value;
                        row.CollectionNumber=Regex.Match(path,@"&col([0-9a-f]{2})",RegexOptions.IgnoreCase).Groups[1].Value;
                        results.Add(row);
                        // Access 0 permits metadata inspection without input/output access.
                        using(var handle=CreateFile(path,0,3,IntPtr.Zero,3,0,IntPtr.Zero)) {
                            if(handle.IsInvalid) {row.Error="OpenMetadata Win32="+Marshal.GetLastWin32Error();continue;}
                            IntPtr preparsed;
                            if(!HidD_GetPreparsedData(handle,out preparsed)) {row.Error="PreparsedData Win32="+Marshal.GetLastWin32Error();continue;}
                            try {
                                HidCaps caps;
                                int status=HidP_GetCaps(preparsed,out caps);
                                if(status!=0x00110000) {row.Error="HidP_GetCaps status="+status;continue;}
                                row.UsagePage=caps.UsagePage.ToString("X4");row.Usage=caps.Usage.ToString("X4");
                                row.InputBytes=caps.InputLength;row.OutputBytes=caps.OutputLength;row.FeatureBytes=caps.FeatureLength;
                            } finally {HidD_FreePreparsedData(preparsed);}
                        }
                    } finally {Marshal.FreeHGlobal(detail);}
                }
            } finally {SetupDiDestroyDeviceInfoList(set);}
            return results.ToArray();
        }
    }
}
