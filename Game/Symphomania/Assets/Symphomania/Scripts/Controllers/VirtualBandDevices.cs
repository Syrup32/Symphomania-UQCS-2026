using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.Layouts;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.Utilities;
#if UNITY_EDITOR
using UnityEditor;
#endif

// ---------------------------------------------------------------------------
// Custom Input System layouts for the five Virtual Band controllers.
//
// WHY CUSTOM LAYOUTS: without these, Unity's HID backend auto-generates a
// generic Joystick layout for anything that reports as a GenericDesktop
// Joystick/Gamepad. That "works" but gives you nameless button0..buttonN with
// no instrument identity, and the mapping would silently depend on descriptor
// details. Registering a layout matched on vendorId+productId means a plugged-in
// trumpet arrives as a TrumpetDevice with a control literally called "valve2".
//
// !!! THE ONE-BYTE GOTCHA !!!
// Every Virtual Band controller uses HID report ID 1. When a device uses report
// IDs, Unity's HID backend includes the report ID as byte 0 of the device state.
// So the first *data* byte is at FieldOffset(1), not 0. Getting this wrong does
// not throw - it silently reads garbage, and every button looks dead or stuck.
// If you ever change the firmware to drop report IDs, set ReportIdBytes to 0 and
// shift every FieldOffset below down by one.
// ---------------------------------------------------------------------------

namespace Symphomania.Controllers
{
    /// <summary>
    /// Implemented by all five device classes so gameplay code can treat them
    /// uniformly without a switch on concrete type.
    /// </summary>
    public interface IVirtualBandDevice
    {
        InstrumentType Instrument { get; }

        /// <summary>
        /// Is the check input currently active (breath held / bow turning)?
        /// Always false for the drum kit, which has no separate check input.
        /// </summary>
        bool CheckActive { get; }

        /// <summary>Did the check input go from inactive to active this frame?</summary>
        bool CheckStartedThisFrame { get; }

        /// <summary>
        /// Canonical fingering bitmask. See VirtualBandInput for the bit
        /// convention - it is shared with the beatmap loader so hit-judging is a
        /// single integer compare.
        /// </summary>
        uint FingeringMask { get; }

        /// <summary>
        /// Drum kit only: bitmask of pads struck this frame (bit 0 = pad 1).
        /// Zero for every other instrument.
        /// </summary>
        uint PadsStruckThisFrame { get; }
    }

    // -----------------------------------------------------------------------
    // Trumpet - PID 0x0001
    // 1 data byte, 4 buttons: breath + 3 valves.
    // -----------------------------------------------------------------------

    [StructLayout(LayoutKind.Explicit, Size = 2)]
    public struct TrumpetHIDInputReport : IInputStateTypeInfo
    {
        public FourCC format => new FourCC('H', 'I', 'D');

        [FieldOffset(0)] public byte reportId;

        [InputControl(name = "breath", layout = "Button", bit = 0, displayName = "Breath")]
        [InputControl(name = "valve1", layout = "Button", bit = 1, displayName = "Valve 1 (forward)")]
        [InputControl(name = "valve2", layout = "Button", bit = 2, displayName = "Valve 2 (middle)")]
        [InputControl(name = "valve3", layout = "Button", bit = 3, displayName = "Valve 3 (back)")]
        [FieldOffset(1)] public byte buttons;
    }

    [InputControlLayout(stateType = typeof(TrumpetHIDInputReport), displayName = "VirtualBand Trumpet")]
    public class TrumpetDevice : InputDevice, IVirtualBandDevice
    {
        public ButtonControl breath { get; private set; }
        public ButtonControl valve1 { get; private set; }
        public ButtonControl valve2 { get; private set; }
        public ButtonControl valve3 { get; private set; }

        protected override void FinishSetup()
        {
            base.FinishSetup();
            breath = GetChildControl<ButtonControl>("breath");
            valve1 = GetChildControl<ButtonControl>("valve1");
            valve2 = GetChildControl<ButtonControl>("valve2");
            valve3 = GetChildControl<ButtonControl>("valve3");
        }

        public InstrumentType Instrument => InstrumentType.Trumpet;
        public bool CheckActive => breath.isPressed;
        public bool CheckStartedThisFrame => breath.wasPressedThisFrame;
        public uint PadsStruckThisFrame => 0u;

        public uint FingeringMask
        {
            get
            {
                uint m = 0;
                if (valve1.isPressed) m |= 1u << 0;
                if (valve2.isPressed) m |= 1u << 1;
                if (valve3.isPressed) m |= 1u << 2;
                return m;
            }
        }
    }

    // -----------------------------------------------------------------------
    // Saxophone - PID 0x0002
    // 2 data bytes, 9 buttons: breath + register + 7 holes.
    // Hole 7 is the lone bit in the second data byte.
    // -----------------------------------------------------------------------

    [StructLayout(LayoutKind.Explicit, Size = 3)]
    public struct SaxophoneHIDInputReport : IInputStateTypeInfo
    {
        public FourCC format => new FourCC('H', 'I', 'D');

        [FieldOffset(0)] public byte reportId;

        [InputControl(name = "breath", layout = "Button", bit = 0, displayName = "Breath")]
        [InputControl(name = "register", layout = "Button", bit = 1, displayName = "Register/Octave key")]
        [InputControl(name = "hole1", layout = "Button", bit = 2, displayName = "Hole 1 (top, index)")]
        [InputControl(name = "hole2", layout = "Button", bit = 3, displayName = "Hole 2 (top, middle)")]
        [InputControl(name = "hole3", layout = "Button", bit = 4, displayName = "Hole 3 (top, ring)")]
        [InputControl(name = "hole4", layout = "Button", bit = 5, displayName = "Hole 4 (bottom, index)")]
        [InputControl(name = "hole5", layout = "Button", bit = 6, displayName = "Hole 5 (bottom, middle)")]
        [InputControl(name = "hole6", layout = "Button", bit = 7, displayName = "Hole 6 (bottom, ring)")]
        [FieldOffset(1)] public byte buttons0;

        [InputControl(name = "hole7", layout = "Button", bit = 0, displayName = "Hole 7 (half-moon C)")]
        [FieldOffset(2)] public byte buttons1;
    }

    [InputControlLayout(stateType = typeof(SaxophoneHIDInputReport), displayName = "VirtualBand Saxophone")]
    public class SaxophoneDevice : InputDevice, IVirtualBandDevice
    {
        public ButtonControl breath { get; private set; }
        public ButtonControl register { get; private set; }
        public ButtonControl[] holes { get; private set; } // index 0 = hole 1

        protected override void FinishSetup()
        {
            base.FinishSetup();
            breath = GetChildControl<ButtonControl>("breath");
            register = GetChildControl<ButtonControl>("register");
            holes = new ButtonControl[7];
            for (int i = 0; i < 7; i++)
                holes[i] = GetChildControl<ButtonControl>("hole" + (i + 1));
        }

        public InstrumentType Instrument => InstrumentType.Saxophone;
        public bool CheckActive => breath.isPressed;
        public bool CheckStartedThisFrame => breath.wasPressedThisFrame;
        public uint PadsStruckThisFrame => 0u;

        public uint FingeringMask
        {
            get
            {
                // Bit 0 = register, bits 1-7 = holes 1-7.
                uint m = 0;
                if (register.isPressed) m |= 1u << 0;
                for (int i = 0; i < 7; i++)
                    if (holes[i].isPressed) m |= 1u << (i + 1);
                return m;
            }
        }
    }

    // -----------------------------------------------------------------------
    // Violin - PID 0x0004
    // 1 data byte, 8 buttons: bow activity + 7 neck switches. Exact fit.
    // -----------------------------------------------------------------------

    [StructLayout(LayoutKind.Explicit, Size = 2)]
    public struct ViolinHIDInputReport : IInputStateTypeInfo
    {
        public FourCC format => new FourCC('H', 'I', 'D');

        [FieldOffset(0)] public byte reportId;

        [InputControl(name = "bow", layout = "Button", bit = 0, displayName = "Bow (encoder turning)")]
        [InputControl(name = "switch1", layout = "Button", bit = 1, displayName = "Position 1 (nearest scroll)")]
        [InputControl(name = "switch2", layout = "Button", bit = 2, displayName = "Position 2")]
        [InputControl(name = "switch3", layout = "Button", bit = 3, displayName = "Position 3")]
        [InputControl(name = "switch4", layout = "Button", bit = 4, displayName = "Position 4")]
        [InputControl(name = "switch5", layout = "Button", bit = 5, displayName = "Position 5")]
        [InputControl(name = "switch6", layout = "Button", bit = 6, displayName = "Position 6")]
        [InputControl(name = "switch7", layout = "Button", bit = 7, displayName = "Position 7 (upper bout)")]
        [FieldOffset(1)] public byte buttons;
    }

    [InputControlLayout(stateType = typeof(ViolinHIDInputReport), displayName = "VirtualBand Violin")]
    public class ViolinDevice : InputDevice, IVirtualBandDevice
    {
        public ButtonControl bow { get; private set; }
        public ButtonControl[] switches { get; private set; } // index 0 = switch 1

        protected override void FinishSetup()
        {
            base.FinishSetup();
            bow = GetChildControl<ButtonControl>("bow");
            switches = new ButtonControl[7];
            for (int i = 0; i < 7; i++)
                switches[i] = GetChildControl<ButtonControl>("switch" + (i + 1));
        }

        public InstrumentType Instrument => InstrumentType.Violin;

        // NOTE: the encoder reports activity only - never direction or speed.
        // Do not judge on suggested_bow_direction from the beatmap; it's cosmetic.
        public bool CheckActive => bow.isPressed;
        public bool CheckStartedThisFrame => bow.wasPressedThisFrame;
        public uint PadsStruckThisFrame => 0u;

        public uint FingeringMask
        {
            get
            {
                uint m = 0;
                for (int i = 0; i < 7; i++)
                    if (switches[i].isPressed) m |= 1u << i;
                return m;
            }
        }
    }

    // -----------------------------------------------------------------------
    // Trombone - PID 0x0005
    // 1 data byte, 8 buttons: breath + 7 one-hot slide positions.
    // The potentiometer is bucketed to a discrete position in firmware; there
    // is NO analog axis in the report.
    // -----------------------------------------------------------------------

    [StructLayout(LayoutKind.Explicit, Size = 2)]
    public struct TromboneHIDInputReport : IInputStateTypeInfo
    {
        public FourCC format => new FourCC('H', 'I', 'D');

        [FieldOffset(0)] public byte reportId;

        [InputControl(name = "breath", layout = "Button", bit = 0, displayName = "Breath")]
        [InputControl(name = "pos1", layout = "Button", bit = 1, displayName = "Slide position 1")]
        [InputControl(name = "pos2", layout = "Button", bit = 2, displayName = "Slide position 2")]
        [InputControl(name = "pos3", layout = "Button", bit = 3, displayName = "Slide position 3")]
        [InputControl(name = "pos4", layout = "Button", bit = 4, displayName = "Slide position 4")]
        [InputControl(name = "pos5", layout = "Button", bit = 5, displayName = "Slide position 5")]
        [InputControl(name = "pos6", layout = "Button", bit = 6, displayName = "Slide position 6")]
        [InputControl(name = "pos7", layout = "Button", bit = 7, displayName = "Slide position 7")]
        [FieldOffset(1)] public byte buttons;
    }

    [InputControlLayout(stateType = typeof(TromboneHIDInputReport), displayName = "VirtualBand Trombone")]
    public class TromboneDevice : InputDevice, IVirtualBandDevice
    {
        public ButtonControl breath { get; private set; }
        public ButtonControl[] positions { get; private set; } // index 0 = position 1

        protected override void FinishSetup()
        {
            base.FinishSetup();
            breath = GetChildControl<ButtonControl>("breath");
            positions = new ButtonControl[7];
            for (int i = 0; i < 7; i++)
                positions[i] = GetChildControl<ButtonControl>("pos" + (i + 1));
        }

        public InstrumentType Instrument => InstrumentType.Trombone;
        public bool CheckActive => breath.isPressed;
        public bool CheckStartedThisFrame => breath.wasPressedThisFrame;
        public uint PadsStruckThisFrame => 0u;

        public uint FingeringMask
        {
            get
            {
                uint m = 0;
                for (int i = 0; i < 7; i++)
                    if (positions[i].isPressed) m |= 1u << i;
                return m;
            }
        }

        /// <summary>
        /// Convenience: 1-7, or 0 if the slide reports nothing. If more than one
        /// bit is somehow set (shouldn't happen - firmware debounces to one
        /// position), returns the lowest.
        /// </summary>
        public int SlidePosition
        {
            get
            {
                for (int i = 0; i < 7; i++)
                    if (positions[i].isPressed) return i + 1;
                return 0;
            }
        }
    }

    // -----------------------------------------------------------------------
    // Drum kit - PID 0x0003
    //
    // PROVISIONAL: this controller is not built yet. The layout below assumes
    // the obvious continuation of the project's conventions - 7 piezo pads on
    // bits 0-6 of one data byte, pad N = button N with NO offset (there is no
    // check button to occupy button 1, because the hit is the check).
    // Re-verify against the firmware once it exists.
    // -----------------------------------------------------------------------

    [StructLayout(LayoutKind.Explicit, Size = 2)]
    public struct DrumKitHIDInputReport : IInputStateTypeInfo
    {
        public FourCC format => new FourCC('H', 'I', 'D');

        [FieldOffset(0)] public byte reportId;

        [InputControl(name = "pad1", layout = "Button", bit = 0, displayName = "Kick")]
        [InputControl(name = "pad2", layout = "Button", bit = 1, displayName = "Snare")]
        [InputControl(name = "pad3", layout = "Button", bit = 2, displayName = "Hi-Hat")]
        [InputControl(name = "pad4", layout = "Button", bit = 3, displayName = "High Tom")]
        [InputControl(name = "pad5", layout = "Button", bit = 4, displayName = "Floor Tom")]
        [InputControl(name = "pad6", layout = "Button", bit = 5, displayName = "Crash")]
        [InputControl(name = "pad7", layout = "Button", bit = 6, displayName = "Ride")]
        [FieldOffset(1)] public byte buttons;
    }

    [InputControlLayout(stateType = typeof(DrumKitHIDInputReport), displayName = "VirtualBand DrumKit")]
    public class DrumKitDevice : InputDevice, IVirtualBandDevice
    {
        public ButtonControl[] pads { get; private set; } // index 0 = pad 1 (Kick)

        protected override void FinishSetup()
        {
            base.FinishSetup();
            pads = new ButtonControl[7];
            for (int i = 0; i < 7; i++)
                pads[i] = GetChildControl<ButtonControl>("pad" + (i + 1));
        }

        public InstrumentType Instrument => InstrumentType.DrumKit;

        // No separate check input - the hit itself is the check.
        public bool CheckActive => false;
        public bool CheckStartedThisFrame => false;

        /// <summary>Which pads are currently held (rarely what you want - see PadsStruckThisFrame).</summary>
        public uint FingeringMask
        {
            get
            {
                uint m = 0;
                for (int i = 0; i < 7; i++)
                    if (pads[i].isPressed) m |= 1u << i;
                return m;
            }
        }

        /// <summary>
        /// Edge-triggered pad strikes. This is what drum hit-judging should use:
        /// a piezo hit is an instant, not a held state.
        /// </summary>
        public uint PadsStruckThisFrame
        {
            get
            {
                uint m = 0;
                for (int i = 0; i < 7; i++)
                    if (pads[i].wasPressedThisFrame) m |= 1u << i;
                return m;
            }
        }
    }

    // -----------------------------------------------------------------------
    // Registration
    // -----------------------------------------------------------------------

    /// <summary>
    /// Registers all five layouts with the Input System. Runs automatically in
    /// both the editor (on domain reload) and in a build (before first scene).
    /// </summary>
#if UNITY_EDITOR
    [InitializeOnLoad]
#endif
    public static class VirtualBandDeviceRegistration
    {
#if UNITY_EDITOR
        static VirtualBandDeviceRegistration()
        {
            Register();
        }
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void Register()
        {
            RegisterOne<TrumpetDevice>(InstrumentType.Trumpet);
            RegisterOne<SaxophoneDevice>(InstrumentType.Saxophone);
            RegisterOne<DrumKitDevice>(InstrumentType.DrumKit);
            RegisterOne<ViolinDevice>(InstrumentType.Violin);
            RegisterOne<TromboneDevice>(InstrumentType.Trombone);
        }

        static void RegisterOne<TDevice>(InstrumentType instrument) where TDevice : InputDevice
        {
            var info = InstrumentCatalog.Get(instrument);
            InputSystem.RegisterLayout<TDevice>(
                matches: new InputDeviceMatcher()
                    .WithInterface("HID")
                    .WithCapability("vendorId", InstrumentCatalog.VendorId)
                    .WithCapability("productId", info.ProductId));
        }
    }
}
