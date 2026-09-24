using Ryujinx.HLE.HOS.Kernel;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.HLE.HOS.Services.Hid.HidBus.Types;
using System;

namespace Ryujinx.HLE.HOS.Services.Hid.HidBus
{
    // Base class for devices attached to a Joy-Con rail (nn::hidbus).
    abstract class HidbusDevice
    {
        protected readonly HidBusDevices HidBus;

        public KEvent SendCommandAsyncEvent { get; }

        public bool IsActivated { get; private set; }
        public bool IsEnabled { get; private set; }
        public bool IsPollingMode { get; private set; }
        public JoyPollingMode PollingMode { get; private set; }

        protected KProcess TransferMemoryOwner { get; private set; }
        protected ulong TransferMemoryAddress { get; private set; }

        protected HidbusDevice(KernelContext context, HidBusDevices hidBus)
        {
            HidBus = hidBus;
            SendCommandAsyncEvent = new KEvent(context);
        }

        public void Activate()
        {
            if (IsActivated)
            {
                return;
            }

            IsActivated = true;
            OnInit();
        }

        public void Deactivate()
        {
            if (IsActivated)
            {
                OnRelease();
            }

            IsActivated = false;
        }

        public void Enable(bool enable)
        {
            IsEnabled = enable;
        }

        public void SetPollingMode(JoyPollingMode mode)
        {
            PollingMode = mode;
            IsPollingMode = true;
        }

        public void DisablePollingMode()
        {
            IsPollingMode = false;
        }

        public void SetTransferMemory(KProcess owner, ulong address)
        {
            TransferMemoryOwner = owner;
            TransferMemoryAddress = address;
        }

        protected virtual void OnInit() { }

        protected virtual void OnRelease() { }

        // Writes the latest polling data to the transfer memory.
        public virtual void OnUpdate() { }

        public virtual byte DeviceId => 0;

        // Stores a command sent by the guest.
        public virtual bool SetCommand(ReadOnlySpan<byte> data) => false;

        // Writes the reply to the last command and returns its size.
        public virtual ulong GetReply(Span<byte> data) => 0;
    }
}
