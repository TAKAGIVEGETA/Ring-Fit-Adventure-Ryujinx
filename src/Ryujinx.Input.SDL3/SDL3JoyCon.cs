using Ryujinx.Common.Configuration.Hid;
using Ryujinx.Common.Configuration.Hid.Controller;
using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Services.Hid;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using SDL;
using static SDL.SDL3;

namespace Ryujinx.Input.SDL3
{
    internal unsafe class SDL3JoyCon : IGamepad
    {
        private bool HasConfiguration => _configuration != null;

        private readonly record struct ButtonMappingEntry(GamepadButtonInputId To, GamepadButtonInputId From)
        {
            public bool IsValid => To is not GamepadButtonInputId.Unbound && From is not GamepadButtonInputId.Unbound;
        }

        private StandardControllerInputConfig _configuration;

        private readonly Dictionary<GamepadButtonInputId, SDL_GamepadButton> _leftButtonsDriverMapping = new()
        {
             {GamepadButtonInputId.LeftStick, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_STICK},
             {GamepadButtonInputId.DpadUp, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_WEST},
             {GamepadButtonInputId.DpadDown, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST},
             {GamepadButtonInputId.DpadLeft, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH},
             {GamepadButtonInputId.DpadRight, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_NORTH},
             {GamepadButtonInputId.Minus, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START},
             {GamepadButtonInputId.LeftShoulder, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_PADDLE1},
             {GamepadButtonInputId.LeftTrigger, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_PADDLE2},
             {GamepadButtonInputId.SingleRightTrigger0, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER},
             {GamepadButtonInputId.SingleLeftTrigger0, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_SHOULDER},
        };
        private readonly Dictionary<GamepadButtonInputId, SDL_GamepadButton> _rightButtonsDriverMapping = new()
        {
             {GamepadButtonInputId.RightStick, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_STICK},
             {GamepadButtonInputId.A, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH},
             {GamepadButtonInputId.B, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_WEST},
             {GamepadButtonInputId.X, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST},
             {GamepadButtonInputId.Y, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_NORTH},
             {GamepadButtonInputId.Plus, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START},
             {GamepadButtonInputId.RightShoulder, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_PADDLE1},
             {GamepadButtonInputId.RightTrigger, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_PADDLE2},
             {GamepadButtonInputId.SingleRightTrigger1, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER},
             {GamepadButtonInputId.SingleLeftTrigger1, SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_SHOULDER}
        };

        private readonly Dictionary<GamepadButtonInputId, SDL_GamepadButton> _buttonsDriverMapping;
        private readonly Lock _userMappingLock = new();

        private readonly List<ButtonMappingEntry> _buttonsUserMapping;

        private readonly StickInputId[] _stickUserMapping = new StickInputId[(int)StickInputId.Count]
        {
            StickInputId.Unbound, StickInputId.Left, StickInputId.Right,
        };

        public GamepadFeaturesFlag Features { get; }

        private SDL_Gamepad* _gamepadHandle;
        
        private NpadHdRumble _hdRumble;

        private enum JoyConType
        {
            Left, Right
        }

        public const string Prefix = "Nintendo Switch Joy-Con";
        public const string LeftName = "Nintendo Switch Joy-Con (L)";
        public const string RightName = "Nintendo Switch Joy-Con (R)";

        private readonly JoyConType _joyConType;

        public SDL3JoyCon(SDL_Gamepad* gamepadHandle, string driverId)
        {
            _gamepadHandle = gamepadHandle;
            _hdRumble = NpadHdRumble.Create(gamepadHandle);
            _buttonsUserMapping = new List<ButtonMappingEntry>(10);

            Name = SDL_GetGamepadName(_gamepadHandle);
            Id = driverId;
            Features = GetFeaturesFlag();

            // Enable motion tracking
            if ((Features & GamepadFeaturesFlag.Motion) != 0)
            {
                if (!SDL_SetGamepadSensorEnabled(_gamepadHandle, SDL_SensorType.SDL_SENSOR_ACCEL, true))
                {
                    Logger.Error?.Print(LogClass.Hid,
                        $"Could not enable data reporting for SensorType {SDL_SensorType.SDL_SENSOR_ACCEL}.");
                }

                if (!SDL_SetGamepadSensorEnabled(_gamepadHandle, SDL_SensorType.SDL_SENSOR_GYRO, true))
                {
                    Logger.Error?.Print(LogClass.Hid,
                        $"Could not enable data reporting for SensorType {SDL_SensorType.SDL_SENSOR_GYRO}.");
                }
            }

            switch (Name)
            {
                case LeftName:
                    {
                        _buttonsDriverMapping = _leftButtonsDriverMapping;
                        _joyConType = JoyConType.Left;
                        break;
                    }
                case RightName:
                    {
                        _buttonsDriverMapping = _rightButtonsDriverMapping;
                        _joyConType = JoyConType.Right;
                        break;
                    }
            }
        }

        private GamepadFeaturesFlag GetFeaturesFlag()
        {
            GamepadFeaturesFlag result = GamepadFeaturesFlag.None;

            if (SDL_GamepadHasSensor(_gamepadHandle, SDL_SensorType.SDL_SENSOR_ACCEL) &&
                SDL_GamepadHasSensor(_gamepadHandle, SDL_SensorType.SDL_SENSOR_GYRO))
            {
                result |= GamepadFeaturesFlag.Motion;
            }

            if (SDL_RumbleGamepad(_gamepadHandle, 0, 0, 100))
            {
                result |= GamepadFeaturesFlag.Rumble;
            }

            return result;
        }

        public string Id { get; }
        public string Name { get; }
        public bool IsConnected => SDL_GamepadConnected(_gamepadHandle);

        protected virtual void Dispose(bool disposing)
        {
            if (disposing && _hdRumble != null)
            {
                _hdRumble.Dispose();
            }
            if (disposing && _gamepadHandle != null)
            {
                SDL_CloseGamepad(_gamepadHandle);

                _gamepadHandle = null;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }

        public void SetTriggerThreshold(float triggerThreshold)
        {
            // No operations
        }
        
        public bool HDRumble(VibrationValue left, VibrationValue right)
        {
            return _hdRumble?.HdRumble(left, right) ?? false;
        }

        // Rumble rate limits of SDL's Switch driver: faster writes can turn off a Joy-Con over Bluetooth,
        // and a vibration has to be refreshed to keep the actuator running.
        private const ulong RumbleWriteIntervalMs = 30;
        private const ulong RumbleRefreshIntervalMs = 50;

        private const byte RumbleOutputReportId = 0x10;

        // Amplitude steps of the Joy-Con rumble encoding, see
        // https://github.com/dekuNukem/Nintendo_Switch_Reverse_Engineering/blob/master/rumble_data_table.md
        private static readonly float[] _rumbleAmplitudeSteps =
        [
            0.0f, 0.01f, 0.012f, 0.014f, 0.017f, 0.02f, 0.024f, 0.028f, 0.033f, 0.04f,
            0.047f, 0.056f, 0.067f, 0.08f, 0.095f, 0.112f, 0.117f, 0.123f, 0.128f, 0.134f,
            0.14f, 0.146f, 0.152f, 0.159f, 0.166f, 0.173f, 0.181f, 0.189f, 0.198f, 0.206f,
            0.215f, 0.225f, 0.23f, 0.235f, 0.24f, 0.245f, 0.251f, 0.256f, 0.262f, 0.268f,
            0.273f, 0.279f, 0.286f, 0.292f, 0.298f, 0.305f, 0.311f, 0.318f, 0.325f, 0.332f,
            0.34f, 0.347f, 0.355f, 0.362f, 0.37f, 0.378f, 0.387f, 0.395f, 0.404f, 0.413f,
            0.422f, 0.431f, 0.44f, 0.45f, 0.46f, 0.47f, 0.48f, 0.491f, 0.501f, 0.512f,
            0.524f, 0.535f, 0.547f, 0.559f, 0.571f, 0.584f, 0.596f, 0.609f, 0.623f, 0.636f,
            0.65f, 0.665f, 0.679f, 0.694f, 0.709f, 0.725f, 0.741f, 0.757f, 0.773f, 0.79f,
            0.808f, 0.825f, 0.843f, 0.862f, 0.881f, 0.9f, 0.92f, 0.94f, 0.96f, 0.981f,
            1.003f,
        ];

        // Output report ID, packet number and the rumble data of both actuators.
        private readonly byte[] _rumblePacket = new byte[10];
        private VibrationValue _rumbleValue;
        private bool _isRumblePending;
        private bool _isRumbleActive;
        private bool _isRumbleEffectUnsupported;
        private ulong _lastRumbleWriteMs;

        public bool JoyConRumble(VibrationValue left, VibrationValue right)
        {
            if ((Features & GamepadFeaturesFlag.Rumble) == 0)
            {
                return false;
            }

            _rumbleValue = _joyConType == JoyConType.Left ? left : right;
            _isRumbleActive = _rumbleValue.AmplitudeLow > 0 || _rumbleValue.AmplitudeHigh > 0;
            _isRumblePending = true;

            UpdateRumble();

            return true;
        }

        public void UpdateRumble()
        {
            ulong now = SDL_GetTicks();
            ulong elapsed = now - _lastRumbleWriteMs;

            if (elapsed < RumbleWriteIntervalMs || !(_isRumblePending || (_isRumbleActive && elapsed >= RumbleRefreshIntervalMs)))
            {
                return;
            }

            _isRumblePending = false;
            _lastRumbleWriteMs = now;

            if (!_isRumbleEffectUnsupported)
            {
                // Like Eden's Joy-Con driver, send the vibration as HD rumble, keeping the frequencies requested by the application.
                _rumblePacket[0] = RumbleOutputReportId;
                EncodeRumble(_rumbleValue, _rumblePacket.AsSpan(2, 4));
                _rumblePacket.AsSpan(2, 4).CopyTo(_rumblePacket.AsSpan(6, 4));

                fixed (byte* packet = _rumblePacket)
                {
                    if (SDL_SendGamepadEffect(_gamepadHandle, (IntPtr)packet, _rumblePacket.Length))
                    {
                        return;
                    }
                }

                Logger.Warning?.Print(LogClass.Hid, $"HD rumble isn't supported by {Name}, falling back to regular rumble: {SDL_GetError()}");
                SDL_ClearError();

                _isRumbleEffectUnsupported = true;
            }

            // Same amplitude curve as Eden's SDL driver, which makes weak vibrations noticeable on the Joy-Con actuators.
            static ushort ToRumbleIntensity(float amplitude)
            {
                amplitude = Math.Clamp(amplitude, 0f, 1f);

                return (ushort)((amplitude + MathF.Pow(amplitude, 0.35f)) * 0.5f * ushort.MaxValue);
            }

            SDL_RumbleGamepad(_gamepadHandle, ToRumbleIntensity(_rumbleValue.AmplitudeLow), ToRumbleIntensity(_rumbleValue.AmplitudeHigh), SDL_HAPTIC_INFINITY);
        }

        // Port of Eden's RumbleProtocol::SendVibration encoding.
        private static void EncodeRumble(VibrationValue value, Span<byte> data)
        {
            if (value.AmplitudeLow <= 0 && value.AmplitudeHigh <= 0)
            {
                // Neutral rumble.
                data[0] = 0x00;
                data[1] = 0x01;
                data[2] = 0x40;
                data[3] = 0x40;

                return;
            }

            // Protects the actuators from damage caused by strong vibrations.
            float clampAmplitude = 1f / Math.Max(1f, value.AmplitudeLow + value.AmplitudeHigh);

            ushort highFrequency = unchecked((ushort)((EncodeFrequency(value.FrequencyHigh) - 0x60) * 4));
            byte highAmplitude = (byte)(GetRumbleAmplitudeStep(value.AmplitudeHigh * clampAmplitude) * 2);
            byte lowFrequency = unchecked((byte)(EncodeFrequency(value.FrequencyLow) - 0x40));
            int lowAmplitudeStep = GetRumbleAmplitudeStep(value.AmplitudeLow * clampAmplitude);
            ushort lowAmplitude = (ushort)(((lowAmplitudeStep & 1) != 0 ? 0x8000 : 0) | (0x40 + lowAmplitudeStep / 2));

            data[0] = (byte)(highFrequency & 0xFF);
            data[1] = (byte)(highAmplitude | ((highFrequency >> 8) & 0x01));
            data[2] = (byte)(lowFrequency | ((lowAmplitude >> 8) & 0x80));
            data[3] = (byte)(lowAmplitude & 0xFF);
        }

        private static byte EncodeFrequency(float frequency)
        {
            return (byte)Math.Clamp(MathF.Log2(frequency / 10f) * 32f, 0f, 255f);
        }

        private static int GetRumbleAmplitudeStep(float amplitude)
        {
            for (int i = 0; i < _rumbleAmplitudeSteps.Length; i++)
            {
                if (amplitude <= _rumbleAmplitudeSteps[i])
                {
                    return i;
                }
            }

            return _rumbleAmplitudeSteps.Length - 1;
        }

        public bool Rumble(float lowFrequency, float highFrequency, uint durationMs)
        {
            if ((Features & GamepadFeaturesFlag.Rumble) == 0)
            {
                return false;
            }

            ushort lowFrequencyRaw = (ushort)(lowFrequency * ushort.MaxValue);
            ushort highFrequencyRaw = (ushort)(highFrequency * ushort.MaxValue);

            if (durationMs == uint.MaxValue)
            {
                if (!SDL_RumbleGamepad(_gamepadHandle, lowFrequencyRaw, highFrequencyRaw, SDL_HAPTIC_INFINITY))
                    Logger.Error?.Print(LogClass.Hid, "Rumble is not supported on this game controller.");
            }
            else if (durationMs > SDL_HAPTIC_INFINITY)
            {
                Logger.Error?.Print(LogClass.Hid, $"Unsupported rumble duration {durationMs}");
            }
            else
            {
                if (!SDL_RumbleGamepad(_gamepadHandle, lowFrequencyRaw, highFrequencyRaw, durationMs))
                    Logger.Error?.Print(LogClass.Hid, "Rumble is not supported on this game controller.");
            }
            
            if (!String.IsNullOrEmpty(SDL_GetError()))
            {
                Logger.Error?.PrintMsg(LogClass.Hid, SDL_GetError());
                SDL_ClearError();
                return false;
            }

            return true;
        }

        public Vector3 GetMotionData(MotionInputId inputId)
        {
            SDL_SensorType sensorType = inputId switch
            {
                MotionInputId.Accelerometer => SDL_SensorType.SDL_SENSOR_ACCEL,
                MotionInputId.Gyroscope => SDL_SensorType.SDL_SENSOR_GYRO,
                _ => SDL_SensorType.SDL_SENSOR_INVALID
            };

            if ((Features & GamepadFeaturesFlag.Motion) == 0 || sensorType is SDL_SensorType.SDL_SENSOR_INVALID)
                return Vector3.Zero;

            const int ElementCount = 3;

            float[] values = new float[3];

            fixed (float* pValues = &values[0]) {
                if (!SDL_GetGamepadSensorData(_gamepadHandle, sensorType, pValues, ElementCount))
                    return Vector3.Zero;

                Vector3 value = _joyConType switch
                {
                    JoyConType.Left => new Vector3(-values[2], values[1], values[0]),
                    JoyConType.Right => new Vector3(values[2], values[1], -values[0]),
                    _ => throw new NotSupportedException($"Unsupported JoyCon type: {_joyConType}")
                };

                return inputId switch
                {
                    MotionInputId.Gyroscope => RadToDegree(value),
                    MotionInputId.Accelerometer => GsToMs2(value),
                    _ => value
                };
            }
        }

        private static Vector3 RadToDegree(Vector3 rad) => rad * (180 / MathF.PI);

        private static Vector3 GsToMs2(Vector3 gs) => gs / SDL_STANDARD_GRAVITY;

        public void SetConfiguration(InputConfig configuration)
        {
            lock (_userMappingLock)
            {
                _configuration = (StandardControllerInputConfig)configuration;

                _buttonsUserMapping.Clear();

                // First update sticks
                _stickUserMapping[(int)StickInputId.Left] = (StickInputId)_configuration.LeftJoyconStick.Joystick;
                _stickUserMapping[(int)StickInputId.Right] = (StickInputId)_configuration.RightJoyconStick.Joystick;

                switch (_joyConType)
                {
                    case JoyConType.Left:
                        _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.LeftStick, (GamepadButtonInputId)_configuration.LeftJoyconStick.StickButton));
                        _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.DpadUp, (GamepadButtonInputId)_configuration.LeftJoycon.DpadUp));
                        _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.DpadDown, (GamepadButtonInputId)_configuration.LeftJoycon.DpadDown));
                        _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.DpadLeft, (GamepadButtonInputId)_configuration.LeftJoycon.DpadLeft));
                        _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.DpadRight, (GamepadButtonInputId)_configuration.LeftJoycon.DpadRight));
                        _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.Minus, (GamepadButtonInputId)_configuration.LeftJoycon.ButtonMinus));
                        _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.LeftShoulder, (GamepadButtonInputId)_configuration.LeftJoycon.ButtonL));
                        _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.LeftTrigger, (GamepadButtonInputId)_configuration.LeftJoycon.ButtonZl));
                        _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.SingleRightTrigger0, (GamepadButtonInputId)_configuration.LeftJoycon.ButtonSr));
                        _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.SingleLeftTrigger0, (GamepadButtonInputId)_configuration.LeftJoycon.ButtonSl));
                        break;
                    case JoyConType.Right:
                        _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.RightStick, (GamepadButtonInputId)_configuration.RightJoyconStick.StickButton));
                        _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.A, (GamepadButtonInputId)_configuration.RightJoycon.ButtonA));
                        _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.B, (GamepadButtonInputId)_configuration.RightJoycon.ButtonB));
                        _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.X, (GamepadButtonInputId)_configuration.RightJoycon.ButtonX));
                        _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.Y, (GamepadButtonInputId)_configuration.RightJoycon.ButtonY));
                        _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.Plus, (GamepadButtonInputId)_configuration.RightJoycon.ButtonPlus));
                        _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.RightShoulder, (GamepadButtonInputId)_configuration.RightJoycon.ButtonR));
                        _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.RightTrigger, (GamepadButtonInputId)_configuration.RightJoycon.ButtonZr));
                        _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.SingleRightTrigger1, (GamepadButtonInputId)_configuration.RightJoycon.ButtonSr));
                        _buttonsUserMapping.Add(new ButtonMappingEntry(GamepadButtonInputId.SingleLeftTrigger1, (GamepadButtonInputId)_configuration.RightJoycon.ButtonSl));
                        break;
                    default:
                        throw new NotSupportedException($"Unsupported JoyCon type: {_joyConType}");
                }

                SetTriggerThreshold(_configuration.TriggerThreshold);
            }
        }

        public void SetLed(uint packedRgb)
        {
        }

        public GamepadStateSnapshot GetStateSnapshot()
        {
            return IGamepad.GetStateSnapshot(this);
        }

        public GamepadStateSnapshot GetMappedStateSnapshot()
        {
            GamepadStateSnapshot rawState = GetStateSnapshot();
            GamepadStateSnapshot result = default;

            lock (_userMappingLock)
            {
                if (_buttonsUserMapping.Count == 0)
                    return rawState;

                // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
                foreach (ButtonMappingEntry entry in _buttonsUserMapping)
                {
                    if (!entry.IsValid)
                        continue;

                    // Do not touch state of button already pressed
                    if (!result.IsPressed(entry.To))
                    {
                        result.SetPressed(entry.To, rawState.IsPressed(entry.From));
                    }
                }

                (float leftStickX, float leftStickY) = rawState.GetStick(_stickUserMapping[(int)StickInputId.Left]);
                (float rightStickX, float rightStickY) = rawState.GetStick(_stickUserMapping[(int)StickInputId.Right]);

                result.SetStick(StickInputId.Left, leftStickX, leftStickY);
                result.SetStick(StickInputId.Right, rightStickX, rightStickY);
            }

            return result;
        }

        private static float ConvertRawStickValue(short value)
        {
            const float ConvertRate = 1.0f / (short.MaxValue + 0.5f);

            return value * ConvertRate;
        }

        private JoyconConfigControllerStick<GamepadInputId, Common.Configuration.Hid.Controller.StickInputId>
            GetLogicalJoyStickConfig(StickInputId inputId)
        {
            switch (inputId)
            {
                case StickInputId.Left:
                    if (_configuration.RightJoyconStick.Joystick ==
                        Common.Configuration.Hid.Controller.StickInputId.Left)
                        return _configuration.RightJoyconStick;
                    else
                        return _configuration.LeftJoyconStick;
                case StickInputId.Right:
                    if (_configuration.LeftJoyconStick.Joystick ==
                        Common.Configuration.Hid.Controller.StickInputId.Right)
                        return _configuration.LeftJoyconStick;
                    else
                        return _configuration.RightJoyconStick;
            }

            return null;
        }

        public (float, float) GetStick(StickInputId inputId)
        {
            if (inputId == StickInputId.Unbound)
                return (0.0f, 0.0f);

            if (inputId == StickInputId.Left && _joyConType == JoyConType.Right || inputId == StickInputId.Right && _joyConType == JoyConType.Left)
            {
                return (0.0f, 0.0f);
            }

            (short stickX, short stickY) = GetStickXY();

            float resultX = ConvertRawStickValue(stickX);
            float resultY = -ConvertRawStickValue(stickY);

            if (HasConfiguration)
            {
                JoyconConfigControllerStick<GamepadInputId, Common.Configuration.Hid.Controller.StickInputId> joyconStickConfig = GetLogicalJoyStickConfig(inputId);

                if (joyconStickConfig != null)
                {
                    if (joyconStickConfig.InvertStickX)
                        resultX = -resultX;

                    if (joyconStickConfig.InvertStickY)
                        resultY = -resultY;

                    if (joyconStickConfig.Rotate90CW)
                    {
                        float temp = resultX;
                        resultX = resultY;
                        resultY = -temp;
                    }
                }
            }

            return inputId switch
            {
                StickInputId.Left when _joyConType == JoyConType.Left => (resultY, -resultX),
                StickInputId.Right when _joyConType == JoyConType.Right => (-resultY, resultX),
                _ => (0.0f, 0.0f)
            };
        }

        private (short, short) GetStickXY()
        {
            return (
                SDL_GetGamepadAxis(_gamepadHandle, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX),
                SDL_GetGamepadAxis(_gamepadHandle, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTY));
        }

        public bool IsPressed(GamepadButtonInputId inputId)
        {
            if (!_buttonsDriverMapping.TryGetValue(inputId, out SDL_GamepadButton button))
            {
                return false;
            }

            return SDL_GetGamepadButton(_gamepadHandle, button);
        }
        
        public static bool IsJoyCon(SDL_JoystickID gamepadsId)
        {
            return SDL_GetGamepadNameForID(gamepadsId) is LeftName or RightName;
        }
        
        public static bool IsLeftJoyCon(SDL_JoystickID gamepadsId)
        {
            return SDL_GetGamepadNameForID(gamepadsId) is LeftName;
        }
    }
}
