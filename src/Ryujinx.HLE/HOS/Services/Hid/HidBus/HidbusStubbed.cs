using Ryujinx.HLE.HOS.Kernel;

namespace Ryujinx.HLE.HOS.Services.Hid.HidBus
{
    // Placeholder for bus handles with no supported device attached.
    class HidbusStubbed : HidbusDevice
    {
        public HidbusStubbed(KernelContext context, HidBusDevices hidBus) : base(context, hidBus) { }
    }
}
