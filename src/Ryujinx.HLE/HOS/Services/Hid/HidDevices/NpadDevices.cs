using Ryujinx.Common;
using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.HLE.HOS.Services.Hid.Types;
using Ryujinx.HLE.HOS.Services.Hid.Types.SharedMemory.Common;
using Ryujinx.HLE.HOS.Services.Hid.Types.SharedMemory.Npad;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Ryujinx.HLE.HOS.Services.Hid
{
    public class NpadDevices : BaseDevice
    {
        private const int NoMatchNotifyFrequencyMs = 2000;
        private int _activeCount;
        private long _lastNotifyTimestamp;

        public const int MaxControllers = 9; // Players 1-8 and Handheld
        private ControllerType[] _configuredTypes;
        private readonly KEvent[] _styleSetUpdateEvents;
        private readonly bool[] _supportedPlayers;
        // Players disconnected by the application, they stay disconnected until a button is pressed or the input configuration changes.
        private readonly bool[] _disconnectedByApplication;
        // The assignment mode is persistent across controller (re)connections, like on hardware.
        private readonly NpadJoyAssignmentMode[] _joyAssignmentModes;
        // Controller types switched by the application through the assignment mode, they take precedence over the configured types until the input configuration changes.
        private readonly ControllerType[] _assignmentModeTypes;
        // Halves of a JoyconPair that are connected, a single Joy-Con merged in Dual mode only has one of them.
        private readonly bool[] _isDualLeftConnected;
        private readonly bool[] _isDualRightConnected;
        // Like Eden's default vibration value, no vibration.
        private VibrationValue _neutralVibrationValue = new()
        {
            AmplitudeLow = 0f,
            FrequencyLow = 160f,
            AmplitudeHigh = 0f,
            FrequencyHigh = 320f,
        };

        internal NpadJoyHoldType JoyHold { get; set; }
        internal bool SixAxisActive = false; // TODO: link to hidserver when implemented
        internal ControllerType SupportedStyleSets { get; set; }

        public Dictionary<PlayerIndex, ConcurrentQueue<(VibrationValue, VibrationValue)>> RumbleQueues = new();
        public Dictionary<PlayerIndex, (VibrationValue, VibrationValue)> LastVibrationValues = new();

        public NpadDevices(Switch device, bool active = true) : base(device, active)
        {
            _configuredTypes = new ControllerType[MaxControllers];

            SupportedStyleSets = ControllerType.Handheld | ControllerType.JoyconPair |
                                 ControllerType.JoyconLeft | ControllerType.JoyconRight |
                                 ControllerType.ProController;

            _supportedPlayers = new bool[MaxControllers];
            _supportedPlayers.AsSpan().Fill(true);

            _disconnectedByApplication = new bool[MaxControllers];

            _joyAssignmentModes = new NpadJoyAssignmentMode[MaxControllers];
            _joyAssignmentModes.AsSpan().Fill(NpadJoyAssignmentMode.Dual);

            _assignmentModeTypes = new ControllerType[MaxControllers];

            _isDualLeftConnected = new bool[MaxControllers];
            _isDualLeftConnected.AsSpan().Fill(true);
            _isDualRightConnected = new bool[MaxControllers];
            _isDualRightConnected.AsSpan().Fill(true);

            _styleSetUpdateEvents = new KEvent[MaxControllers];
            for (int i = 0; i < _styleSetUpdateEvents.Length; ++i)
            {
                _styleSetUpdateEvents[i] = new KEvent(_device.System.KernelContext);
            }

            _activeCount = 0;

            JoyHold = NpadJoyHoldType.Vertical;
            SixAxisActive = false;
        }

        internal ref KEvent GetStyleSetUpdateEvent(PlayerIndex player)
        {
            return ref _styleSetUpdateEvents[(int)player];
        }

        internal void ClearSupportedPlayers()
        {
            _supportedPlayers.AsSpan().Clear();
        }

        internal void SetSupportedPlayer(PlayerIndex player, bool supported = true)
        {
            if ((uint)player >= _supportedPlayers.Length)
            {
                return;
            }

            _supportedPlayers[(int)player] = supported;
        }

        internal IEnumerable<PlayerIndex> GetSupportedPlayers()
        {
            for (int i = 0; i < _supportedPlayers.Length; ++i)
            {
                if (_supportedPlayers[i])
                {
                    yield return (PlayerIndex)i;
                }
            }
        }

        public bool Validate(int playerMin, int playerMax, ControllerType acceptedTypes, out int configuredCount, out PlayerIndex primaryIndex)
        {
            primaryIndex = PlayerIndex.Unknown;
            configuredCount = 0;

            Span<NpadState> nPadsSpan = _device.Hid.SharedMemory.Npads.AsSpan();

            for (int i = 0; i < MaxControllers; ++i)
            {
                ControllerType npad = _configuredTypes[i];

                if (npad == ControllerType.Handheld && _device.System.State.DockedMode)
                {
                    continue;
                }

                ControllerType currentType = (ControllerType)nPadsSpan[i].InternalState.StyleSet;

                if (currentType != ControllerType.None && (npad & acceptedTypes) != 0 && _supportedPlayers[i])
                {
                    configuredCount++;
                    if (primaryIndex == PlayerIndex.Unknown)
                    {
                        primaryIndex = (PlayerIndex)i;
                    }
                }
            }

            if (configuredCount < playerMin || configuredCount > playerMax || primaryIndex == PlayerIndex.Unknown)
            {
                return false;
            }

            return true;
        }

        internal void SetJoyAssignmentMode(PlayerIndex player, NpadJoyAssignmentMode mode)
        {
            if ((uint)player >= _joyAssignmentModes.Length)
            {
                return;
            }

            _joyAssignmentModes[(int)player] = mode;
            _device.Hid.SharedMemory.Npads[(int)player].InternalState.JoyAssignmentMode = mode;
        }

        // Index of each style in NpadInternalState.SixAxisSensorPropertiesArray (one byte per style).
        private const int SixAxisPropertiesFullKey = 0;
        private const int SixAxisPropertiesHandheld = 1;
        private const int SixAxisPropertiesJoyDualLeft = 2;
        private const int SixAxisPropertiesJoyDualRight = 3;
        private const int SixAxisPropertiesJoyLeft = 4;
        private const int SixAxisPropertiesJoyRight = 5;

        private const ulong SixAxisPropertyIsNewlyAssigned = 1 << 0;

        private static ulong GetNewlyAssignedMask(int propertiesIndex)
        {
            return SixAxisPropertyIsNewlyAssigned << (propertiesIndex * 8);
        }

        internal void ResetIsSixAxisSensorDeviceNewlyAssigned(PlayerIndex player, NpadStyleIndex styleIndex, bool isRightDevice)
        {
            if ((uint)player >= MaxControllers)
            {
                return;
            }

            int propertiesIndex = styleIndex switch
            {
                NpadStyleIndex.Handheld => SixAxisPropertiesHandheld,
                NpadStyleIndex.JoyDual => isRightDevice ? SixAxisPropertiesJoyDualRight : SixAxisPropertiesJoyDualLeft,
                NpadStyleIndex.JoyLeft => SixAxisPropertiesJoyLeft,
                NpadStyleIndex.JoyRight => SixAxisPropertiesJoyRight,
                _ => SixAxisPropertiesFullKey,
            };

            _device.Hid.SharedMemory.Npads[(int)player].InternalState.SixAxisSensorPropertiesArray &= ~GetNewlyAssignedMask(propertiesIndex);
        }

        // Like Eden/hardware: switch JoyDual↔JoyLeft/JoyRight when the assignment mode changes.
        internal void SetNpadMode(PlayerIndex player, NpadJoyAssignmentMode assignmentMode, NpadJoyDeviceType deviceType, out NpadIdType npadIdTypeSet, out bool npadIdTypeIsSet)
        {
            npadIdTypeSet = default;
            npadIdTypeIsSet = false;

            if ((uint)player >= MaxControllers)
            {
                return;
            }

            ref NpadInternalState controller = ref _device.Hid.SharedMemory.Npads[(int)player].InternalState;
            ControllerType currentType = (ControllerType)controller.StyleSet;

            if (currentType == ControllerType.None)
            {
                return;
            }

            ControllerType targetType = currentType;
            bool isDualLeftConnected = true;
            bool isDualRightConnected = true;

            if (assignmentMode == NpadJoyAssignmentMode.Dual)
            {
                // Single → Dual: JoyLeft → JoyDual(left-only), JoyRight → JoyDual(right-only)
                if (currentType is ControllerType.JoyconLeft or ControllerType.JoyconRight)
                {
                    targetType = ControllerType.JoyconPair;
                    isDualLeftConnected = currentType == ControllerType.JoyconLeft;
                    isDualRightConnected = currentType == ControllerType.JoyconRight;
                }
            }
            else if (assignmentMode == NpadJoyAssignmentMode.Single)
            {
                // Dual → Single: JoyDual(left-only) → JoyLeft, JoyDual(right-only) → JoyRight, a full JoyDual depends on deviceType
                if (currentType == ControllerType.JoyconPair)
                {
                    bool isLeft = _isDualLeftConnected[(int)player] && _isDualRightConnected[(int)player]
                        ? deviceType == NpadJoyDeviceType.Left
                        : _isDualLeftConnected[(int)player];

                    targetType = isLeft ? ControllerType.JoyconLeft : ControllerType.JoyconRight;
                }
            }

            if (targetType == currentType)
            {
                return;
            }

            Logger.Info?.Print(LogClass.Hid, $"SetNpadMode: {player} {currentType} → {targetType}, mode {assignmentMode}, dual left {isDualLeftConnected} right {isDualRightConnected}");

            // Set before switching so that Remap doesn't revert it back to the configured type.
            _assignmentModeTypes[(int)player] = targetType;

            SetupNpad(player, ControllerType.None);

            _isDualLeftConnected[(int)player] = isDualLeftConnected;
            _isDualRightConnected[(int)player] = isDualRightConnected;

            SetupNpad(player, targetType);
        }

        internal void DisconnectByApplication(PlayerIndex player)
        {
            if ((uint)player >= _disconnectedByApplication.Length)
            {
                return;
            }

            // The npad is reset on the next input update, see Remap.
            _disconnectedByApplication[(int)player] = true;
        }

        public void Configure(params ReadOnlySpan<ControllerConfig> configs)
        {
            _configuredTypes = new ControllerType[MaxControllers];
            _disconnectedByApplication.AsSpan().Clear();
            _assignmentModeTypes.AsSpan().Clear();
            _isDualLeftConnected.AsSpan().Fill(true);
            _isDualRightConnected.AsSpan().Fill(true);

            for (int i = 0; i < configs.Length; ++i)
            {
                PlayerIndex player = configs[i].Player;
                ControllerType controllerType = configs[i].Type;

                if (player > PlayerIndex.Handheld)
                {
                    throw new InvalidOperationException("Player must be Player1-8 or Handheld");
                }

                if (controllerType == ControllerType.Handheld)
                {
                    player = PlayerIndex.Handheld;
                }

                _configuredTypes[(int)player] = controllerType;

                Logger.Info?.Print(LogClass.Hid, $"Configured Controller {controllerType} to {player}");
            }
        }

        public void Update(IList<GamepadInput> states)
        {
            ReconnectOnButtonPress(states);

            Remap();

            Span<bool> updated = stackalloc bool[10];

            // Update configured inputs
            for (int i = 0; i < states.Count; ++i)
            {
                GamepadInput state = states[i];

                updated[(int)state.PlayerId] = true;

                UpdateInput(state);
            }

            for (int i = 0; i < updated.Length; i++)
            {
                if (!updated[i])
                {
                    UpdateDisconnectedInput((PlayerIndex)i);
                }
            }
        }

        // Like on hardware, pressing a button on a controller disconnected by the application reconnects it.
        private void ReconnectOnButtonPress(IList<GamepadInput> states)
        {
            const ControllerKeys StickDirections = ControllerKeys.LStickLeft | ControllerKeys.LStickUp | ControllerKeys.LStickRight | ControllerKeys.LStickDown |
                                                   ControllerKeys.RStickLeft | ControllerKeys.RStickUp | ControllerKeys.RStickRight | ControllerKeys.RStickDown;

            for (int i = 0; i < states.Count; ++i)
            {
                GamepadInput state = states[i];

                if ((uint)state.PlayerId >= _disconnectedByApplication.Length || !_disconnectedByApplication[(int)state.PlayerId])
                {
                    continue;
                }

                if ((state.Buttons & ~StickDirections) != 0)
                {
                    _disconnectedByApplication[(int)state.PlayerId] = false;

                    Logger.Info?.Print(LogClass.Hid, $"Reconnecting {state.PlayerId} after a button press");
                }
            }
        }

        private void Remap()
        {
            // Remap/Init if necessary
            for (int i = 0; i < MaxControllers; ++i)
            {
                ControllerType config = _configuredTypes[i];

                if (config != ControllerType.None && _assignmentModeTypes[i] != ControllerType.None)
                {
                    config = _assignmentModeTypes[i];
                }

                // Remove Handheld config when Docked
                if (config == ControllerType.Handheld && _device.System.State.DockedMode)
                {
                    config = ControllerType.None;
                }

                // Auto-remap ProController and JoyconPair
                if (config == ControllerType.JoyconPair && (SupportedStyleSets & ControllerType.JoyconPair) == 0 && (SupportedStyleSets & ControllerType.ProController) != 0)
                {
                    config = ControllerType.ProController;
                }
                else if (config == ControllerType.ProController && (SupportedStyleSets & ControllerType.ProController) == 0 && (SupportedStyleSets & ControllerType.JoyconPair) != 0)
                {
                    config = ControllerType.JoyconPair;
                }

                // Check StyleSet and PlayerSet
                if ((config & SupportedStyleSets) == 0 || !_supportedPlayers[i] || _disconnectedByApplication[i])
                {
                    config = ControllerType.None;
                }

                SetupNpad((PlayerIndex)i, config);
            }

            if (_activeCount == 0 && PerformanceCounter.ElapsedMilliseconds > _lastNotifyTimestamp + NoMatchNotifyFrequencyMs)
            {
                Logger.Warning?.Print(LogClass.Hid, $"No matching controllers found. Application requests '{SupportedStyleSets}' on '{string.Join(", ", GetSupportedPlayers())}'");
                _lastNotifyTimestamp = PerformanceCounter.ElapsedMilliseconds;
            }
        }

        private void SetupNpad(PlayerIndex player, ControllerType type)
        {
            ref NpadInternalState controller = ref _device.Hid.SharedMemory.Npads[(int)player].InternalState;

            ControllerType oldType = (ControllerType)controller.StyleSet;

            if (oldType == type)
            {
                return; // Already configured
            }

            controller = NpadInternalState.Create(); // Reset it

            if (type == ControllerType.None)
            {
                _styleSetUpdateEvents[(int)player].ReadableEvent.Signal(); // Signal disconnect
                _activeCount--;

                Logger.Info?.Print(LogClass.Hid, $"Disconnected Controller {oldType} from {player}");

                return;
            }

            // TODO: Allow customizing colors at config
            controller.JoyAssignmentMode = _joyAssignmentModes[(int)player];
            controller.FullKeyColor.FullKeyBody = (uint)NpadColor.BodyGray;
            controller.FullKeyColor.FullKeyButtons = (uint)NpadColor.ButtonGray;
            controller.JoyColor.LeftBody = (uint)NpadColor.BodyNeonBlue;
            controller.JoyColor.LeftButtons = (uint)NpadColor.ButtonGray;
            controller.JoyColor.RightBody = (uint)NpadColor.BodyNeonRed;
            controller.JoyColor.RightButtons = (uint)NpadColor.ButtonGray;

            controller.SystemProperties = NpadSystemProperties.IsPoweredJoyDual |
                                          NpadSystemProperties.IsPoweredJoyLeft |
                                          NpadSystemProperties.IsPoweredJoyRight;

            controller.BatteryLevelJoyDual = NpadBatteryLevel.Percent100;
            controller.BatteryLevelJoyLeft = NpadBatteryLevel.Percent100;
            controller.BatteryLevelJoyRight = NpadBatteryLevel.Percent100;

            switch (type)
            {
#pragma warning disable IDE0055 // Disable formatting
                case ControllerType.ProController:
                    controller.StyleSet           = NpadStyleTag.FullKey;
                    controller.SixAxisSensorPropertiesArray |= GetNewlyAssignedMask(SixAxisPropertiesFullKey);
                    controller.DeviceType         = DeviceType.FullKey;
                    controller.SystemProperties  |= NpadSystemProperties.IsAbxyButtonOriented |
                                                    NpadSystemProperties.IsPlusAvailable      |
                                                    NpadSystemProperties.IsMinusAvailable;
                    controller.AppletFooterUiType = AppletFooterUiType.SwitchProController;
                    break;
                case ControllerType.Handheld:
                    controller.StyleSet           = NpadStyleTag.Handheld;
                    controller.SixAxisSensorPropertiesArray |= GetNewlyAssignedMask(SixAxisPropertiesHandheld);
                    controller.JoyAssignmentMode  = _joyAssignmentModes[(int)player] = NpadJoyAssignmentMode.Dual;
                    controller.DeviceType         = DeviceType.HandheldLeft |
                                                    DeviceType.HandheldRight;
                    controller.SystemProperties  |= NpadSystemProperties.IsAbxyButtonOriented |
                                                    NpadSystemProperties.IsPlusAvailable      |
                                                    NpadSystemProperties.IsMinusAvailable;
                    controller.AppletFooterUiType = AppletFooterUiType.HandheldJoyConLeftJoyConRight;
                    break;
                case ControllerType.JoyconPair:
                    controller.StyleSet           = NpadStyleTag.JoyDual;
                    controller.JoyAssignmentMode  = _joyAssignmentModes[(int)player] = NpadJoyAssignmentMode.Dual;
                    controller.SystemProperties  |= NpadSystemProperties.IsAbxyButtonOriented;

                    if (_isDualLeftConnected[(int)player])
                    {
                        controller.SixAxisSensorPropertiesArray |= GetNewlyAssignedMask(SixAxisPropertiesJoyDualLeft);
                        controller.DeviceType |= DeviceType.JoyLeft;
                        controller.SystemProperties |= NpadSystemProperties.IsMinusAvailable;
                    }

                    if (_isDualRightConnected[(int)player])
                    {
                        controller.SixAxisSensorPropertiesArray |= GetNewlyAssignedMask(SixAxisPropertiesJoyDualRight);
                        controller.DeviceType |= DeviceType.JoyRight;
                        controller.SystemProperties |= NpadSystemProperties.IsPlusAvailable;
                    }

                    if (_isDualLeftConnected[(int)player] && _isDualRightConnected[(int)player])
                    {
                        controller.AppletFooterUiType = _device.System.State.DockedMode ? AppletFooterUiType.JoyDual : AppletFooterUiType.HandheldJoyConLeftJoyConRight;
                    }
                    else
                    {
                        controller.AppletFooterUiType = _isDualLeftConnected[(int)player] ? AppletFooterUiType.JoyDualLeftOnly : AppletFooterUiType.JoyDualRightOnly;
                    }
                    break;
                case ControllerType.JoyconLeft:
                    controller.StyleSet           = NpadStyleTag.JoyLeft;
                    controller.SixAxisSensorPropertiesArray |= GetNewlyAssignedMask(SixAxisPropertiesJoyLeft);
                    controller.DeviceType         = DeviceType.JoyLeft;
                    controller.SystemProperties  |= NpadSystemProperties.IsSlSrButtonOriented |
                                                    NpadSystemProperties.IsMinusAvailable;
                    controller.AppletFooterUiType = _device.System.State.DockedMode ? AppletFooterUiType.JoyDualLeftOnly : AppletFooterUiType.HandheldJoyConLeftOnly;
                    break;
                case ControllerType.JoyconRight:
                    controller.StyleSet           = NpadStyleTag.JoyRight;
                    controller.SixAxisSensorPropertiesArray |= GetNewlyAssignedMask(SixAxisPropertiesJoyRight);
                    controller.DeviceType         = DeviceType.JoyRight;
                    controller.SystemProperties  |= NpadSystemProperties.IsSlSrButtonOriented |
                                                    NpadSystemProperties.IsPlusAvailable;
                    controller.AppletFooterUiType = _device.System.State.DockedMode ? AppletFooterUiType.JoyDualRightOnly : AppletFooterUiType.HandheldJoyConRightOnly;
                    break;
                case ControllerType.Pokeball:
                    controller.StyleSet           = NpadStyleTag.Palma;
                    controller.DeviceType         = DeviceType.Palma;
                    controller.AppletFooterUiType = AppletFooterUiType.None;
                    break;
#pragma warning restore IDE0055
            }

            _styleSetUpdateEvents[(int)player].ReadableEvent.Signal();
            _activeCount++;

            Logger.Info?.Print(LogClass.Hid, $"Connected Controller {type} to {player}");
        }

        private ref RingLifo<NpadCommonState> GetCommonStateLifo(ref NpadInternalState npad)
        {
            switch (npad.StyleSet)
            {
                case NpadStyleTag.FullKey:
                    return ref npad.FullKey;
                case NpadStyleTag.Handheld:
                    return ref npad.Handheld;
                case NpadStyleTag.JoyDual:
                    return ref npad.JoyDual;
                case NpadStyleTag.JoyLeft:
                    return ref npad.JoyLeft;
                case NpadStyleTag.JoyRight:
                    return ref npad.JoyRight;
                case NpadStyleTag.Palma:
                    return ref npad.Palma;
                default:
                    return ref npad.SystemExt;
            }
        }

        private void UpdateUnusedInputIfNotEqual(ref RingLifo<NpadCommonState> currentlyUsed, ref RingLifo<NpadCommonState> possiblyUnused)
        {
            if (!Unsafe.AreSame(ref currentlyUsed, ref possiblyUnused))
            {
                NpadCommonState newState = new();

                WriteNewInputEntry(ref possiblyUnused, ref newState);
            }
        }

        private void WriteNewInputEntry(ref RingLifo<NpadCommonState> lifo, ref NpadCommonState state)
        {
            ref NpadCommonState previousEntry = ref lifo.GetCurrentEntryRef();

            state.SamplingNumber = previousEntry.SamplingNumber + 1;

            lifo.Write(ref state);
        }

        private void UpdateUnusedSixInputIfNotEqual(ref RingLifo<SixAxisSensorState> currentlyUsed, ref RingLifo<SixAxisSensorState> possiblyUnused)
        {
            if (!Unsafe.AreSame(ref currentlyUsed, ref possiblyUnused))
            {
                SixAxisSensorState newState = new();

                WriteNewSixInputEntry(ref possiblyUnused, ref newState);
            }
        }

        private void WriteNewSixInputEntry(ref RingLifo<SixAxisSensorState> lifo, ref SixAxisSensorState state)
        {
            ref SixAxisSensorState previousEntry = ref lifo.GetCurrentEntryRef();

            state.SamplingNumber = previousEntry.SamplingNumber + 1;

            lifo.Write(ref state);
        }

        private void UpdateInput(GamepadInput state)
        {
            if (state.PlayerId == PlayerIndex.Unknown)
            {
                return;
            }

            ref NpadInternalState currentNpad = ref _device.Hid.SharedMemory.Npads[(int)state.PlayerId].InternalState;

            if (currentNpad.StyleSet == NpadStyleTag.None)
            {
                return;
            }

            ref RingLifo<NpadCommonState> lifo = ref GetCommonStateLifo(ref currentNpad);

            NpadCommonState newState = new()
            {
                Buttons = (NpadButton)state.Buttons,
                AnalogStickL = new AnalogStickState
                {
                    X = state.LStick.Dx,
                    Y = state.LStick.Dy,
                },
                AnalogStickR = new AnalogStickState
                {
                    X = state.RStick.Dx,
                    Y = state.RStick.Dy,
                },
                Attributes = NpadAttribute.IsConnected,
            };

            switch (currentNpad.StyleSet)
            {
                case NpadStyleTag.Handheld:
                case NpadStyleTag.FullKey:
                    newState.Attributes |= NpadAttribute.IsWired;
                    break;
                case NpadStyleTag.JoyDual:
                    if (_isDualLeftConnected[(int)state.PlayerId])
                    {
                        newState.Attributes |= NpadAttribute.IsLeftConnected;
                    }

                    if (_isDualRightConnected[(int)state.PlayerId])
                    {
                        newState.Attributes |= NpadAttribute.IsRightConnected;
                    }
                    break;
                case NpadStyleTag.JoyLeft:
                    newState.Attributes |= NpadAttribute.IsLeftConnected;
                    break;
                case NpadStyleTag.JoyRight:
                    newState.Attributes |= NpadAttribute.IsRightConnected;
                    break;
            }

            WriteNewInputEntry(ref lifo, ref newState);

            // Mirror data to Default layout just in case
            if (!currentNpad.StyleSet.HasFlag(NpadStyleTag.SystemExt))
            {
                WriteNewInputEntry(ref currentNpad.SystemExt, ref newState);
            }

            UpdateUnusedInputIfNotEqual(ref lifo, ref currentNpad.FullKey);
            UpdateUnusedInputIfNotEqual(ref lifo, ref currentNpad.Handheld);
            UpdateUnusedInputIfNotEqual(ref lifo, ref currentNpad.JoyDual);
            UpdateUnusedInputIfNotEqual(ref lifo, ref currentNpad.JoyLeft);
            UpdateUnusedInputIfNotEqual(ref lifo, ref currentNpad.JoyRight);
            UpdateUnusedInputIfNotEqual(ref lifo, ref currentNpad.Palma);
        }

        private void UpdateDisconnectedInput(PlayerIndex index)
        {
            ref NpadInternalState currentNpad = ref _device.Hid.SharedMemory.Npads[(int)index].InternalState;

            NpadCommonState newState = new();

            WriteNewInputEntry(ref currentNpad.FullKey, ref newState);
            WriteNewInputEntry(ref currentNpad.Handheld, ref newState);
            WriteNewInputEntry(ref currentNpad.JoyDual, ref newState);
            WriteNewInputEntry(ref currentNpad.JoyLeft, ref newState);
            WriteNewInputEntry(ref currentNpad.JoyRight, ref newState);
            WriteNewInputEntry(ref currentNpad.Palma, ref newState);
        }

        public void UpdateSixAxis(IList<SixAxisInput> states)
        {
            // Like hardware (and Eden), the six-axis sensors are sampled at 200Hz, so write one sample per elapsed 5ms.
            // Applications such as Ring Fit Adventure assume this rate, e.g. to detect steps from the leg strap Joy-Con.
            // Input updates run far more often than that, so most of them must not write any sample.
            long nowNs = PerformanceCounter.ElapsedNanoseconds;

            if (_nextSixAxisSampleNs == 0)
            {
                _nextSixAxisSampleNs = nowNs;
            }

            int sampleCount = 0;

            while (nowNs >= _nextSixAxisSampleNs && sampleCount < MaxSixAxisSamplesPerUpdate)
            {
                WriteSixAxisSample(states);

                _nextSixAxisSampleNs += SixAxisSamplingIntervalNs;
                sampleCount++;
            }

            // After a stall, drop the samples that don't fit in the lifos rather than catching up on them later.
            if (nowNs >= _nextSixAxisSampleNs)
            {
                _nextSixAxisSampleNs = nowNs + SixAxisSamplingIntervalNs;
            }
        }

        private void WriteSixAxisSample(IList<SixAxisInput> states)
        {
            Span<bool> updated = stackalloc bool[10];

            for (int i = 0; i < states.Count; ++i)
            {
                updated[(int)states[i].PlayerId] = true;

                if (SetSixAxisState(states[i]))
                {
                    i++;

                    if (i >= states.Count)
                    {
                        return;
                    }

                    SetSixAxisState(states[i], true);
                }
            }

            for (int i = 0; i < updated.Length; i++)
            {
                if (!updated[i])
                {
                    UpdateDisconnectedInputSixAxis((PlayerIndex)i);
                }
            }
        }

        private const long SixAxisSamplingIntervalNs = 5_000_000;
        // Bounds the samples written after a stall to the capacity of the six-axis lifos.
        private const int MaxSixAxisSamplesPerUpdate = 16;

        private long _nextSixAxisSampleNs;

        private ref RingLifo<SixAxisSensorState> GetSixAxisSensorLifo(ref NpadInternalState npad, bool isRightPair)
        {
            switch (npad.StyleSet)
            {
                case NpadStyleTag.FullKey:
                    return ref npad.FullKeySixAxisSensor;
                case NpadStyleTag.Handheld:
                    return ref npad.HandheldSixAxisSensor;
                case NpadStyleTag.JoyDual:
                    if (isRightPair)
                    {
                        return ref npad.JoyDualRightSixAxisSensor;
                    }
                    else
                    {
                        return ref npad.JoyDualSixAxisSensor;
                    }
                case NpadStyleTag.JoyLeft:
                    return ref npad.JoyLeftSixAxisSensor;
                case NpadStyleTag.JoyRight:
                    return ref npad.JoyRightSixAxisSensor;
                default:
                    throw new NotImplementedException($"{npad.StyleSet}");
            }
        }

        private bool SetSixAxisState(SixAxisInput state, bool isRightPair = false)
        {
            if (state.PlayerId == PlayerIndex.Unknown)
            {
                return false;
            }

            ref NpadInternalState currentNpad = ref _device.Hid.SharedMemory.Npads[(int)state.PlayerId].InternalState;

            if (currentNpad.StyleSet == NpadStyleTag.None)
            {
                return false;
            }

            HidVector accel = new()
            {
                X = state.Accelerometer.X,
                Y = state.Accelerometer.Y,
                Z = state.Accelerometer.Z,
            };

            HidVector gyro = new()
            {
                X = state.Gyroscope.X,
                Y = state.Gyroscope.Y,
                Z = state.Gyroscope.Z,
            };

            HidVector rotation = new()
            {
                X = state.Rotation.X,
                Y = state.Rotation.Y,
                Z = state.Rotation.Z,
            };

            SixAxisSensorState newState = new()
            {
                DeltaTime = SixAxisSamplingIntervalNs,
                Acceleration = accel,
                AngularVelocity = gyro,
                Angle = rotation,
                Attributes = SixAxisSensorAttribute.IsConnected,
            };

            state.Orientation.AsSpan().CopyTo(newState.Direction.AsSpan());

            ref RingLifo<SixAxisSensorState> lifo = ref GetSixAxisSensorLifo(ref currentNpad, isRightPair);

            WriteNewSixInputEntry(ref lifo, ref newState);

            bool needUpdateRight = currentNpad.StyleSet == NpadStyleTag.JoyDual && !isRightPair;

            if (!isRightPair)
            {
                UpdateUnusedSixInputIfNotEqual(ref lifo, ref currentNpad.FullKeySixAxisSensor);
                UpdateUnusedSixInputIfNotEqual(ref lifo, ref currentNpad.HandheldSixAxisSensor);
                UpdateUnusedSixInputIfNotEqual(ref lifo, ref currentNpad.JoyDualSixAxisSensor);
                UpdateUnusedSixInputIfNotEqual(ref lifo, ref currentNpad.JoyLeftSixAxisSensor);
                UpdateUnusedSixInputIfNotEqual(ref lifo, ref currentNpad.JoyRightSixAxisSensor);
            }

            if (!needUpdateRight && !isRightPair)
            {
                SixAxisSensorState emptyState = new()
                {
                    DeltaTime = SixAxisSamplingIntervalNs,
                    Attributes = SixAxisSensorAttribute.IsConnected,
                };

                WriteNewSixInputEntry(ref currentNpad.JoyDualRightSixAxisSensor, ref emptyState);
            }

            return needUpdateRight;
        }
        
        internal bool IsSixAxisSensorAtRest(PlayerIndex player, bool isRightDevice)
        {
            // Same thresholds as yuzu/Eden (MotionInput::IsMoving with IsAtRestStandard).
            const float GyroThreshold = 0.01f; // Rotations per second
            const float AccelMin = 0.9f; // G
            const float AccelMax = 1.1f;

            if ((uint)player >= MaxControllers)
            {
                return true;
            }

            ref NpadInternalState currentNpad = ref _device.Hid.SharedMemory.Npads[(int)player].InternalState;

            if (currentNpad.StyleSet is not (NpadStyleTag.FullKey or NpadStyleTag.Handheld or NpadStyleTag.JoyDual or NpadStyleTag.JoyLeft or NpadStyleTag.JoyRight))
            {
                return true; // It will always be at rest because it cannot move.
            }

            ref SixAxisSensorState storage = ref GetSixAxisSensorLifo(ref currentNpad, isRightDevice && currentNpad.StyleSet == NpadStyleTag.JoyDual).GetCurrentEntryRef();

            float acceleration = MathF.Sqrt(storage.Acceleration.X * storage.Acceleration.X +
                                            storage.Acceleration.Y * storage.Acceleration.Y +
                                            storage.Acceleration.Z * storage.Acceleration.Z);

            float angularVelocity = MathF.Sqrt(storage.AngularVelocity.X * storage.AngularVelocity.X +
                                               storage.AngularVelocity.Y * storage.AngularVelocity.Y +
                                               storage.AngularVelocity.Z * storage.AngularVelocity.Z);

            return angularVelocity < GyroThreshold && acceleration > AccelMin && acceleration < AccelMax;
        }

        private void UpdateDisconnectedInputSixAxis(PlayerIndex index)
        {
            ref NpadInternalState currentNpad = ref _device.Hid.SharedMemory.Npads[(int)index].InternalState;

            SixAxisSensorState newState = new()
            {
                Attributes = SixAxisSensorAttribute.IsConnected,
            };

            WriteNewSixInputEntry(ref currentNpad.FullKeySixAxisSensor, ref newState);
            WriteNewSixInputEntry(ref currentNpad.HandheldSixAxisSensor, ref newState);
            WriteNewSixInputEntry(ref currentNpad.JoyDualSixAxisSensor, ref newState);
            WriteNewSixInputEntry(ref currentNpad.JoyDualRightSixAxisSensor, ref newState);
            WriteNewSixInputEntry(ref currentNpad.JoyLeftSixAxisSensor, ref newState);
            WriteNewSixInputEntry(ref currentNpad.JoyRightSixAxisSensor, ref newState);
        }

        public void UpdateRumbleQueue(PlayerIndex index, Dictionary<byte, VibrationValue> dualVibrationValues)
        {
            if (RumbleQueues.TryGetValue(index, out ConcurrentQueue<(VibrationValue, VibrationValue)> currentQueue))
            {
                if (!dualVibrationValues.TryGetValue(0, out VibrationValue leftVibrationValue))
                {
                    leftVibrationValue = _neutralVibrationValue;
                }

                if (!dualVibrationValues.TryGetValue(1, out VibrationValue rightVibrationValue))
                {
                    rightVibrationValue = _neutralVibrationValue;
                }

                if (!LastVibrationValues.TryGetValue(index, out (VibrationValue, VibrationValue) dualVibrationValue) || !leftVibrationValue.Equals(dualVibrationValue.Item1) || !rightVibrationValue.Equals(dualVibrationValue.Item2))
                {
                    currentQueue.Enqueue((leftVibrationValue, rightVibrationValue));

                    LastVibrationValues[index] = (leftVibrationValue, rightVibrationValue);
                }
            }
        }

        public VibrationValue GetLastVibrationValue(PlayerIndex index, byte position)
        {
            if (!LastVibrationValues.TryGetValue(index, out (VibrationValue, VibrationValue) dualVibrationValue))
            {
                return _neutralVibrationValue;
            }

            return (position == 0) ? dualVibrationValue.Item1 : dualVibrationValue.Item2;
        }

        public ConcurrentQueue<(VibrationValue, VibrationValue)> GetRumbleQueue(PlayerIndex index)
        {
            if (!RumbleQueues.TryGetValue(index, out ConcurrentQueue<(VibrationValue, VibrationValue)> rumbleQueue))
            {
                rumbleQueue = new ConcurrentQueue<(VibrationValue, VibrationValue)>();
                _device.Hid.Npads.RumbleQueues[index] = rumbleQueue;
            }

            return rumbleQueue;
        }
    }
}
