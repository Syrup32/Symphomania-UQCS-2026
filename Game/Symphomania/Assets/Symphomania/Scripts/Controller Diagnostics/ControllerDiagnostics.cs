using System.Collections.Generic;
using UnityEngine;
using Symphomania.Controllers;

namespace Symphomania.Diagnostics
{
    /// <summary>
    /// Live controller diagnostics overlay.
    ///
    /// Drop this on a single empty GameObject in an otherwise empty scene and
    /// press Play - there is nothing else to set up, no prefabs, no UI canvas.
    /// It draws the split-screen layout and, in each section, the live state of
    /// that instrument's controller.
    ///
    /// What it's for, beyond looking at buttons:
    ///  - Confirms the PID -> instrument -> screen section mapping is right.
    ///  - Confirms each firmware's HID report layout matches what Unity expects
    ///    (a one-bit misalignment is instantly visible as the wrong lamp lighting).
    ///  - The raw HID panel (F11) lists EVERY HID device with its VID/PID, so a
    ///    controller that enumerates with the wrong identity is distinguishable
    ///    from one that doesn't enumerate at all. This is the fast way to test
    ///    the saxophone and trombone end to end for the first time.
    ///
    /// Keys:  F1-F5 pick the keyboard stand-in instrument, F10 toggle layout
    ///        preview mode, F11 raw HID panel, F12 disable/enable the fallback.
    /// </summary>
    [DisallowMultipleComponent]
    public class ControllerDiagnostics : MonoBehaviour
    {
        [Tooltip("Show all five sections even when only some are playable, so the " +
                 "full intended layout is always visible. Toggle at runtime with F10.")]
        public bool ShowAllSections = true;

        [Tooltip("Seconds a momentary event (drum strike, check onset) stays lit so " +
                 "a single-frame signal is actually visible.")]
        public float FlashDuration = 0.12f;

        readonly Dictionary<InstrumentType, InstrumentSnapshot> _snapshots =
            new Dictionary<InstrumentType, InstrumentSnapshot>();

        // Momentary events decay over FlashDuration so the eye can catch them.
        readonly Dictionary<InstrumentType, float[]> _padFlash = new Dictionary<InstrumentType, float[]>();
        readonly Dictionary<InstrumentType, float> _checkFlash = new Dictionary<InstrumentType, float>();

        bool _showRawHid;

        static Texture2D _white;
        GUIStyle _label, _boldLabel, _smallLabel, _header;
        GUIStyle _lampOn, _lampOff, _checkLampOn, _checkLampOff;
        bool _stylesReady;

        void Awake()
        {
            VirtualBandInput.Initialize();

            foreach (var instrument in InstrumentCatalog.All)
            {
                _padFlash[instrument] = new float[8];
                _checkFlash[instrument] = 0f;
            }
        }

        void Update()
        {
            KeyboardInstrumentFallback.PollInstrumentSwitch();

            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null)
            {
                if (kb.f10Key.wasPressedThisFrame) ShowAllSections = !ShowAllSections;
                if (kb.f11Key.wasPressedThisFrame) _showRawHid = !_showRawHid;
            }

            // Sample once per frame here, not in OnGUI - OnGUI runs several times
            // per frame and would double-count or miss edge-triggered events.
            foreach (var instrument in InstrumentCatalog.All)
            {
                var snapshot = VirtualBandInput.Sample(instrument);
                _snapshots[instrument] = snapshot;

                var flashes = _padFlash[instrument];
                for (int i = 0; i < flashes.Length; i++)
                {
                    if ((snapshot.PadsStruck & (1u << i)) != 0) flashes[i] = FlashDuration;
                    else if (flashes[i] > 0f) flashes[i] -= Time.unscaledDeltaTime;
                }

                if (snapshot.CheckStarted) _checkFlash[instrument] = FlashDuration;
                else if (_checkFlash[instrument] > 0f) _checkFlash[instrument] -= Time.unscaledDeltaTime;
            }
        }

        // -------------------------------------------------------------------
        // Drawing
        // -------------------------------------------------------------------

        void EnsureStyles()
        {
            if (_stylesReady) return;

            if (_white == null)
            {
                _white = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
                _white.SetPixel(0, 0, Color.white);
                _white.Apply();
            }

            _label = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true };
            _boldLabel = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, richText = true };
            _smallLabel = new GUIStyle(GUI.skin.label) { fontSize = 11, richText = true };
            _header = new GUIStyle(GUI.skin.label) { fontSize = 14, richText = true };

            // Lamp styles are cached rather than built per lamp: OnGUI runs at
            // least twice a frame (Layout + Repaint) plus once per input event,
            // and there are up to 44 lamps on screen. Allocating a GUIStyle for
            // each would mean well over a hundred allocations per frame of GC
            // churn - inside the very tool you'd use to eyeball timing.
            var lit = new Color(0f, 0f, 0f);
            var unlit = new Color(0.62f, 0.62f, 0.68f);

            _lampOn = new GUIStyle(_smallLabel)
            { alignment = TextAnchor.MiddleCenter, normal = { textColor = lit } };
            _lampOff = new GUIStyle(_smallLabel)
            { alignment = TextAnchor.MiddleCenter, normal = { textColor = unlit } };
            _checkLampOn = new GUIStyle(_label)
            { alignment = TextAnchor.MiddleCenter, normal = { textColor = lit } };
            _checkLampOff = new GUIStyle(_label)
            { alignment = TextAnchor.MiddleCenter, normal = { textColor = new Color(0.6f, 0.6f, 0.65f) } };

            _stylesReady = true;
        }

        static void Fill(Rect rect, Color color)
        {
            var previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, _white);
            GUI.color = previous;
        }

        static void Outline(Rect rect, Color color, float thickness = 2f)
        {
            Fill(new Rect(rect.x, rect.y, rect.width, thickness), color);
            Fill(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), color);
            Fill(new Rect(rect.x, rect.y, thickness, rect.height), color);
            Fill(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), color);
        }

        void OnGUI()
        {
            EnsureStyles();

            var active = ShowAllSections
                ? new List<InstrumentType>(InstrumentCatalog.All)
                : VirtualBandInput.PlayableInstruments();

            if (active.Count == 0)
                active = new List<InstrumentType>(InstrumentCatalog.All);

            Fill(new Rect(0, 0, Screen.width, Screen.height), new Color(0.09f, 0.10f, 0.13f));

            foreach (var instrument in active)
            {
                var rect = BandScreenLayout.GetGuiRect(instrument, active, Screen.width, Screen.height);
                if (rect == Rect.zero) continue;
                DrawSection(instrument, rect);
            }

            DrawTopBar();

            if (_showRawHid) DrawRawHidPanel();
        }

        void DrawSection(InstrumentType instrument, Rect rect)
        {
            var info = InstrumentCatalog.Get(instrument);
            var snapshot = _snapshots.TryGetValue(instrument, out var s) ? s : default;

            bool live = snapshot.Present && !snapshot.IsFallback;
            bool fallback = snapshot.Present && snapshot.IsFallback;

            var background = live ? new Color(0.12f, 0.17f, 0.15f)
                           : fallback ? new Color(0.16f, 0.15f, 0.10f)
                                      : new Color(0.13f, 0.13f, 0.15f);

            var border = live ? new Color(0.30f, 0.75f, 0.45f)
                       : fallback ? new Color(0.80f, 0.68f, 0.25f)
                                  : new Color(0.28f, 0.28f, 0.33f);

            var padded = new Rect(rect.x + 4, rect.y + 4, rect.width - 8, rect.height - 8);
            Fill(padded, background);
            Outline(padded, border);

            float x = padded.x + 12;
            float y = padded.y + 10;
            float width = padded.width - 24;

            string status = live ? "<color=#5FD08A>CONNECTED</color>"
                          : fallback ? "<color=#D9B23F>KEYBOARD</color>"
                          : info.HardwareExists ? "<color=#7A7A85>not connected</color>"
                                                : "<color=#7A7A85>hardware not built</color>";

            GUI.Label(new Rect(x, y, width, 22), $"<b>{info.DisplayName}</b>", _boldLabel);
            y += 20;
            GUI.Label(new Rect(x, y, width, 18), status, _smallLabel);
            y += 16;
            GUI.Label(new Rect(x, y, width, 18),
                $"<color=#7A7A85>PID 0x{info.ProductId:X4}</color>", _smallLabel);
            y += 20;

            // Check input lamp.
            if (info.HasCheckButton)
            {
                bool held = snapshot.Check;
                bool flashing = _checkFlash.TryGetValue(instrument, out var f) && f > 0f;

                var lamp = new Rect(x, y, Mathf.Min(width, 96), 24);
                Fill(lamp, held ? new Color(0.20f, 0.70f, 0.95f)
                        : flashing ? new Color(0.16f, 0.45f, 0.60f)
                                   : new Color(0.20f, 0.21f, 0.25f));
                GUI.Label(lamp, info.CheckLabel, held ? _checkLampOn : _checkLampOff);
                y += 30;
            }
            else
            {
                GUI.Label(new Rect(x, y, width, 18),
                    "<color=#7A7A85>no check input - the hit is the check</color>", _smallLabel);
                y += 24;
            }

            // Fingering lamps.
            int count = info.FingeringCount;
            float gap = 4f;
            float cellWidth = Mathf.Min(58f, (width - gap * (count - 1)) / count);
            float cellHeight = 30f;

            for (int i = 0; i < count; i++)
            {
                bool on = (snapshot.Fingering & (1u << i)) != 0;
                bool struck = _padFlash.TryGetValue(instrument, out var flashes) && flashes[i] > 0f;

                var cell = new Rect(x + i * (cellWidth + gap), y, cellWidth, cellHeight);
                Fill(cell, on ? new Color(0.35f, 0.82f, 0.50f)
                       : struck ? new Color(0.90f, 0.55f, 0.25f)
                                : new Color(0.20f, 0.21f, 0.25f));

                GUI.Label(cell, info.FingeringLabels[i], (on || struck) ? _lampOn : _lampOff);
            }
            y += cellHeight + 8;

            // Canonical mask - what hit-judging will actually compare.
            //
            // Printed LSB-FIRST so character N sits directly under lamp N.
            // Convert.ToString would print MSB-first, which reads backwards from
            // the lamp row above it - and in a tool whose whole job is making a
            // one-bit misalignment obvious, two readouts disagreeing about bit
            // direction is worse than having no readout.
            var bits = new System.Text.StringBuilder(count);
            for (int i = 0; i < count; i++)
                bits.Append((snapshot.Fingering & (1u << i)) != 0 ? '1' : '0');

            GUI.Label(new Rect(x, y, width, 18),
                $"<color=#8A8A95>mask</color> 0x{snapshot.Fingering:X2}  " +
                $"<color=#8A8A95>bit0-&gt;</color>{bits}",
                _smallLabel);
            y += 18;

            if (instrument == InstrumentType.Trombone && snapshot.Fingering != 0)
            {
                int position = 0;
                for (int i = 0; i < 7; i++)
                    if ((snapshot.Fingering & (1u << i)) != 0) { position = i + 1; break; }
                GUI.Label(new Rect(x, y, width, 18),
                    $"<color=#8A8A95>slide position</color> {position}", _smallLabel);
            }

            if (instrument == InstrumentType.Trombone && CountBits(snapshot.Fingering) > 1)
            {
                GUI.Label(new Rect(x, y + 18, width, 18),
                    "<color=#E06060>! multiple positions set - slide should be one-hot</color>",
                    _smallLabel);
            }
        }

        static int CountBits(uint value)
        {
            int n = 0;
            while (value != 0) { n += (int)(value & 1u); value >>= 1; }
            return n;
        }

        void DrawTopBar()
        {
            var bar = new Rect(0, 0, Screen.width, 26);
            Fill(bar, new Color(0f, 0f, 0f, 0.72f));

            int connected = VirtualBandInput.ConnectedInstruments().Count;
            string activeName =
                InstrumentCatalog.TryGet(KeyboardInstrumentFallback.ActiveInstrument, out var activeInfo)
                    ? activeInfo.DisplayName
                    : "none";

            string fallbackState = VirtualBandInput.KeyboardFallbackEnabled
                ? $"<color=#D9B23F>keyboard: {activeName}</color>"
                : "<color=#7A7A85>keyboard off</color>";

            string layoutMode = ShowAllSections ? "all sections" : "session layout";

            GUI.Label(new Rect(10, 4, Screen.width - 20, 20),
                $"<b>Symphomania</b> controller diagnostics   |   " +
                $"<color=#5FD08A>{connected}/5 connected</color>   |   {fallbackState}   |   " +
                $"<color=#7A7A85>{layoutMode}</color>   |   " +
                $"<color=#7A7A85>F1-F5 instrument  F10 layout  F11 raw HID  F12 keyboard</color>",
                _header);
        }

        void DrawRawHidPanel()
        {
            var devices = VirtualBandInput.EnumerateHidDevices();

            float width = Mathf.Min(620f, Screen.width - 40f);
            float height = 60f + devices.Count * 34f;
            var panel = new Rect(Screen.width - width - 20, 40, width, height);

            Fill(panel, new Color(0.05f, 0.05f, 0.07f, 0.96f));
            Outline(panel, new Color(0.45f, 0.45f, 0.55f));

            float x = panel.x + 14;
            float y = panel.y + 10;

            GUI.Label(new Rect(x, y, width - 28, 20),
                "<b>All HID devices seen by Unity</b>", _header);
            y += 22;

            if (devices.Count == 0)
            {
                GUI.Label(new Rect(x, y, width - 28, 20),
                    "<color=#E06060>No HID devices at all.</color> The controller isn't enumerating - " +
                    "check you're plugged into the native USB port, not the UART one.", _smallLabel);
                return;
            }

            foreach (var device in devices)
            {
                bool ours = device.VendorId == InstrumentCatalog.VendorId;
                var expected = device.Instrument;

                // TryGet, not Get: expected can legitimately be None here (a
                // capabilities blob we couldn't parse, or a PID outside the
                // table). A KeyNotFoundException thrown from OnGUI would blank
                // the entire overlay on every IMGUI pass - during exactly the
                // first-plug bring-up session this panel exists for.
                string expectedName = InstrumentCatalog.TryGet(expected, out var expectedInfo)
                    ? expectedInfo.DisplayName
                    : "unknown instrument";

                string line;
                if (device.RecognizedAsVirtualBand)
                {
                    line = $"<color=#5FD08A>OK</color>  {device.Device.displayName}  " +
                           $"<color=#7A7A85>VID 0x{device.VendorId:X4} PID 0x{device.ProductId:X4}</color>  " +
                           $"-> {expectedName}";
                }
                else if (ours && expected != InstrumentType.None)
                {
                    line = $"<color=#D9B23F>??</color>  {device.Device.displayName}  " +
                           $"<color=#7A7A85>VID 0x{device.VendorId:X4} PID 0x{device.ProductId:X4}</color>  " +
                           $"- right IDs but Unity used a generic layout. Restart Play mode; " +
                           $"if it persists the layout registration didn't run.";
                }
                else
                {
                    line = $"<color=#7A7A85>--  {device.Device.displayName}  " +
                           $"VID 0x{device.VendorId:X4} PID 0x{device.ProductId:X4}  (not Virtual Band)</color>";
                }

                GUI.Label(new Rect(x, y, width - 28, 32), line, _smallLabel);
                y += 32;
            }
        }
    }
}
