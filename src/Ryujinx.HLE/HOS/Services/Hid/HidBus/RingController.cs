using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Kernel;
using Ryujinx.HLE.HOS.Services.Hid.HidBus.Types;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Ryujinx.HLE.HOS.Services.Hid.HidBus
{
    // Emulated Ring-Con (Ring Fit Adventure) attached to the right Joy-Con rail. Ported from yuzu/Eden.
    class RingController : HidbusDevice
    {
        // These values are obtained from a real ring controller.
        private const short IdleValue = 2280;
        private const short IdleDeadzone = 120;
        private const short Range = 2500;

        private const int EntryCount = 10;

        // Most missing command names are leftovers from other firmware versions.
        private enum RingConCommand : uint
        {
            GetFirmwareVersion = 0x00020000,
            ReadId = 0x00020100,
            JoyPolling = 0x00020101,
            Unknown1 = 0x00020104,
            C20105 = 0x00020105,
            Unknown2 = 0x00020204,
            Unknown3 = 0x00020304,
            Unknown4 = 0x00020404,
            ReadUnkCal = 0x00020504,
            ReadFactoryCal = 0x00020A04,
            Unknown5 = 0x00021104,
            Unknown6 = 0x00021204,
            Unknown7 = 0x00021304,
            ReadUserCal = 0x00021A04,
            ReadRepCount = 0x00023104,
            ReadTotalPushCount = 0x00023204,
            ResetRepCount = 0x04013104,
            Unknown8 = 0x04011104,
            Unknown9 = 0x04011204,
            Unknown10 = 0x04011304,
            SaveCalData = 0x10011A04,
            Error = 0xFFFFFFFF,
        }

        private enum DataValid : uint
        {
            Valid,
            BadCrc,
            Cal,
        }

        private struct CalibrationValue
        {
            public short Value;
            public ushort Crc;
        }

        private RingConCommand _command = RingConCommand.Error;

        // These counters are used in multitasking mode while the switch is sleeping.
        private byte _totalRepCount;
        private readonly byte _totalPushCount = 0;

        private CalibrationValue _userOsMax = new() { Value = Range, Crc = 228 };
        private CalibrationValue _userHkMax = new() { Value = -Range, Crc = 239 };
        private CalibrationValue _userZero = new() { Value = IdleValue, Crc = 225 };

        private JoyEnableSixAxisDataAccessor _enableSixAxisData;

        public RingController(KernelContext context, HidBusDevices hidBus) : base(context, hidBus) { }

        public override byte DeviceId => 0x20;

        protected override void OnInit()
        {
            HidBus.IsRingConActive = true;
        }

        protected override void OnRelease()
        {
            HidBus.IsRingConActive = false;
        }

        public override void OnUpdate()
        {
            if (!IsActivated || !IsEnabled || !IsPollingMode || TransferMemoryOwner == null)
            {
                return;
            }

            // TODO: Increment multitasking counters from motion and sensor data.

            if (PollingMode != JoyPollingMode.SixAxisSensorEnable)
            {
                Logger.Error?.Print(LogClass.ServiceHid, $"Polling mode not supported {PollingMode}");
                return;
            }

            ref DataAccessorHeader header = ref _enableSixAxisData.Header;

            header.TotalEntries = EntryCount;
            header.Result = 0;

            ulong lastSamplingNumber = _enableSixAxisData.Entries[(int)header.LatestEntry].SamplingNumber;

            header.LatestEntry = (header.LatestEntry + 1) % EntryCount;

            ref JoyEnableSixAxisPollingEntry entry = ref _enableSixAxisData.Entries[(int)header.LatestEntry];

            entry.SamplingNumber = lastSamplingNumber + 1;
            entry.PollingData.SamplingNumber = entry.SamplingNumber;
            entry.PollingData.OutSize = 8;

            Span<byte> data = entry.PollingData.Data.AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)DataValid.Valid);
            BinaryPrimitives.WriteInt16LittleEndian(data[4..], GetSensorValue());
            BinaryPrimitives.WriteUInt16LittleEndian(data[6..], 0);

            // The guest may release the transfer memory at any time, don't write to it once it's gone.
            if (!TransferMemoryOwner.CpuMemory.IsRangeMapped(TransferMemoryAddress, (ulong)Unsafe.SizeOf<JoyEnableSixAxisDataAccessor>()))
            {
                return;
            }

            TransferMemoryOwner.CpuMemory.Write(TransferMemoryAddress, _enableSixAxisData);

            // TODO: Temporary Ring-Con debugging aid.
            if (entry.SamplingNumber % 66 == 1)
            {
                Logger.Info?.Print(LogClass.ServiceHid, $"Ring-Con polling: sampling={entry.SamplingNumber}, value={BinaryPrimitives.ReadInt16LittleEndian(data[4..])}, address=0x{TransferMemoryAddress:X}");
            }
        }

        private short GetSensorValue()
        {
            float force = Math.Clamp(HidBus.RingConForce, -1f, 1f);

            return (short)((short)(force * Range) + IdleValue);
        }

        public override bool SetCommand(ReadOnlySpan<byte> data)
        {
            if (data.Length < 4)
            {
                Logger.Error?.Print(LogClass.ServiceHid, $"Command size not supported {data.Length}");
                _command = RingConCommand.Error;
                return false;
            }

            _command = (RingConCommand)BinaryPrimitives.ReadUInt32LittleEndian(data);

            switch (_command)
            {
                case RingConCommand.GetFirmwareVersion:
                case RingConCommand.ReadId:
                case RingConCommand.C20105:
                case RingConCommand.ReadUnkCal:
                case RingConCommand.ReadFactoryCal:
                case RingConCommand.ReadUserCal:
                case RingConCommand.ReadRepCount:
                case RingConCommand.ReadTotalPushCount:
                    SendCommandAsyncEvent.WritableEvent.Signal();
                    return true;
                case RingConCommand.ResetRepCount:
                    _totalRepCount = 0;
                    SendCommandAsyncEvent.WritableEvent.Signal();
                    return true;
                case RingConCommand.SaveCalData:
                    if (data.Length >= 0x10)
                    {
                        _userOsMax = ReadCalibrationValue(data[4..]);
                        _userHkMax = ReadCalibrationValue(data[8..]);
                        _userZero = ReadCalibrationValue(data[12..]);
                    }

                    SendCommandAsyncEvent.WritableEvent.Signal();
                    return true;
                default:
                    Logger.Error?.Print(LogClass.ServiceHid, $"Command not implemented 0x{(uint)_command:X8}");
                    _command = RingConCommand.Error;
                    // Signal a reply to avoid softlocking the game.
                    SendCommandAsyncEvent.WritableEvent.Signal();
                    return false;
            }
        }

        public override ulong GetReply(Span<byte> outData)
        {
            Span<byte> reply = stackalloc byte[0x14];
            reply.Clear();

            int size;

            switch (_command)
            {
                case RingConCommand.GetFirmwareVersion:
                    WriteStatus(reply, DataValid.Valid);
                    reply[4] = 0x0; // sub
                    reply[5] = 0x2c; // main
                    size = 8;
                    break;
                case RingConCommand.ReadId:
                    // The values are hardcoded from a real joycon.
                    WriteStatus(reply, DataValid.Valid);
                    BinaryPrimitives.WriteUInt16LittleEndian(reply[4..], 8);
                    BinaryPrimitives.WriteUInt16LittleEndian(reply[6..], 41);
                    BinaryPrimitives.WriteUInt16LittleEndian(reply[8..], 22294);
                    BinaryPrimitives.WriteUInt16LittleEndian(reply[10..], 19777);
                    BinaryPrimitives.WriteUInt16LittleEndian(reply[12..], 13621);
                    BinaryPrimitives.WriteUInt16LittleEndian(reply[14..], 8245);
                    size = 0x10;
                    break;
                case RingConCommand.C20105:
                    WriteStatus(reply, DataValid.Valid);
                    reply[4] = 1;
                    size = 8;
                    break;
                case RingConCommand.ReadUnkCal:
                    WriteStatus(reply, DataValid.Valid);
                    size = 8;
                    break;
                case RingConCommand.ReadFactoryCal:
                    WriteStatus(reply, DataValid.Valid);
                    BinaryPrimitives.WriteInt32LittleEndian(reply[4..], IdleValue + Range + IdleDeadzone); // os_max
                    BinaryPrimitives.WriteInt32LittleEndian(reply[8..], IdleValue - Range - IdleDeadzone); // hk_max
                    BinaryPrimitives.WriteInt32LittleEndian(reply[12..], IdleValue - IdleDeadzone); // zero_min
                    BinaryPrimitives.WriteInt32LittleEndian(reply[16..], IdleValue + IdleDeadzone); // zero_max
                    size = 0x14;
                    break;
                case RingConCommand.ReadUserCal:
                    WriteStatus(reply, DataValid.Valid);
                    WriteCalibrationValue(reply[4..], _userOsMax);
                    WriteCalibrationValue(reply[8..], _userHkMax);
                    WriteCalibrationValue(reply[12..], _userZero);
                    size = 0x14;
                    break;
                case RingConCommand.ReadRepCount:
                case RingConCommand.ResetRepCount:
                    size = WriteThreeByteReply(reply, _totalRepCount);
                    break;
                case RingConCommand.ReadTotalPushCount:
                    size = WriteThreeByteReply(reply, _totalPushCount);
                    break;
                case RingConCommand.SaveCalData:
                    WriteStatus(reply, DataValid.Valid);
                    size = 4;
                    break;
                default:
                    WriteStatus(reply, DataValid.BadCrc);
                    size = 8;
                    break;
            }

            size = Math.Min(size, outData.Length);
            reply[..size].CopyTo(outData);

            return (ulong)size;
        }

        private static void WriteStatus(Span<byte> reply, DataValid status)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(reply, (uint)status);
        }

        private static int WriteThreeByteReply(Span<byte> reply, byte value)
        {
            WriteStatus(reply, DataValid.Valid);
            reply[4] = value;
            reply[5] = 0;
            reply[6] = 0;
            reply[7] = GetCrcValue([value, 0, 0, 0]);

            return 8;
        }

        private static CalibrationValue ReadCalibrationValue(ReadOnlySpan<byte> data)
        {
            return new CalibrationValue
            {
                Value = BinaryPrimitives.ReadInt16LittleEndian(data),
                Crc = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]),
            };
        }

        private static void WriteCalibrationValue(Span<byte> data, CalibrationValue value)
        {
            BinaryPrimitives.WriteInt16LittleEndian(data, value.Value);
            BinaryPrimitives.WriteUInt16LittleEndian(data[2..], value.Crc);
        }

        private static byte GetCrcValue(ReadOnlySpan<byte> data)
        {
            byte crc = 0;

            foreach (byte value in data)
            {
                for (int i = 0x80; i > 0; i >>= 1)
                {
                    bool bit = (crc & 0x80) != 0;

                    if ((value & i) != 0)
                    {
                        bit = !bit;
                    }

                    crc <<= 1;

                    if (bit)
                    {
                        crc ^= 0x8d;
                    }
                }
            }

            return crc;
        }
    }
}
