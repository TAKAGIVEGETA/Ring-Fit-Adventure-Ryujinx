using System.Runtime.InteropServices;

namespace Ryujinx.HLE.HOS.Services.Hid.HidBus.Types
{
    // One entry per bus handle, stored in the hidbus shared memory.
    [StructLayout(LayoutKind.Sequential, Size = 0x80)]
    struct HidbusStatusManagerEntry
    {
        public byte IsConnected;
        public byte Padding0;
        public ushort Padding1;
        public int IsConnectedResult;
        public byte IsEnabled;
        public byte IsInFocus;
        public byte IsPollingMode;
        public byte Reserved;
        public JoyPollingMode PollingMode;
    }
}
