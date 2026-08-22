using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace Symphomania.Controllers
{
    /// <summary>
    /// Keyboard stand-in for a controller, so beatmap loading, scrolling,
    /// judging and scoring can all be built and tested with nothing plugged in.
    ///
    /// Rather than trying to fit five instruments onto one keyboard at once
    /// (which collides immediately - the sax alone wants 9 signals), exactly one
    /// instrument is "held" at a time and the same keys mean different things
    /// depending on which. Cycle with F1-F5.
    ///
    ///   F1 Trumpet   F2 Saxophone   F3 Drum Kit   F4 Violin   F5 Trombone
    ///   F12          disable the fallback entirely
    ///
    ///   Space        check input (breath / bow). Unused for drums.
    ///   1..8         fingering signals, in canonical mask bit order:
    ///                  trumpet    1-3 = valves 1-3
    ///                  saxophone  1   = register, 2-8 = holes 1-7
    ///                  violin     1-7 = switches 1-7
    ///                  trombone   1-7 = slide positions (last pressed wins)
    ///                  drums      1-7 = pads 1-7 (strike on keydown)
    ///
    /// This is a development aid, not a play mode. It cannot represent the
    /// analog feel of breath or bowing, and it lets you press key combinations
    /// the real hardware can't produce (two trombone slide positions at once) -
    /// so a chart that passes on keyboard is not proof it passes on hardware.
    /// </summary>
    public static class KeyboardInstrumentFallback
    {
        /// <summary>Which instrument the keyboard is currently standing in for.</summary>
        public static InstrumentType ActiveInstrument = InstrumentType.Trumpet;

        static int _lastTrombonePosition;

        /// <summary>
        /// Clears state that would otherwise survive into the next Play session
        /// when domain reload is disabled. Called from VirtualBandInput's reset.
        /// </summary>
        public static void ResetStatics()
        {
            ActiveInstrument = InstrumentType.Trumpet;
            _lastTrombonePosition = 0;
        }

        public static bool IsActiveFor(InstrumentType instrument) =>
            ActiveInstrument == instrument && Keyboard.current != null;

        /// <summary>
        /// Call once per frame from a MonoBehaviour Update (the diagnostics
        /// overlay does this) to handle the F-key instrument switching.
        /// </summary>
        public static void PollInstrumentSwitch()
        {
            var kb = Keyboard.current;
            if (kb == null) return;

            if (kb.f1Key.wasPressedThisFrame) ActiveInstrument = InstrumentType.Trumpet;
            if (kb.f2Key.wasPressedThisFrame) ActiveInstrument = InstrumentType.Saxophone;
            if (kb.f3Key.wasPressedThisFrame) ActiveInstrument = InstrumentType.DrumKit;
            if (kb.f4Key.wasPressedThisFrame) ActiveInstrument = InstrumentType.Violin;
            if (kb.f5Key.wasPressedThisFrame) ActiveInstrument = InstrumentType.Trombone;

            if (kb.f12Key.wasPressedThisFrame)
                VirtualBandInput.KeyboardFallbackEnabled = !VirtualBandInput.KeyboardFallbackEnabled;
        }

        static KeyControl DigitKey(Keyboard kb, int oneBased)
        {
            switch (oneBased)
            {
                case 1: return kb.digit1Key;
                case 2: return kb.digit2Key;
                case 3: return kb.digit3Key;
                case 4: return kb.digit4Key;
                case 5: return kb.digit5Key;
                case 6: return kb.digit6Key;
                case 7: return kb.digit7Key;
                case 8: return kb.digit8Key;
                default: return null;
            }
        }

        public static InstrumentSnapshot Sample(InstrumentType instrument)
        {
            var kb = Keyboard.current;
            var snapshot = new InstrumentSnapshot
            {
                Instrument = instrument,
                Present = true,
                IsFallback = true,
            };

            if (kb == null || ActiveInstrument != instrument)
            {
                snapshot.Present = false;
                return snapshot;
            }

            var info = InstrumentCatalog.Get(instrument);

            if (info.HasCheckButton)
            {
                snapshot.Check = kb.spaceKey.isPressed;
                snapshot.CheckStarted = kb.spaceKey.wasPressedThisFrame;
            }

            uint fingering = 0;
            uint struck = 0;

            for (int i = 0; i < info.FingeringCount && i < 8; i++)
            {
                var key = DigitKey(kb, i + 1);
                if (key == null) continue;

                if (key.isPressed) fingering |= 1u << i;
                if (key.wasPressedThisFrame) struck |= 1u << i;
            }

            if (instrument == InstrumentType.Trombone)
            {
                // The real slide is one-hot - it physically cannot be in two
                // positions. Collapse to the most recently pressed key that is
                // STILL HELD, so the fallback can't produce input the hardware
                // never would.
                //
                // The "still held" part matters: latching purely on key-down
                // leaves the latch pointing at a released key (hold 3, tap 5,
                // release 5 -> still reports 5 while only 3 is down), which looks
                // exactly like a HID bit-order fault and would send you hunting
                // through firmware for a bug that isn't there.
                for (int i = 0; i < 7; i++)
                    if ((struck & (1u << i)) != 0) _lastTrombonePosition = i + 1;

                bool latchStillHeld =
                    _lastTrombonePosition >= 1 && _lastTrombonePosition <= 7 &&
                    (fingering & (1u << (_lastTrombonePosition - 1))) != 0;

                if (!latchStillHeld)
                {
                    // Latched key was released - fall back to the lowest key
                    // still down, or nothing.
                    _lastTrombonePosition = 0;
                    for (int i = 0; i < 7; i++)
                    {
                        if ((fingering & (1u << i)) != 0)
                        {
                            _lastTrombonePosition = i + 1;
                            break;
                        }
                    }
                }

                fingering = _lastTrombonePosition == 0
                    ? 0u
                    : 1u << (_lastTrombonePosition - 1);
            }

            snapshot.Fingering = fingering;

            if (instrument == InstrumentType.DrumKit)
                snapshot.PadsStruck = struck;

            return snapshot;
        }
    }
}
