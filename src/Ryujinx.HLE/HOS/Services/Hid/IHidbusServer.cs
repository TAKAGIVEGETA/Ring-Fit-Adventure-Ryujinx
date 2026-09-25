using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Ipc;
using Ryujinx.HLE.HOS.Kernel.Memory;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.HLE.HOS.Services.Hid.HidBus;
using Ryujinx.HLE.HOS.Services.Hid.HidBus.Types;
using Ryujinx.Horizon.Common;
using System;

namespace Ryujinx.HLE.HOS.Services.Hid
{
    [Service("hidbus")]
    class IHidbusServer : IpcService
    {
        private int _sharedMemoryHandle;

        public IHidbusServer(ServiceCtx context) { }

        [CommandCmif(1)]
        // GetBusHandle(nn::hid::NpadIdType, nn::hidbus::BusType, nn::applet::AppletResourceUserId) -> (bool HasHandle, nn::hidbus::BusHandle)
        public ResultCode GetBusHandle(ServiceCtx context)
        {
            NpadIdType npadIdType = (NpadIdType)context.RequestData.ReadInt32();
            context.RequestData.BaseStream.Position += 4; // Padding
            BusType busType = (BusType)context.RequestData.ReadInt64();
            long appletResourceUserId = context.RequestData.ReadInt64();

            bool hasHandle = context.Device.Hid.HidBus.GetBusHandle(npadIdType, busType, out BusHandle busHandle);

            context.ResponseData.Write(hasHandle);
            context.ResponseData.BaseStream.Position += 7; // Padding
            context.ResponseData.WriteStruct(busHandle);

            Logger.Info?.Print(LogClass.ServiceHid, $"GetBusHandle: npadIdType={npadIdType}, busType={busType}, appletResourceUserId={appletResourceUserId}, hasHandle={hasHandle}");

            return ResultCode.Success;
        }

        [CommandCmif(2)]
        // IsExternalDeviceConnected(nn::hidbus::BusHandle) -> bool
        public ResultCode IsExternalDeviceConnected(ServiceCtx context)
        {
            BusHandle busHandle = context.RequestData.ReadStruct<BusHandle>();

            ResultCode result = context.Device.Hid.HidBus.IsExternalDeviceConnected(busHandle, out bool isConnected);

            context.ResponseData.Write(isConnected);

            return result;
        }

        [CommandCmif(3)]
        // Initialize(nn::hidbus::BusHandle, nn::applet::AppletResourceUserId, pid)
        public ResultCode Initialize(ServiceCtx context)
        {
            BusHandle busHandle = context.RequestData.ReadStruct<BusHandle>();
            long appletResourceUserId = context.RequestData.ReadInt64();

            Logger.Info?.Print(LogClass.ServiceHid, $"Initialize: internalIndex={busHandle.InternalIndex}, playerNumber={busHandle.PlayerNumber}, busType={busHandle.BusTypeId}, appletResourceUserId={appletResourceUserId}, ringCon={context.Device.Hid.HidBus.EnableRingCon}");

            return context.Device.Hid.HidBus.Initialize(busHandle);
        }

        [CommandCmif(4)]
        // Finalize(nn::hidbus::BusHandle, nn::applet::AppletResourceUserId, pid)
        public ResultCode Finalize(ServiceCtx context)
        {
            BusHandle busHandle = context.RequestData.ReadStruct<BusHandle>();

            Logger.Info?.Print(LogClass.ServiceHid, $"Finalize: internalIndex={busHandle.InternalIndex}");

            return context.Device.Hid.HidBus.Finalize(busHandle);
        }

        [CommandCmif(5)]
        // EnableExternalDevice(bool, nn::hidbus::BusHandle, u64, nn::applet::AppletResourceUserId, pid)
        public ResultCode EnableExternalDevice(ServiceCtx context)
        {
            bool isEnabled = context.RequestData.ReadBoolean();
            context.RequestData.BaseStream.Position += 7; // Padding
            BusHandle busHandle = context.RequestData.ReadStruct<BusHandle>();

            Logger.Debug?.Print(LogClass.ServiceHid, $"EnableExternalDevice: isEnabled={isEnabled}, internalIndex={busHandle.InternalIndex}");

            return context.Device.Hid.HidBus.EnableExternalDevice(busHandle, isEnabled);
        }

        [CommandCmif(6)]
        // GetExternalDeviceId(nn::hidbus::BusHandle) -> u32
        public ResultCode GetExternalDeviceId(ServiceCtx context)
        {
            BusHandle busHandle = context.RequestData.ReadStruct<BusHandle>();

            ResultCode result = context.Device.Hid.HidBus.GetExternalDeviceId(busHandle, out uint deviceId);

            context.ResponseData.Write(deviceId);

            return result;
        }

        [CommandCmif(7)]
        // SendCommandAsync(nn::hidbus::BusHandle, buffer<bytes, 0x21>)
        public ResultCode SendCommandAsync(ServiceCtx context)
        {
            BusHandle busHandle = context.RequestData.ReadStruct<BusHandle>();

            (ulong position, ulong size) = context.Request.GetBufferType0x21();

            byte[] data = new byte[size];

            context.Memory.Read(position, data);

            return context.Device.Hid.HidBus.SendCommandAsync(busHandle, data);
        }

        [CommandCmif(8)]
        // GetSendCommandAsynceResult(nn::hidbus::BusHandle) -> (u64, buffer<bytes, 0x22>)
        public ResultCode GetSendCommandAsynceResult(ServiceCtx context)
        {
            BusHandle busHandle = context.RequestData.ReadStruct<BusHandle>();

            (ulong position, ulong size) = context.Request.GetBufferType0x22();

            byte[] data = new byte[size];

            ResultCode result = context.Device.Hid.HidBus.GetSendCommandAsyncResult(busHandle, data, out ulong outSize);

            context.Memory.Write(position, data);

            context.ResponseData.Write(outSize);

            return result;
        }

        [CommandCmif(9)]
        // SetEventForSendCommandAsycResult(nn::hidbus::BusHandle) -> handle<copy>
        public ResultCode SetEventForSendCommandAsycResult(ServiceCtx context)
        {
            BusHandle busHandle = context.RequestData.ReadStruct<BusHandle>();

            ResultCode result = context.Device.Hid.HidBus.GetSendCommandAsyncEvent(busHandle, out KEvent sendCommandEvent);

            if (result != ResultCode.Success)
            {
                return result;
            }

            if (context.Process.HandleTable.GenerateHandle(sendCommandEvent.ReadableEvent, out int handle) != Result.Success)
            {
                throw new InvalidOperationException("Out of handles!");
            }

            context.Response.HandleDesc = IpcHandleDesc.MakeCopy(handle);

            return ResultCode.Success;
        }

        [CommandCmif(10)]
        // GetSharedMemoryHandle() -> handle<copy>
        public ResultCode GetSharedMemoryHandle(ServiceCtx context)
        {
            if (_sharedMemoryHandle == 0)
            {
                if (context.Process.HandleTable.GenerateHandle(context.Device.System.HidBusSharedMem, out _sharedMemoryHandle) != Result.Success)
                {
                    throw new InvalidOperationException("Out of handles!");
                }
            }

            context.Response.HandleDesc = IpcHandleDesc.MakeCopy(_sharedMemoryHandle);

            return ResultCode.Success;
        }

        [CommandCmif(11)]
        // EnableJoyPollingReceiveMode(u32, nn::hidbus::JoyPollingMode, nn::hidbus::BusHandle, handle<copy, transfer_memory>)
        public ResultCode EnableJoyPollingReceiveMode(ServiceCtx context)
        {
            uint transferMemorySize = context.RequestData.ReadUInt32();
            JoyPollingMode pollingMode = (JoyPollingMode)context.RequestData.ReadUInt32();
            BusHandle busHandle = context.RequestData.ReadStruct<BusHandle>();
            int transferMemoryHandle = context.Request.HandleDesc.ToCopy[0];

            KTransferMemory transferMemory = context.Process.HandleTable.GetObject<KTransferMemory>(transferMemoryHandle);

            Logger.Info?.Print(LogClass.ServiceHid, $"EnableJoyPollingReceiveMode: pollingMode={pollingMode}, size=0x{transferMemorySize:X}, internalIndex={busHandle.InternalIndex}");

            ResultCode result = context.Device.Hid.HidBus.EnableJoyPollingReceiveMode(busHandle, pollingMode, transferMemory.Creator, transferMemory.Address);

            context.Device.System.KernelContext.Syscall.CloseHandle(transferMemoryHandle);

            return result;
        }

        [CommandCmif(12)]
        // DisableJoyPollingReceiveMode(nn::hidbus::BusHandle)
        public ResultCode DisableJoyPollingReceiveMode(ServiceCtx context)
        {
            BusHandle busHandle = context.RequestData.ReadStruct<BusHandle>();

            Logger.Info?.Print(LogClass.ServiceHid, $"DisableJoyPollingReceiveMode: internalIndex={busHandle.InternalIndex}");

            return context.Device.Hid.HidBus.DisableJoyPollingReceiveMode(busHandle);
        }

        [CommandCmif(14)]
        // SetStatusManagerType(nn::hidbus::detail::StatusManagerType)
        public ResultCode SetStatusManagerType(ServiceCtx context)
        {
            uint statusManagerType = context.RequestData.ReadUInt32();

            Logger.Stub?.PrintStub(LogClass.ServiceHid, new { statusManagerType });

            return ResultCode.Success;
        }
    }
}
