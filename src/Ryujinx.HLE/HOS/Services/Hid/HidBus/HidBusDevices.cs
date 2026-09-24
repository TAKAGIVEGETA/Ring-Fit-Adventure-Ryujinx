using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Kernel;
using Ryujinx.HLE.HOS.Kernel.Memory;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.HLE.HOS.Services.Hid.HidBus.Types;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Ryujinx.HLE.HOS.Services.Hid.HidBus
{
    // State of the hidbus service (Joy-Con rail devices). Ported from yuzu/Eden.
    public class HidBusDevices
    {
        private const int MaxNumberOfHandles = 0x13;

        // (15ms, 66Hz)
        private const long UpdateIntervalMs = 15;

        private struct DeviceSlot
        {
            public bool IsInitialized;
            public BusHandle Handle;
            public HidbusDevice Device;
        }

        private readonly KernelContext _context;
        private readonly SharedMemoryStorage _storage;
        private readonly Lock _lock = new();

        private readonly DeviceSlot[] _devices = new DeviceSlot[MaxNumberOfHandles];
        private readonly HidbusStatusManagerEntry[] _status = new HidbusStatusManagerEntry[MaxNumberOfHandles];

        private readonly Stopwatch _updateTimer = Stopwatch.StartNew();
        private long _lastUpdateMs;
        private bool _isHidbusEnabled;

        /// <summary>
        /// Whether an emulated Ring-Con should be attached to the right Joy-Con rail.
        /// </summary>
        public bool EnableRingCon { get; set; }

        /// <summary>
        /// True while a game has an active Ring-Con, meaning the host device should be polled.
        /// </summary>
        public bool IsRingConActive { get; internal set; }

        /// <summary>
        /// Normalized Ring-Con force, from -1 (fully pulled) to 1 (fully squeezed).
        /// </summary>
        public float RingConForce { get; private set; }

        static HidBusDevices()
        {
            CheckTypeSize<BusHandle>(0x8);
            CheckTypeSize<HidbusStatusManagerEntry>(0x80);
            CheckTypeSize<JoyEnableSixAxisDataAccessor>(0x190);
        }

        private static void CheckTypeSize<T>(int expectedSize)
        {
            if (Unsafe.SizeOf<T>() != expectedSize)
            {
                throw new InvalidOperationException($"{typeof(T).Name} has size 0x{Unsafe.SizeOf<T>():X}, expected 0x{expectedSize:X}");
            }
        }

        internal HidBusDevices(KernelContext context, SharedMemoryStorage storage)
        {
            _context = context;
            _storage = storage;
        }

        /// <summary>
        /// Updates the attached devices. Called from the input thread, rate-limited to the hidbus polling interval.
        /// </summary>
        public void Update(float ringConForce)
        {
            RingConForce = ringConForce;

            long now = _updateTimer.ElapsedMilliseconds;

            if (now - _lastUpdateMs < UpdateIntervalMs)
            {
                return;
            }

            _lastUpdateMs = now;

            lock (_lock)
            {
                if (!_isHidbusEnabled)
                {
                    return;
                }

                for (int i = 0; i < _devices.Length; i++)
                {
                    if (!_devices[i].IsInitialized)
                    {
                        continue;
                    }

                    HidbusDevice device = _devices[i].Device;

                    device.OnUpdate();

                    int entryIndex = _devices[i].Handle.InternalIndex;

                    _status[entryIndex].IsPollingMode = device.IsPollingMode ? (byte)1 : (byte)0;
                    _status[entryIndex].PollingMode = device.PollingMode;
                    _status[entryIndex].IsEnabled = device.IsEnabled ? (byte)1 : (byte)0;

                    WriteStatusEntry(entryIndex);
                }
            }
        }

        private void WriteStatusEntry(int entryIndex)
        {
            _storage.GetRef<HidbusStatusManagerEntry>((ulong)(entryIndex * Unsafe.SizeOf<HidbusStatusManagerEntry>())) = _status[entryIndex];
        }

        private int GetDeviceIndex(BusHandle handle)
        {
            for (int i = 0; i < _devices.Length; i++)
            {
                BusHandle other = _devices[i].Handle;

                if (other.AbstractedPadId == handle.AbstractedPadId &&
                    other.InternalIndex == handle.InternalIndex &&
                    other.PlayerNumber == handle.PlayerNumber &&
                    other.BusTypeId == handle.BusTypeId &&
                    other.IsValid == handle.IsValid)
                {
                    return i;
                }
            }

            return -1;
        }

        internal bool GetBusHandle(NpadIdType npadId, BusType busType, out BusHandle handle)
        {
            lock (_lock)
            {
                handle = _devices[0].Handle;

                for (int i = 0; i < _devices.Length; i++)
                {
                    if (_devices[i].Handle.IsValid != 0 &&
                        _devices[i].Handle.PlayerNumber == (byte)npadId &&
                        _devices[i].Handle.BusTypeId == (byte)busType)
                    {
                        handle = _devices[i].Handle;
                        return true;
                    }
                }

                // Handle not found. Create a new one.
                for (int i = 0; i < _devices.Length; i++)
                {
                    if (_devices[i].Handle.IsValid == 0)
                    {
                        _devices[i].Handle = new BusHandle
                        {
                            AbstractedPadId = i,
                            InternalIndex = (byte)i,
                            PlayerNumber = (byte)npadId,
                            BusTypeId = (byte)busType,
                            IsValid = 1,
                        };

                        handle = _devices[i].Handle;
                        return true;
                    }
                }

                Logger.Error?.Print(LogClass.ServiceHid, "No free or matching bus handle found");

                return false;
            }
        }

        internal ResultCode IsExternalDeviceConnected(BusHandle handle, out bool isConnected)
        {
            lock (_lock)
            {
                isConnected = false;

                int index = GetDeviceIndex(handle);

                if (index < 0)
                {
                    return ResultCode.InvalidDeviceIndex;
                }

                isConnected = _devices[index].Device?.IsActivated ?? false;

                return ResultCode.Success;
            }
        }

        internal ResultCode Initialize(BusHandle handle)
        {
            lock (_lock)
            {
                _isHidbusEnabled = true;

                int index = GetDeviceIndex(handle);

                if (index < 0)
                {
                    return ResultCode.InvalidDeviceIndex;
                }

                int entryIndex = _devices[index].Handle.InternalIndex;

                _devices[index].Device?.Deactivate();

                bool isRingCon = handle.InternalIndex == 0 && EnableRingCon;

                if (isRingCon)
                {
                    _devices[index].Device = new RingController(_context, this);
                    _devices[index].IsInitialized = true;
                    _devices[index].Device.Activate();
                }
                else
                {
                    _devices[index].Device = new HidbusStubbed(_context, this);
                    _devices[index].IsInitialized = true;
                }

                _status[entryIndex] = new HidbusStatusManagerEntry
                {
                    IsInFocus = 1,
                    IsConnected = isRingCon ? (byte)1 : (byte)0,
                    IsConnectedResult = 0,
                    IsEnabled = 0,
                    IsPollingMode = 0,
                };

                WriteStatusEntry(entryIndex);

                return ResultCode.Success;
            }
        }

        internal ResultCode Finalize(BusHandle handle)
        {
            lock (_lock)
            {
                int index = GetDeviceIndex(handle);

                if (index < 0)
                {
                    return ResultCode.InvalidDeviceIndex;
                }

                int entryIndex = _devices[index].Handle.InternalIndex;

                _devices[index].IsInitialized = false;
                _devices[index].Device?.Deactivate();

                _status[entryIndex] = new HidbusStatusManagerEntry
                {
                    IsInFocus = 1,
                    IsConnected = 0,
                    IsConnectedResult = 0,
                    IsEnabled = 0,
                    IsPollingMode = 0,
                };

                WriteStatusEntry(entryIndex);

                return ResultCode.Success;
            }
        }

        internal ResultCode EnableExternalDevice(BusHandle handle, bool isEnabled)
        {
            return WithDevice(handle, device => device.Enable(isEnabled));
        }

        internal ResultCode GetExternalDeviceId(BusHandle handle, out uint deviceId)
        {
            uint id = 0;

            ResultCode result = WithDevice(handle, device => id = device.DeviceId);

            deviceId = id;

            return result;
        }

        internal ResultCode SendCommandAsync(BusHandle handle, byte[] data)
        {
            return WithDevice(handle, device => device.SetCommand(data));
        }

        internal ResultCode GetSendCommandAsyncResult(BusHandle handle, byte[] outData, out ulong outSize)
        {
            ulong size = 0;

            ResultCode result = WithDevice(handle, device => size = device.GetReply(outData));

            outSize = size;

            return result;
        }

        internal ResultCode GetSendCommandAsyncEvent(BusHandle handle, out Kernel.Threading.KEvent sendCommandEvent)
        {
            Kernel.Threading.KEvent evnt = null;

            ResultCode result = WithDevice(handle, device => evnt = device.SendCommandAsyncEvent);

            sendCommandEvent = evnt;

            return result;
        }

        internal ResultCode EnableJoyPollingReceiveMode(BusHandle handle, JoyPollingMode pollingMode, KProcess owner, ulong address)
        {
            return WithDevice(handle, device =>
            {
                device.SetPollingMode(pollingMode);
                device.SetTransferMemory(owner, address);
            });
        }

        internal ResultCode DisableJoyPollingReceiveMode(BusHandle handle)
        {
            return WithDevice(handle, device => device.DisablePollingMode());
        }

        private ResultCode WithDevice(BusHandle handle, Action<HidbusDevice> action)
        {
            lock (_lock)
            {
                int index = GetDeviceIndex(handle);

                if (index < 0 || _devices[index].Device == null)
                {
                    return ResultCode.InvalidDeviceIndex;
                }

                action(_devices[index].Device);

                return ResultCode.Success;
            }
        }
    }
}
