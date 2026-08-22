using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Symphomania.Controllers
{
    /// <summary>
    /// One frame's worth of input for one instrument, in a form gameplay code
    /// can consume without knowing whether it came from real hardware or the
    /// keyboard fallback.
    /// </summary>
    public struct InstrumentSnapshot
    {
        public InstrumentType Instrument;

        /// <summary>False when nothing is providing input for this instrument.</summary>
        public bool Present;

        /// <summary>True when this came from the keyboard stand-in, not a controller.</summary>
        public bool IsFallback;

        /// <summary>Check input held (breath / bow turning). Always false for drums.</summary>
        public bool Check;

        /// <summary>Check input went active this frame - the edge hit-judging cares about.</summary>
        public bool CheckStarted;

        /// <summary>Canonical fingering bits. See <see cref="VirtualBandInput"/> for the convention.</summary>
        public uint Fingering;

        /// <summary>Drums only: pads struck this frame, bit 0 = pad 1.</summary>
        public uint PadsStruck;
    }

    /// <summary>
    /// Central registry of connected Virtual Band controllers.
    ///
    /// CANONICAL FINGERING MASK CONVENTION
    /// -----------------------------------
    /// Both this class and the beatmap loader reduce an instrument's fingering
    /// state to one uint, so judging a hit is `live.Fingering == note.Fingering`
    /// rather than comparing JSON objects field by field every frame.
    ///
    ///   Trumpet    bit 0..2  = valve 1..3
    ///   Saxophone  bit 0     = register/octave key
    ///              bit 1..7  = hole 1..7
    ///   Violin     bit 0..6  = switch 1..7
    ///   Trombone   bit 0..6  = slide position 1..7 (one-hot)
    ///   Drum kit   bit 0..6  = pad 1..7 (use PadsStruck, not Fingering)
    ///
    /// Note this is deliberately NOT the raw HID byte: the HID reports shift
    /// fingering up by one bit to keep the check input on button 1. Converting
    /// here once means the rest of the game never thinks about that offset.
    ///
    /// Only hardware-verifiable fields appear here. Cosmetic beatmap fields
    /// (violin's suggested_bow_direction) are deliberately absent so they cannot
    /// leak into hit-judging.
    /// </summary>
    public static class VirtualBandInput
    {
        public class ConnectedController
        {
            public InstrumentType Instrument;
            public InputDevice Device;
            public IVirtualBandDevice Band;
            public string DisplayName;
        }

        static readonly Dictionary<InstrumentType, ConnectedController> _connected =
            new Dictionary<InstrumentType, ConnectedController>();

        static bool _initialized;

        /// <summary>
        /// When true, an instrument with no physical controller falls back to the
        /// keyboard so gameplay can be developed and tested with nothing plugged
        /// in. Leave on during development; turn off for a real session.
        /// </summary>
        public static bool KeyboardFallbackEnabled = true;

        public static event Action<ConnectedController> ControllerConnected;
        public static event Action<InstrumentType> ControllerDisconnected;

        /// <summary>
        /// Wipes all static state at the start of every Play session.
        ///
        /// This exists because of "Enter Play Mode Options" with domain reload
        /// disabled - a setting people turn on for faster iteration. With it on,
        /// statics survive between Play sessions, so without this hook the second
        /// Play would find _initialized already true, skip re-subscribing to
        /// onDeviceChange (the Input System replaced its manager, so the old
        /// subscription is gone), and leave _connected full of dead InputDevice
        /// instances from the previous session. Every controller would read as
        /// connected but permanently frozen. SubsystemRegistration runs before
        /// BeforeSceneLoad on every Play, reload or not.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            InputSystem.onDeviceChange -= OnDeviceChange;
            _connected.Clear();
            ControllerConnected = null;
            ControllerDisconnected = null;
            KeyboardFallbackEnabled = true;
            KeyboardInstrumentFallback.ResetStatics();
            _initialized = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            VirtualBandDeviceRegistration.Register();

            InputSystem.onDeviceChange -= OnDeviceChange; // never double-subscribe
            InputSystem.onDeviceChange += OnDeviceChange;

            // Pick up anything already plugged in before we started listening.
            foreach (var device in InputSystem.devices)
                TryAdd(device);
        }

        static void OnDeviceChange(InputDevice device, InputDeviceChange change)
        {
            // Deliberately NOT handling Enabled/Disabled. Disabled is not an
            // unplug - the Input System raises it on focus loss and on soft
            // reset, so treating it as a disconnect would drop a player's
            // instrument mid-song when they alt-tabbed (and, with the keyboard
            // fallback on, silently hand their lane to the keyboard). Only
            // physical presence changes move the registry.
            switch (change)
            {
                case InputDeviceChange.Added:
                case InputDeviceChange.Reconnected:
                    TryAdd(device);
                    break;

                case InputDeviceChange.Removed:
                case InputDeviceChange.Disconnected:
                    TryRemove(device);
                    break;
            }
        }

        static void TryAdd(InputDevice device)
        {
            if (!(device is IVirtualBandDevice band)) return;

            var instrument = band.Instrument;

            if (_connected.TryGetValue(instrument, out var existing))
            {
                if (existing.Device == device) return;

                // Two controllers claiming the same instrument. Sessions are
                // one-of-each by design, so first plugged in wins and we say so
                // loudly rather than silently swapping which one the player is
                // holding mid-song.
                Debug.LogWarning(
                    $"[VirtualBand] A second {instrument} controller was connected " +
                    $"({Describe(device)}). Ignoring it - already using {Describe(existing.Device)}.");
                return;
            }

            var entry = new ConnectedController
            {
                Instrument = instrument,
                Device = device,
                Band = band,
                DisplayName = Describe(device),
            };

            _connected[instrument] = entry;
            Debug.Log($"[VirtualBand] {instrument} connected ({entry.DisplayName}).");
            ControllerConnected?.Invoke(entry);
        }

        static void TryRemove(InputDevice device)
        {
            if (!(device is IVirtualBandDevice band)) return;

            var instrument = band.Instrument;
            if (_connected.TryGetValue(instrument, out var entry) && entry.Device == device)
            {
                _connected.Remove(instrument);
                Debug.Log($"[VirtualBand] {instrument} disconnected.");
                ControllerDisconnected?.Invoke(instrument);
            }
        }

        static string Describe(InputDevice device)
        {
            var product = device.description.product;
            return string.IsNullOrEmpty(product) ? device.displayName : product;
        }

        /// <summary>Is a physical controller for this instrument connected right now?</summary>
        public static bool IsConnected(InstrumentType instrument) =>
            _connected.ContainsKey(instrument);

        public static ConnectedController GetController(InstrumentType instrument) =>
            _connected.TryGetValue(instrument, out var c) ? c : null;

        /// <summary>
        /// Instruments with a physical controller attached, in screen order.
        /// This is one half of the intersection described in beatmap_schema.md:
        /// a session plays the instruments that are BOTH in this list AND present
        /// in the loaded beatmap.
        /// </summary>
        public static List<InstrumentType> ConnectedInstruments()
        {
            var result = new List<InstrumentType>();
            foreach (var instrument in InstrumentCatalog.All)
                if (_connected.ContainsKey(instrument))
                    result.Add(instrument);
            return result;
        }

        /// <summary>
        /// Instruments that will produce input this frame - connected hardware,
        /// plus keyboard stand-ins if the fallback is on.
        /// </summary>
        public static List<InstrumentType> PlayableInstruments()
        {
            var result = new List<InstrumentType>();
            foreach (var instrument in InstrumentCatalog.All)
            {
                if (_connected.ContainsKey(instrument) ||
                    (KeyboardFallbackEnabled && KeyboardInstrumentFallback.IsActiveFor(instrument)))
                    result.Add(instrument);
            }
            return result;
        }

        /// <summary>Read this frame's input for one instrument.</summary>
        public static InstrumentSnapshot Sample(InstrumentType instrument)
        {
            if (_connected.TryGetValue(instrument, out var c))
            {
                return new InstrumentSnapshot
                {
                    Instrument = instrument,
                    Present = true,
                    IsFallback = false,
                    Check = c.Band.CheckActive,
                    CheckStarted = c.Band.CheckStartedThisFrame,
                    Fingering = c.Band.FingeringMask,
                    PadsStruck = c.Band.PadsStruckThisFrame,
                };
            }

            if (KeyboardFallbackEnabled && KeyboardInstrumentFallback.IsActiveFor(instrument))
                return KeyboardInstrumentFallback.Sample(instrument);

            return new InstrumentSnapshot { Instrument = instrument, Present = false };
        }

        // -------------------------------------------------------------------
        // Diagnostics helper: every HID device the OS is showing us, whether or
        // not we recognized it. If a controller is plugged in but not appearing
        // as a Virtual Band device, its VID/PID will show up here - which tells
        // you the firmware enumerated but the identity is wrong, as opposed to
        // the device not enumerating at all.
        // -------------------------------------------------------------------

        [Serializable]
        struct HidCapabilities
        {
            public int vendorId;
            public int productId;
        }

        public struct RawHidDevice
        {
            public InputDevice Device;
            public int VendorId;
            public int ProductId;
            public bool RecognizedAsVirtualBand;

            /// <summary>
            /// Which instrument this is, taken from the device itself when Unity
            /// recognized it, and only guessed from the PID otherwise. None if
            /// neither is available. Read from the device rather than the PID so
            /// a device whose capabilities JSON failed to parse still reports
            /// correctly.
            /// </summary>
            public InstrumentType Instrument;
        }

        public static List<RawHidDevice> EnumerateHidDevices()
        {
            var result = new List<RawHidDevice>();

            foreach (var device in InputSystem.devices)
            {
                if (device.description.interfaceName != "HID") continue;

                int vid = 0, pid = 0;
                var caps = device.description.capabilities;
                if (!string.IsNullOrEmpty(caps))
                {
                    try
                    {
                        var parsed = JsonUtility.FromJson<HidCapabilities>(caps);
                        vid = parsed.vendorId;
                        pid = parsed.productId;
                    }
                    catch
                    {
                        // Non-fatal: some platforms report capabilities we can't
                        // parse. The device still shows up in the list, just
                        // without IDs.
                    }
                }

                var band = device as IVirtualBandDevice;

                result.Add(new RawHidDevice
                {
                    Device = device,
                    VendorId = vid,
                    ProductId = pid,
                    RecognizedAsVirtualBand = band != null,
                    Instrument = band != null
                        ? band.Instrument
                        : InstrumentCatalog.FromProductId(pid),
                });
            }

            return result;
        }
    }
}
