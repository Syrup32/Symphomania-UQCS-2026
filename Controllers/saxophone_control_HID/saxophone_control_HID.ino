// ============================================================================
// Virtual Band - Saxophone Controller
// ESP32-S3, native USB HID gamepad
//
// Board setting required (Arduino IDE > Tools):
//   USB Mode: "USB-OTG (TinyUSB)"
//   USB CDC On Boot: "Enabled"   (keeps Serial available for debug alongside HID)
//
// HID report: 2 bytes, 9 buttons used, report ID 1.
//   Button 1 (byte0 bit0) = CHECK input -> blow/breath detected (mic hysteresis)
//   Button 2 (byte0 bit1) = Register/octave key
//   Button 3 (byte0 bit2) = Hole 1 (top notes, index finger)
//   Button 4 (byte0 bit3) = Hole 2 (top notes, middle finger)
//   Button 5 (byte0 bit4) = Hole 3 (top notes, ring finger)
//   Button 6 (byte0 bit5) = Hole 4 (bottom notes, index finger)
//   Button 7 (byte0 bit6) = Hole 5 (bottom notes, middle finger)
//   Button 8 (byte0 bit7) = Hole 6 (bottom notes, ring finger)
//   Button 9 (byte1 bit0) = Hole 7 (bottom notes, half-moon C key)
//
// Hole numbering (1-7) is top-to-bottom, mouthpiece-to-bell, matching the
// beatmap schema's saxophone input shape:
//   {"holes": {"1".."7": bool}, "register": bool, "breath": true}
//
// VID:PID = 0x1209:0x0002 (pid.codes shared VID, test-range PID reserved
// for the saxophone -- see project doc controller_hid_protocol.md).
// ============================================================================

#include <driver/i2s_std.h>
#include "USB.h"
#include "USBHID.h"

// ---------------------------------------------------------------------------
// Custom HID gamepad descriptor: 9 buttons (2-byte report), report ID 1
// ---------------------------------------------------------------------------
static const uint8_t report_descriptor[] = {
  0x05, 0x01,        // Usage Page (Generic Desktop)
  0x09, 0x04,        // Usage (Joystick)
  0xA1, 0x01,        // Collection (Application)
  0x85, 0x01,        //   Report ID (1)
  0x05, 0x09,        //   Usage Page (Button)
  0x19, 0x01,        //   Usage Minimum (Button 1)
  0x29, 0x09,        //   Usage Maximum (Button 9)
  0x15, 0x00,        //   Logical Minimum (0)
  0x25, 0x01,        //   Logical Maximum (1)
  0x75, 0x01,        //   Report Size (1 bit)
  0x95, 0x09,        //   Report Count (9)
  0x81, 0x02,        //   Input (Data,Var,Abs)
  0x95, 0x07,        //   Report Count (7) -- padding bits to fill 2 bytes
  0x75, 0x01,        //   Report Size (1 bit)
  0x81, 0x03,        //   Input (Const,Var,Abs) -- padding
  0xC0                // End Collection
};

USBHID HID;

class SaxophoneHIDDevice : public USBHIDDevice {
public:
  SaxophoneHIDDevice(void) {
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

  bool send(uint8_t byte0, uint8_t byte1) {
    uint8_t report[2] = { byte0, byte1 };
    return HID.SendReport(1, report, 2);
  }
};

SaxophoneHIDDevice SaxHid;

// ---------------------------------------------------------------------------
// Switches (SS-5GL style: C -> GPIO, NC -> GND, NO -> 3.3V, plain INPUT,
// idle = LOW, pressed = HIGH). Register/octave key + 7 tone holes.
// ---------------------------------------------------------------------------
const int PRESSED_STATE = HIGH;
const unsigned long DEBOUNCE_MS = 25;

struct SwitchInput {
  uint8_t pin;
  const char* name;
  int lastReading;
  int stableState;
  unsigned long lastChangeTime;
};

// Index 0 = register key, indices 1-7 = holes 1-7 (top-to-bottom)
SwitchInput switches[8] = {
  { 10, "Register/octave key",              HIGH, HIGH, 0 },
  { 15, "Hole 1 (top, index finger)",       HIGH, HIGH, 0 },
  { 16, "Hole 2 (top, middle finger)",      HIGH, HIGH, 0 },
  { 9,  "Hole 3 (top, ring finger)",        HIGH, HIGH, 0 },
  { 6,  "Hole 4 (bottom, index finger)",    HIGH, HIGH, 0 },
  { 13, "Hole 5 (bottom, middle finger)",   HIGH, HIGH, 0 },
  { 18, "Hole 6 (bottom, ring finger)",     HIGH, HIGH, 0 },
  { 12, "Hole 7 (bottom, half-moon C key)", HIGH, HIGH, 0 },
};

// ---------------- Microphone / blow detection (same wiring as trumpet) ----------------
#define I2S_SD   4   // INMP441 SD  -> GPIO4
#define I2S_SCK  2   // INMP441 SCK -> GPIO2
#define I2S_WS   3   // INMP441 WS  -> GPIO3

#define THRESH_ON   3700000   // must exceed this to START a blow
#define THRESH_OFF  1500000   // must drop below this to release
#define RELEASE_MS  40         // must stay low this long before releasing (tuned down, see trumpet notes)
#define SUSTAIN     3          // consecutive reads above THRESH_ON to start

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

// Hysteresis + sustain/release timing, same approach as the trumpet.
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
String currentFingering() {
  String s = "";
  for (uint8_t i = 0; i < 8; i++) {
    if (switches[i].stableState == PRESSED_STATE) {
      if (s.length() > 0) s += "+";
      s += (i == 0) ? "REG" : String(i); // i=1..7 -> hole number
    }
  }
  if (s.length() == 0) s = "none";
  return s;
}

uint8_t lastSentByte0 = 0xFF, lastSentByte1 = 0xFF; // force first send

void setup() {
  // USB identity must be set and USB.begin() called before anything else
  // that could delay setup() -- with "USB CDC On Boot" enabled, the host
  // can enumerate the device very early, and once enumeration completes
  // with default descriptors, changing VID/PID/strings afterward has no
  // effect until the next physical reconnect. Doing this first minimizes
  // that race.
  USB.VID(0x1209);
  USB.PID(0x0002);
  USB.productName("VirtualBand Saxophone");
  USB.manufacturerName("Virtual Band Project");
  USB.begin();

  SaxHid.begin();

  Serial.begin(115200);
  delay(200);

  for (uint8_t i = 0; i < 8; i++) {
    pinMode(switches[i].pin, INPUT); // actively driven by the switch, no pull-up needed
  }

  setupMic();

  Serial.println("Saxophone HID controller ready.");
  Serial.println("Button 1 = breath check, Button 2 = register key, Buttons 3-9 = holes 1-7");
  Serial.println();
}

void loop() {
  unsigned long now = millis();

  // --- Switch debounce (register key + 7 holes) ---
  bool switchChanged = false;
  for (uint8_t i = 0; i < 8; i++) {
    int reading = digitalRead(switches[i].pin);

    if (reading != switches[i].lastReading) {
      switches[i].lastChangeTime = now;
    }

    if ((now - switches[i].lastChangeTime) > DEBOUNCE_MS) {
      if (switches[i].stableState != reading) {
        switches[i].stableState = reading;
        switchChanged = true;
      }
    }

    switches[i].lastReading = reading;
  }

  if (switchChanged) {
    Serial.print("Fingering: ");
    Serial.println(currentFingering());
  }

  // --- Mic / blow detection ---
  long amplitude = readMicAmplitude();
  updateBlowState(amplitude);

  // --- Build and send HID report (2 bytes) ---
  uint8_t byte0 = 0;
  uint8_t byte1 = 0;

  if (blowing) byte0 |= (1 << 0);                                        // button 1: breath
  if (switches[0].stableState == PRESSED_STATE) byte0 |= (1 << 1);       // button 2: register
  if (switches[1].stableState == PRESSED_STATE) byte0 |= (1 << 2);       // button 3: hole 1
  if (switches[2].stableState == PRESSED_STATE) byte0 |= (1 << 3);       // button 4: hole 2
  if (switches[3].stableState == PRESSED_STATE) byte0 |= (1 << 4);       // button 5: hole 3
  if (switches[4].stableState == PRESSED_STATE) byte0 |= (1 << 5);       // button 6: hole 4
  if (switches[5].stableState == PRESSED_STATE) byte0 |= (1 << 6);       // button 7: hole 5
  if (switches[6].stableState == PRESSED_STATE) byte0 |= (1 << 7);       // button 8: hole 6
  if (switches[7].stableState == PRESSED_STATE) byte1 |= (1 << 0);       // button 9: hole 7

  if ((byte0 != lastSentByte0 || byte1 != lastSentByte1) && HID.ready()) {
    SaxHid.send(byte0, byte1);
    lastSentByte0 = byte0;
    lastSentByte1 = byte1;
  }

  // --- Throttled combined debug line (every 100 ms) ---
  static unsigned long lastPrint = 0;
  if (now - lastPrint >= 100) {
    lastPrint = now;
    Serial.print("amp=");
    Serial.print(amplitude);
    Serial.print("  blow=");
    Serial.print(blowing ? "ON " : "off");
    Serial.print("  fingering=");
    Serial.print(currentFingering());
    Serial.print("  report=0b");
    for (int b = 0; b < 1; b++) Serial.print((byte1 >> b) & 1);
    Serial.print(" ");
    for (int b = 7; b >= 0; b--) Serial.print((byte0 >> b) & 1);
    Serial.println();
  }
}