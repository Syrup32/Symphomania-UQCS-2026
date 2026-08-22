// ============================================================================
// Virtual Band - Drum Kit Controller
// ESP32-S3, native USB HID gamepad
//
// Built on top of the working piezo-tap-detection logic from the earlier
// "ESP32-S3-WROOM-1 | Piezo Sensor Test" sketch (single sensor on GPIO12/
// ADC2_CH1) -- same threshold/debounce approach, just replicated across 7
// sensors on ADC1 pins and wired into a USB HID report instead of Serial.
//
// Confirmed on real hardware: this board is an N8R2 module, but still
// requires the Freenove-family "Hardware CDC and JTAG" USB Mode setting
// (not "USB-OTG (TinyUSB)") for the custom VID/PID to take effect -- same
// counter-intuitive gotcha documented for the trumpet/trombone, just on a
// different board family than expected. Also confirmed: Flash Size/PSRAM
// must be set to this board's actual N8R2 spec (8MB flash, QSPI PSRAM,
// NOT the Freenove N8R8's OPI PSRAM), and "USB Firmware MSC On Boot" must
// be Disabled -- leaving it Enabled makes the board enumerate as a USB
// mass-storage drive in File Explorer instead of (only) a HID gamepad.
//
// UNLIKE every other controller in this project, the drum kit has NO
// separate "check" input on button 1. The physical hit itself is both the
// note selection and the timing check -- there's nothing else to press
// first, so there's no button-1-as-check convention here (this is called
// out as the deliberate exception in the project's own design notes).
//
// HID report: 1 byte, 7 buttons used (bit 7 is unused padding), report ID 1.
//   Button 1 (bit 0) = Crash cymbal   (leftmost pad)
//   Button 2 (bit 1) = Snare drum
//   Button 3 (bit 2) = High Tom
//   Button 4 (bit 3) = Kick / Bass drum
//   Button 5 (bit 4) = Mid Tom
//   Button 6 (bit 5) = Floor Tom
//   Button 7 (bit 6) = Ride cymbal    (rightmost pad)
//
// This button order matches the physical left-to-right pad layout AND the
// authoritative pad numbering in beatmap_schema.md / controller_hid_protocol.md
// (pad N = button N, no offset -- see those docs for the 2026-08-22 update
// that replaced Hi-Hat with Mid Tom in the official 7-pad set).
//
// VID:PID = 0x1209:0x0003 (pid.codes shared VID, test-range PID reserved
// for the drum kit -- see project doc controller_hid_protocol.md).
// ============================================================================

#include "USB.h"
#include "USBHID.h"

// ---------------------------------------------------------------------------
// Custom HID gamepad descriptor: 7 buttons + 1 padding bit, report ID 1

// ---------------------------------------------------------------------------
static const uint8_t report_descriptor[] = {
  0x05, 0x01,        // Usage Page (Generic Desktop)
  0x09, 0x04,        // Usage (Joystick)
  0xA1, 0x01,        // Collection (Application)
  0x85, 0x01,        //   Report ID (1)
  0x05, 0x09,        //   Usage Page (Button)
  0x19, 0x01,        //   Usage Minimum (Button 1)
  0x29, 0x07,        //   Usage Maximum (Button 7)
  0x15, 0x00,        //   Logical Minimum (0)
  0x25, 0x01,        //   Logical Maximum (1)
  0x75, 0x01,        //   Report Size (1 bit)
  0x95, 0x07,        //   Report Count (7)
  0x81, 0x02,        //   Input (Data,Var,Abs)
  0x95, 0x01,        //   Report Count (1) -- 1 padding bit to fill the byte
  0x75, 0x01,        //   Report Size (1 bit)
  0x81, 0x03,        //   Input (Const,Var,Abs) -- padding
  0xC0                // End Collection
};

USBHID HID;

class DrumKitHIDDevice : public USBHIDDevice {
public:
  DrumKitHIDDevice(void) {
    static bool initialized = false;
    if (!initialized) {
      initialized = true;
      HID.addDevice(this, sizeof(report_descriptor));
    }
  }

  void begin(void) {
    HID.begin();
  }

  uint16_t _onGetDescriptor(uint8_t *buffer) override {
    memcpy(buffer, report_descriptor, sizeof(report_descriptor));
    return sizeof(report_descriptor);
  }

  bool send(uint8_t buttons) {
    return HID.SendReport(1, &buttons, 1);
  }
};

DrumKitHIDDevice DrumKitHid;

// ---------------------------------------------------------------------------
// Piezo pads: 7 sensors, all on ADC1 channels so nothing here depends on
// Wi-Fi being off (ADC2 is the one that conflicts with Wi-Fi; unused here,
// but ADC1 is the safer default now that there are 7 of these instead of 1).
// Each pad needs the same protection circuit validated in the original
// single-sensor test: piezo lead 1 -> GPIO, with a 1M ohm bleed resistor to
// GND at minimum (plus optional 1k series resistor + clamping diodes to
// 3.3V/GND for extra protection); piezo lead 2 -> GND. All 7 pads share the
// same 3.3V rail and GND per the wiring plan -- note the piezo itself is
// self-generating and doesn't actually consume the 3.3V rail; only the
// ESP32-S3's own logic references it.
// ---------------------------------------------------------------------------
const int NUM_PADS = 7;

struct DrumPad {
  uint8_t pin;
  const char* name;
  int baseline;
  unsigned long lastHitTime;
  bool reportBitActive;
};

// Order = physical left-to-right layout = HID button order = beatmap pad
// numbering (pad N = button N, bit N-1).
DrumPad pads[NUM_PADS] = {
  { 1, "Crash",     0, 0, false }, // button 1 / pad 1
  { 2, "Snare",     0, 0, false }, // button 2 / pad 2
  { 4, "High Tom",  0, 0, false }, // button 3 / pad 3
  { 5, "Kick",      0, 0, false }, // button 4 / pad 4
  { 6, "Mid Tom",   0, 0, false }, // button 5 / pad 5
  { 7, "Floor Tom", 0, 0, false }, // button 6 / pad 6
  { 8, "Ride",      0, 0, false }, // button 7 / pad 7
};

const int ADC_RESOLUTION_BITS = 12;
const int ADC_MAX = (1 << ADC_RESOLUTION_BITS) - 1; // 4095

// Trigger threshold, expressed in volts rather than raw counts so it's easy
// to reason about/retune. Cross-talk (a real hit on one pad's shock
// traveling through the shared mounting surface into its neighbors, so
// several pads all see a spike from a single physical hit) was reported at
// the original TAP_THRESHOLD (200 raw counts, effectively ~0.16V) -- that
// threshold worked fine for the single, isolated sensor it was validated
// on, but 7 pads sharing a rigid mount need real headroom above ambient
// vibration transfer to only fire on the pad that was actually struck.
// 1.0V helped; raised further to 1.3V per continued tuning. If cross-talk
// still shows up after this change, it points at the pads needing
// mechanical decoupling (foam/rubber isolation under each piezo disc, not
// just electrical filtering) rather than a threshold that can be raised
// further without also raising the bar for genuine (especially light)
// hits.
const float ADC_VREF_VOLTS       = 3.3;
const float TAP_THRESHOLD_VOLTS  = 1.3; // retune per pad/mounting if needed
const int   TAP_THRESHOLD = (int)((TAP_THRESHOLD_VOLTS / ADC_VREF_VOLTS) * ADC_MAX);
                                       // ~1613 raw counts at 1.3V/3.3V/12-bit

// DEBOUNCE_MS's real job is to ignore a single physical strike's own decay
// ringing (a piezo disc's output oscillates for a bit after impact, and
// without this it can re-cross TAP_THRESHOLD on the way down and register
// as a second "hit" that never happened) -- it is NOT meant to be a
// minimum-time-between-real-hits limiter. The original 150ms, inherited
// from the single-sensor test where hit speed was never exercised, was
// actually acting as that limiter: it silently swallowed any second real
// hit on the same pad faster than ~150ms apart (a bit under 7 hits/sec),
// which is well within normal fast double-stroke drumming. Piezo ringdown
// is typically much shorter than that, so this is dropped to 30ms -- long
// enough to still reject decay-ringing false triggers, short enough that
// legitimate fast double taps (down to ~33 hits/sec) get through. If
// false double-triggers from ringing show up at this value, raise it in
// small (10-20ms) steps rather than jumping back toward 150ms.
const unsigned long DEBOUNCE_MS = 30; // ignore repeated triggers on the same pad
                                       // within this window (see note above --
                                       // this is a ring-out filter, not a
                                       // hit-rate cap)
// HOLD_MS matters for double-tap responsiveness too, and was actually the
// bigger of the two gates: a pad can't register a new hit while its
// reportBitActive flag is still true from the previous one (see the
// trigger condition in loop() below), so the real minimum time between two
// registered hits on the same pad was max(DEBOUNCE_MS, HOLD_MS), not just
// DEBOUNCE_MS alone. At the old 50ms this was still tighter than the old
// 150ms DEBOUNCE_MS, but now that DEBOUNCE_MS is down to 30ms, HOLD_MS was
// left as the binding constraint. Matched to 30ms here so both gates line
// up -- still long enough for Unity's polling to reliably see each
// individual press/release edge, short enough that a fast, genuine double
// tap gets two distinct HID reports instead of being merged into one held
// press or dropped entirely.
const unsigned long HOLD_MS     = 30;

void calibratePad(DrumPad &pad) {
  long sum = 0;
  const int samples = 50;
  for (int i = 0; i < samples; i++) {
    sum += analogRead(pad.pin);
    delay(5);
  }
  pad.baseline = sum / samples;
}

uint8_t lastSentReport = 0xFF; // force first send

void setup() {
  // USB identity first in setup(), before Serial/anything else -- see
  // controller_hid_protocol.md for why (early CDC-on-boot enumeration can
  // otherwise lock in default descriptors before this runs).
  USB.VID(0x1209);
  USB.PID(0x0003);
  USB.productName("VirtualBand DrumKit");
  USB.manufacturerName("Virtual Band Project");
  USB.serialNumber("VBAND-DRUMKIT-001");
  USB.begin();

  DrumKitHid.begin();

  Serial.begin(115200);
  delay(200);

  analogReadResolution(ADC_RESOLUTION_BITS);
  for (int i = 0; i < NUM_PADS; i++) {
    analogSetPinAttenuation(pads[i].pin, ADC_11db);
  }

  Serial.println("Calibrating pad baselines (keep the kit still)...");
  for (int i = 0; i < NUM_PADS; i++) {
    calibratePad(pads[i]);
    Serial.print(pads[i].name);
    Serial.print(" (GPIO");
    Serial.print(pads[i].pin);
    Serial.print(") baseline = ");
    Serial.println(pads[i].baseline);
  }

  Serial.println();
  Serial.println("Drum Kit HID controller ready.");
  Serial.println("Pad 1 Crash=GPIO1  Pad 2 Snare=GPIO2  Pad 3 HighTom=GPIO4");
  Serial.println("Pad 4 Kick=GPIO5   Pad 5 MidTom=GPIO6  Pad 6 FloorTom=GPIO7");
  Serial.println("Pad 7 Ride=GPIO8");
  Serial.println();
}

void loop() {
  unsigned long now = millis();
  bool reportChanged = false;

  for (int i = 0; i < NUM_PADS; i++) {
    DrumPad &pad = pads[i];
    int raw = analogRead(pad.pin);
    int delta = abs(raw - pad.baseline);

    // New hit: only trigger if this pad's bit isn't already active and
    // we're past its debounce window since the last hit.
    if (!pad.reportBitActive &&
        delta > TAP_THRESHOLD &&
        (now - pad.lastHitTime) > DEBOUNCE_MS) {
      pad.lastHitTime = now;
      pad.reportBitActive = true;
      reportChanged = true;

      Serial.print(">>> HIT: ");
      Serial.print(pad.name);
      Serial.print("  delta=");
      Serial.println(delta);
    }

    // Release the button bit HOLD_MS after the hit was registered.
    if (pad.reportBitActive && (now - pad.lastHitTime) >= HOLD_MS) {
      pad.reportBitActive = false;
      reportChanged = true;
    }
  }

  if (reportChanged) {
    uint8_t report = 0;
    for (int i = 0; i < NUM_PADS; i++) {
      if (pads[i].reportBitActive) report |= (1 << i);
    }

    if (report != lastSentReport && HID.ready()) {
      DrumKitHid.send(report);
      lastSentReport = report;
    }
  }
}