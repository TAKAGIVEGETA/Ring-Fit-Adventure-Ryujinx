using Ryujinx.Common.Logging;
using System;
using System.Threading;
using SDL;
using static SDL.SDL3;

namespace Ryujinx.Input.SDL3
{
    /// <summary>
    /// Drives a physical Ring-Con attached to a right Joy-Con through a raw SDL HID handle.
    /// SDL's own HIDAPI driver keeps handling buttons and motion; this only enables the Joy-Con MCU
    /// external device polling and reads the Ring-Con force from the standard 0x30 input reports.
    /// Protocol ported from yuzu/Eden (input_common/helpers/joycon_protocol).
    /// </summary>
    public unsafe class SDL3RingConDriver : IRingConDriver
    {
        private const ushort NintendoVendorId = 0x057E;
        private const ushort RightJoyConProductId = 0x2007;

        private const byte OutputReportSubCommand = 0x01;
        private const byte InputReportSubCommandReply = 0x21;
        private const byte InputReportStandardFull = 0x30;

        private const byte SubCommandSetReportMode = 0x03;
        private const byte SubCommandSetMcuConfig = 0x21;
        private const byte SubCommandSetMcuState = 0x22;
        private const byte SubCommandEnableImu = 0x40;
        private const byte SubCommandGetExternalDeviceInfo = 0x59;
        private const byte SubCommandEnableExternalPolling = 0x5A;
        private const byte SubCommandSetExternalFormatConfig = 0x5C;

        private const ushort ExternalDeviceRingController = 0x2000;

        // Offset of the Ring-Con value in a 0x30 report (it replaces the third IMU sample).
        private const int RingDataOffset = 0x27;

        private const short DefaultRingRange = 800;

        private const int RetryDelayMs = 2000;
        private const int ReportTimeoutMs = 1000;

        private static readonly byte[] _ringConfig =
        [
            0x06, 0x03, 0x25, 0x06, 0x00, 0x00, 0x00, 0x00, 0x1C, 0x16, 0xED, 0x34, 0x36,
            0x00, 0x00, 0x00, 0x0A, 0x64, 0x0B, 0xE6, 0xA9, 0x22, 0x00, 0x00, 0x04, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x90, 0xA8, 0xE1, 0x34, 0x36,
        ];

        private static readonly byte[] _ringPollingConfig = [0x04, 0x01, 0x01, 0x02];

        private readonly AutoResetEvent _wakeEvent = new(false);
        private readonly byte[] _buffer = new byte[0x170];

        private Thread _thread;
        private volatile bool _isActive;
        private volatile bool _isDisposed;
        private volatile bool _isConnected;
        private volatile float _force;

        private SDL_hid_device* _device;
        private byte _packetCounter;

        private short _calibrationDefault;
        private short _calibrationMax;
        private short _calibrationMin;
        private bool _isCalibrated;

        public bool IsConnected => _isConnected;

        public float Force => _force;

        public void SetActive(bool active)
        {
            if (_isDisposed || _isActive == active)
            {
                return;
            }

            _isActive = active;

            if (active && _thread == null)
            {
                _thread = new Thread(PollingThread)
                {
                    Name = "HID.RingConThread",
                    IsBackground = true,
                };
                _thread.Start();
            }

            _wakeEvent.Set();
        }

        private void PollingThread()
        {
            if (SDL_hid_init() != 0)
            {
                Logger.Error?.Print(LogClass.Hid, $"Ring-Con: SDL_hid_init failed: {SDL_GetError()}");
                return;
            }

            long lastReport = Environment.TickCount64;

            while (!_isDisposed)
            {
                if (!_isActive)
                {
                    Close();
                    _wakeEvent.WaitOne();
                    continue;
                }

                if (_device == null)
                {
                    if (!Open())
                    {
                        _wakeEvent.WaitOne(RetryDelayMs);
                        continue;
                    }

                    lastReport = Environment.TickCount64;
                }

                int length;

                fixed (byte* pBuffer = _buffer)
                {
                    length = SDL_hid_read_timeout(_device, pBuffer, (nuint)_buffer.Length, 50);
                }

                if (length < 0)
                {
                    Logger.Warning?.Print(LogClass.Hid, "Ring-Con: Lost connection to the right Joy-Con");
                    Close();
                    continue;
                }

                if (length > RingDataOffset + 1 && _buffer[0] == InputReportStandardFull)
                {
                    lastReport = Environment.TickCount64;
                    UpdateForce((short)(_buffer[RingDataOffset] | (_buffer[RingDataOffset + 1] << 8)));
                }
                else if (Environment.TickCount64 - lastReport > ReportTimeoutMs)
                {
                    Logger.Warning?.Print(LogClass.Hid, "Ring-Con: No data received, reconnecting");
                    Close();
                }
            }

            Close();
            SDL_hid_exit();
        }

        private bool Open()
        {
            SDL_hid_device_info* devices = SDL_hid_enumerate(NintendoVendorId, RightJoyConProductId);

            for (SDL_hid_device_info* info = devices; info != null && _device == null; info = info->next)
            {
                _device = SDL_hid_open_path(info->path);
            }

            SDL_hid_free_enumeration(devices);

            if (_device == null)
            {
                return false;
            }

            if (!EnableRingCon())
            {
                Close();
                return false;
            }

            _isCalibrated = false;
            _isConnected = true;

            Logger.Info?.Print(LogClass.Hid, "Ring-Con: Connected");

            return true;
        }

        private void Close()
        {
            if (_device != null)
            {
                // Turn the MCU off so the Joy-Con goes back to its normal state.
                SendSubCommand(SubCommandSetMcuState, [0x00]);
                SDL_hid_close(_device);
                _device = null;

                Logger.Info?.Print(LogClass.Hid, "Ring-Con: Disconnected");
            }

            _isConnected = false;
            _force = 0f;
        }

        private bool EnableRingCon()
        {
            if (!SendSubCommand(SubCommandSetReportMode, [InputReportStandardFull]) ||
                !SendSubCommand(SubCommandEnableImu, [0x01]) ||
                !SendSubCommand(SubCommandSetMcuState, [0x01]))
            {
                return false;
            }

            // ConfigureMCU / SetDeviceMode / Standby
            byte[] mcuConfig = new byte[0x26];
            mcuConfig[0] = 0x21;
            mcuConfig[1] = 0x01;
            mcuConfig[2] = 0x01;
            mcuConfig[37] = CalculateMcuCrc8(mcuConfig.AsSpan(1, 36));

            if (!SendSubCommand(SubCommandSetMcuConfig, mcuConfig))
            {
                return false;
            }

            if (!IsRingConnected())
            {
                Logger.Debug?.Print(LogClass.Hid, "Ring-Con: No Ring-Con attached to the right Joy-Con");
                return false;
            }

            return SendSubCommand(SubCommandSetExternalFormatConfig, _ringConfig) &&
                   SendSubCommand(SubCommandEnableExternalPolling, _ringPollingConfig);
        }

        private bool IsRingConnected()
        {
            const int MaxTries = 42;

            for (int tries = 0; tries < MaxTries; tries++)
            {
                if (SendSubCommand(SubCommandGetExternalDeviceInfo, [], out int replyLength) &&
                    replyLength > 16 &&
                    (_buffer[15] | (_buffer[16] << 8)) == ExternalDeviceRingController)
                {
                    return true;
                }

                if (_isDisposed || !_isActive)
                {
                    return false;
                }
            }

            return false;
        }

        private bool SendSubCommand(byte subCommand, ReadOnlySpan<byte> data)
        {
            return SendSubCommand(subCommand, data, out _);
        }

        private bool SendSubCommand(byte subCommand, ReadOnlySpan<byte> data, out int replyLength)
        {
            const int MaxTries = 20;

            replyLength = 0;

            Span<byte> packet = stackalloc byte[0x31];
            packet.Clear();

            packet[0] = OutputReportSubCommand;
            packet[1] = (byte)(_packetCounter++ & 0xF);
            // Neutral rumble data
            packet[2] = 0x00;
            packet[3] = 0x01;
            packet[4] = 0x40;
            packet[5] = 0x40;
            packet[6] = 0x00;
            packet[7] = 0x01;
            packet[8] = 0x40;
            packet[9] = 0x40;
            packet[10] = subCommand;
            data.CopyTo(packet[11..]);

            fixed (byte* pPacket = packet)
            {
                if (SDL_hid_write(_device, pPacket, (nuint)packet.Length) < 0)
                {
                    return false;
                }
            }

            for (int tries = 0; tries < MaxTries; tries++)
            {
                int length;

                fixed (byte* pBuffer = _buffer)
                {
                    length = SDL_hid_read_timeout(_device, pBuffer, (nuint)_buffer.Length, 66);
                }

                if (length < 0)
                {
                    return false;
                }

                if (length > 14 && _buffer[0] == InputReportSubCommandReply && _buffer[14] == subCommand)
                {
                    replyLength = length;
                    return true;
                }
            }

            return false;
        }

        private void UpdateForce(short value)
        {
            // TODO: Get the calibration from the Ring-Con itself, like Eden this is computed at runtime.
            if (!_isCalibrated)
            {
                _calibrationDefault = value;
                _calibrationMax = (short)(value + DefaultRingRange);
                _calibrationMin = (short)(value - DefaultRingRange);
                _isCalibrated = true;
            }

            _calibrationMax = Math.Max(_calibrationMax, value);
            _calibrationMin = Math.Min(_calibrationMin, value);

            float normalized = value - _calibrationDefault;

            if (normalized > 0)
            {
                normalized /= _calibrationMax - _calibrationDefault;
            }
            else if (normalized < 0)
            {
                normalized /= _calibrationDefault - _calibrationMin;
            }

            _force = normalized;
        }

        // CRC-8-CCITT (polynomial 0x07)
        private static byte CalculateMcuCrc8(ReadOnlySpan<byte> data)
        {
            byte crc = 0;

            foreach (byte value in data)
            {
                crc ^= value;

                for (int i = 0; i < 8; i++)
                {
                    crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1);
                }
            }

            return crc;
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _isActive = false;
            _wakeEvent.Set();
            _thread?.Join();
            _wakeEvent.Dispose();

            GC.SuppressFinalize(this);
        }
    }
}
