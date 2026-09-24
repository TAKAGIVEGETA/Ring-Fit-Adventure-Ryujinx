using Ryujinx.Common.Memory;
using System.Runtime.InteropServices;

namespace Ryujinx.HLE.HOS.Services.Hid.HidBus.Types
{
    [StructLayout(LayoutKind.Sequential, Size = 0x30)]
    struct DataAccessorHeader
    {
        public int Result;
        public uint Padding;
        public Array24<byte> Unused;
        public ulong LatestEntry;
        public ulong TotalEntries;
    }

    [StructLayout(LayoutKind.Sequential, Size = 0x18)]
    struct JoyEnableSixAxisPollingData
    {
        public Array8<byte> Data;
        public byte OutSize;
        public Array7<byte> Padding;
        public ulong SamplingNumber;
    }

    [StructLayout(LayoutKind.Sequential, Size = 0x20)]
    struct JoyEnableSixAxisPollingEntry
    {
        public ulong SamplingNumber;
        public JoyEnableSixAxisPollingData PollingData;
    }

    // Layout of the transfer memory written while the device is in SixAxisSensorEnable polling mode.
    [StructLayout(LayoutKind.Sequential, Size = 0x190)]
    struct JoyEnableSixAxisDataAccessor
    {
        public DataAccessorHeader Header;
        public Array11<JoyEnableSixAxisPollingEntry> Entries;
    }
}
