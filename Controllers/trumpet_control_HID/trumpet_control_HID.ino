// ============================================================================
// Virtual Band - Trumpet Controller
// ESP32-S3, native USB HID gamepad
//
// Built on top of the working "Trumpet Valve + Blow Tester" sketch --
// valve debounce and I2S blow-detection logic are unchanged, just wired
// into a USB HID report instead of only printing to Serial.
//
// Board setting required (Arduino IDE > Tools):
//   USB Mode: "USB-OTG (TinyUSB)"
//   USB CDC On Boot: "Enabled"   (keeps Serial available for debug alongside HID)
//
// HID report: 1 byte, 4 buttons used, report ID 1.
//   Button 1 (bit 0) = CHECK input -> blow/breath detected (mic hysteresis)
//   Button 2 (bit 1) = Valve 1 (forward)
//   Button 3 (bit 2) = Valve 2 (middle)
//   Button 4 (bit 3) = Valve 3 (back)
//
// VID:PID = 0x1209:0x0001 (pid.codes shared VID, test-range PID reserved
// for the trumpet -- see project doc controller_hid_protocol.md).
// ============================================================================

#include <driver/i2s_std.h>
#include "USB.h"
#include "USBHID.h"

// ---------------------------------------------------------------------------
// Custom HID gamepad descriptor: 4 buttons, no axes, report ID 1
// ---------------------------------------------------------------------------
static const uint8_t report_descriptor[] = {
  0x05, 0x01,        // Usage Page (Generic Desktop)
  0x09, 0x04,        // Usage (Joystick)
  0xA1, 0x01,        // Collection (Application)
  0x85, 0x01,        //   Report ID (1)
  0x05, 0x09,        //   Usage Page (Button)
  0x19, 0x01,        //   Usage Minimum (Button 1)
  0x29, 0x04,        //   Usage Maximum (Button 4)
  0x15, 0x00,        //   Logical Minimum (0)
  0x25, 0x01,        //   Logical Maximum (1)
  0x75, 0x01,        //   Report Size (1 bit)
  0x95, 0x04,        //   Report Count (4)
  0x81, 0x02,        //   Input (Data,Var,Abs)
  0x95, 0x04,        //   Report Count (4) -- 4 padding bits to fill the byte
  0x75, 0x01,        //   Report Size (1 bit)
  0x81, 0x03,        //   Input (Const,Var,Abs) -- padding
  0xC0                // End Collection
};

USBHID HID;

class TrumpetHIDDevice : public USBHIDDevice {
public:
  TrumpetHIDDevice(void) {
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

TrumpetHIDDevice TrumpetHid;

// ---------------- Valve switches ----------------
const uint8_t PIN_VALVE1 = 40; // forward
const uint8_t PIN_VALVE2 = 38; // middle
const uint8_t PIN_VALVE3 = 47; // back

const int PRESSED_STATE = HIGH; // flip to LOW if your logic is inverted

const unsigned long DEBOUNCE_MS = 25;

struct ValveInput {
  uint8_t pin;
  const char* name;
  int lastReading;
  int stableState;
  unsigned long lastChangeTime;
};

ValveInput valves[3] = {
  { PIN_VALVE1, "Valve 1 (forward)", HIGH, HIGH, 0 },
  { PIN_VALVE2, "Valve 2 (middle)",  HIGH, HIGH, 0 },
  { PIN_VALVE3, "Valve 3 (back)",    HIGH, HIGH, 0 },
};

// ---------------- Microphone / blow detection ----------------
#define I2S_SD   4   // INMP441 SD  -> GPIO4
#define I2S_SCK  2   // INMP441 SCK -> GPIO2
#define I2S_WS   3   // INMP441 WS  -> GPIO3

#define THRESH_ON   3700000   // must exceed this to START a blow
#define THRESH_OFF  1500000   // must drop below this to release
#define RELEASE_MS  5        // must stay low this long before releasing
#define SUSTAIN     3         // consecutive reads above THRESH_ON to start

i2s_chan_handle_t rx_chan;
int  hitCount = 0;
bool blowing = false;
unsigned long lowSince = 0;

void setupMic() {
  i2s_chan_config_t chan_cfg = I2S_CHANNEL_DEFAULT_CONFIG(I2S_NUM_0, I2S_ROLE_MASTER);
  i2s_new_channel(&chan_cfg, NULL, &rx_chan);

  i2s_std_config_t std_cfg = {
    .clk_cfg  = I2S_STD_CLK_DEFAULT_CONFIG(16000),
    .slot_cfg = I2S_STD_PHILIPS_SLOT_DEFAULT_CONFIG(
                  I2S_DATA_BIT_WIDTH_32BIT, I2S_SLOT_MODE_MONO),
    .gpio_cfg = {
      .mclk = I2S_GPIO_UNUSED,
      .bclk = (gpio_num_t)I2S_SCK,
      .ws   = (gpio_num_t)I2S_WS,
      .dout = I2S_GPIO_UNUSED,
      .din  = (gpio_num_t)I2S_SD,
      .invert_flags = { .mclk_inv = false, .bclk_inv = false, .ws_inv = false },
    },
  };
  std_cfg.slot_cfg.slot_mask = I2S_STD_SLOT_LEFT;

  i2s_channel_init_std_mode(rx_chan, &std_cfg);
  i2s_channel_enable(rx_chan);
}

// Reads one I2S block and returns the average rectified amplitude
long readMicAmplitude() {
  int32_t samples[256];
  size_t bytes_read = 0;
  long amplitude = 0;

  if (i2s_channel_read(rx_chan, samples, sizeof(samples), &bytes_read, 100) == ESP_OK) {
    int n = bytes_read / sizeof(int32_t);
    long sum = 0;
    for (int i = 0; i < n; i++) sum += abs(samples[i] >> 8);
    amplitude = (n > 0) ? sum / n : 0;
  }
  return amplitude;
}

// Hysteresis + sustain/release timing so brief noise spikes don't
// trigger false blows and brief dropouts don't cut a note short.
void updateBlowState(long amplitude) {
  unsigned long now = millis();

  if (!blowing) {
    if (amplitude > THRESH_ON) {
      hitCount = min(hitCount + 1, SUSTAIN);
      if (hitCount >= SUSTAIN) {
        blowing = true;
        lowSince = 0;
        Serial.println("Blow: STARTED");
      }
    } else {
      hitCount = 0;
    }
  } else {
    if (amplitude < THRESH_OFF) {
      if (lowSince == 0) lowSince = now;
      if (now - lowSince >= RELEASE_MS) {
        blowing = false;
        hitCount = 0;
        Serial.println("Blow: STOPPED");
      }
    } else {
      lowSince = 0; // recovered, cancel pending release
    }
  }
}

// ---------------- Shared helpers ----------------
String currentChord() {
  String chord = "";
  for (uint8_t i = 0; i < 3; i++) {
    if (valves[i].stableState == PRESSED_STATE) {
      if (chord.length() > 0) chord += "+";
      chord += String(i + 1);
    }
  }
  if (chord.length() == 0) chord = "none";
  return chord;
}

uint8_t lastSentReport = 0xFF; // force first send

void setup() {
  // USB identity must be set and USB.begin() called before anything else
  // that could delay setup() -- with "USB CDC On Boot" enabled, the host
  // can enumerate the device very early, and once enumeration completes
  // with default descriptors, changing VID/PID/strings afterward has no
  // effect until the next physical reconnect. Doing this first minimizes
  // that race.
  USB.VID(0x1209);
  USB.PID(0x0001);
  USB.productName("VirtualBand Trumpet");
  USB.manufacturerName("Virtual Band Project");
  USB.serialNumber("VBAND-TRUMPET-001"); // distinguishes this device instance
                                          // from any stale cached Windows
                                          // record under the same VID:PID
  USB.begin();

  TrumpetHid.begin();

  Serial.begin(115200);
  delay(200);
  Serial.println("[CHECKPOINT 1] Serial + USB init done");

  for (uint8_t i = 0; i < 3; i++) {
    pinMode(valves[i].pin, INPUT); // actively driven by the switch, no pull-up needed
  }
  Serial.println("[CHECKPOINT 2] Valve pinMode done");

  setupMic();
  Serial.println("[CHECKPOINT 3] setupMic() done");

  Serial.println("Trumpet HID controller ready.");
  Serial.println("Valve 1 (forward) = pin 40 -> button 2");
  Serial.println("Valve 2 (middle)  = pin 38 -> button 3");
  Serial.println("Valve 3 (back)    = pin 47 -> button 4");
  Serial.println("Mic (INMP441)     = SD:4 SCK:2 WS:3 -> button 1 (check)");
  Serial.println();
}

void loop() {
  unsigned long now = millis();

  // --- Valve debounce ---
  bool valveChanged = false;
  for (uint8_t i = 0; i < 3; i++) {
    int reading = digitalRead(valves[i].pin);

    if (reading != valves[i].lastReading) {
      valves[i].lastChangeTime = now;
    }

    if ((now - valves[i].lastChangeTime) > DEBOUNCE_MS) {
      if (valves[i].stableState != reading) {
        valves[i].stableState = reading;
        valveChanged = true;
      }
    }

    valves[i].lastReading = reading;
  }

  if (valveChanged) {
    for (uint8_t i = 0; i < 3; i++) {
      bool pressed = (valves[i].stableState == PRESSED_STATE);
      Serial.print(valves[i].name);
      Serial.print(": ");
      Serial.println(pressed ? "PRESSED" : "released");
    }
    Serial.print("Chord: ");
    Serial.println(currentChord());
    Serial.println();
  }

  // --- Mic / blow detection ---
  long amplitude = readMicAmplitude();
  updateBlowState(amplitude);

  // --- Build and send HID report ---
  uint8_t report = 0;
  if (blowing) report |= (1 << 0);                                  // button 1
  if (valves[0].stableState == PRESSED_STATE) report |= (1 << 1);   // button 2
  if (valves[1].stableState == PRESSED_STATE) report |= (1 << 2);   // button 3
  if (valves[2].stableState == PRESSED_STATE) report |= (1 << 3);   // button 4

  if (report != lastSentReport && HID.ready()) {
    TrumpetHid.send(report);
    lastSentReport = report;
  }

  // --- Throttled combined debug line (every 100 ms) ---
  static unsigned long lastPrint = 0;
  if (now - lastPrint >= 100) {
    lastPrint = now;
    Serial.print("amp=");
    Serial.print(amplitude);
    Serial.print("  blow=");
    Serial.print(blowing ? "ON " : "off");
    Serial.print("  chord=");
    Serial.print(currentChord());
    Serial.print("  report=0b");
    for (int b = 3; b >= 0; b--) Serial.print((report >> b) & 1);
    Serial.println();
  }
}