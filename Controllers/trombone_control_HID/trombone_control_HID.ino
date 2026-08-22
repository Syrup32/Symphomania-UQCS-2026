// ============================================================================
// Virtual Band - Trombone Controller
// ESP32-S3 (Freenove FNK0099 board), native USB HID gamepad
//
// Board setting required (Arduino IDE > Tools) -- this board needs the
// SAME settings worked out for the trumpet, NOT the N8R2 defaults:
//   USB Mode: "Hardware CDC and JTAG"   (NOT "USB-OTG (TinyUSB)")
//   USB CDC On Boot: "Enabled"
//   Flash Size: "8MB (64Mb)"
//   PSRAM: "OPI PSRAM"
//   Partition Scheme: "8M with spiffs (3MB APP/1.5MB SPIFFS)"
// See project doc controller_hid_protocol.md for how this was tracked down.
//
// HID report: 1 byte, 8 buttons, report ID 1 (no padding needed -- 8 buttons
// fills the byte exactly, same clean shape as the violin's descriptor).
//   Button 1 (bit 0) = CHECK input -> blow/breath detected (mic hysteresis)
//   Button 2 (bit 1) = slide position 1 (most retracted)
//   Button 3 (bit 2) = slide position 2
//   Button 4 (bit 3) = slide position 3
//   Button 5 (bit 4) = slide position 4
//   Button 6 (bit 5) = slide position 5
//   Button 7 (bit 6) = slide position 6
//   Button 8 (bit 7) = slide position 7 (most extended)
//
// Only one of buttons 2-8 is ever active at a time -- the slide can only be
// in one position, unlike the trumpet's valves which can chord together.
//
// VID:PID = 0x1209:0x0005 (pid.codes shared VID, test-range PID reserved
// for the trombone -- see project doc controller_hid_protocol.md).
// ============================================================================

#include <driver/i2s_std.h>
#include "USB.h"
#include "USBHID.h"

// ---------------------------------------------------------------------------
// Custom HID gamepad descriptor: 8 buttons, no axes, report ID 1
// ---------------------------------------------------------------------------
static const uint8_t report_descriptor[] = {
  0x05, 0x01,        // Usage Page (Generic Desktop)
  0x09, 0x04,        // Usage (Joystick)
  0xA1, 0x01,        // Collection (Application)
  0x85, 0x01,        //   Report ID (1)
  0x05, 0x09,        //   Usage Page (Button)
  0x19, 0x01,        //   Usage Minimum (Button 1)
  0x29, 0x08,        //   Usage Maximum (Button 8)
  0x15, 0x00,        //   Logical Minimum (0)
  0x25, 0x01,        //   Logical Maximum (1)
  0x75, 0x01,        //   Report Size (1)
  0x95, 0x08,        //   Report Count (8)
  0x81, 0x02,        //   Input (Data,Var,Abs)
  0xC0                // End Collection
};

USBHID HID;

class TromboneHIDDevice : public USBHIDDevice {
public:
  TromboneHIDDevice(void) {
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

TromboneHIDDevice TromboneHid;

// ---------------------------------------------------------------------------
// Slide potentiometer -> discrete positions 1-7 -> buttons 2-8
// ---------------------------------------------------------------------------
const int PIN_SLIDE_POT = 16; // wiper signal, ADC2_CH5 on ESP32-S3
                               // (ADC2 can conflict with active Wi-Fi, but
                               // this sketch never calls WiFi.begin())

const int ADC_RESOLUTION_BITS = 12;
const int ADC_MAX = (1 << ADC_RESOLUTION_BITS) - 1; // 4095

const unsigned long SLIDE_DEBOUNCE_MS = 25; // same debounce style as the
                                             // switch-based controllers

int lastRawPosition = 1;
int stableSlidePosition = 1;
unsigned long lastSlideChangeTime = 0;

// Maps a raw ADC reading (0..ADC_MAX) to a discrete position 1-7, dividing
// the full range into 7 equal buckets. Position 1 = most retracted (lowest
// voltage), position 7 = most extended (highest voltage, ~3.3V).
int rawToSlidePosition(int raw) {
  int position = (raw * 7) / (ADC_MAX + 1); // 0..6
  if (position > 6) position = 6;
  if (position < 0) position = 0;
  return position + 1; // 1..7
}

// ---------------- Microphone / blow detection (same wiring as trumpet/sax) ----------------
#define I2S_SD   4   // INMP441 SD  -> GPIO4
#define I2S_SCK  2   // INMP441 SCK -> GPIO2
#define I2S_WS   3   // INMP441 WS  -> GPIO3

#define THRESH_ON   3700000   // must exceed this to START a blow
#define THRESH_OFF  1500000   // must drop below this to release
#define RELEASE_MS  40         // must stay low this long before releasing (tuned per trumpet's testing)
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

// Hysteresis + sustain/release timing, same approach as the trumpet/sax.
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

uint8_t lastSentReport = 0xFF; // force first send

void setup() {
  // USB identity must be set and USB.begin() called before anything else
  // that could delay setup(). On this board the real blocker turned out to
  // be board settings (see header comment), not this ordering -- but it's
  // kept as good practice regardless.
  USB.VID(0x1209);
  USB.PID(0x0005);
  USB.productName("VirtualBand Trombone");
  USB.manufacturerName("Virtual Band Project");
  USB.serialNumber("VBAND-TROMBONE-001");
  USB.begin();

  TromboneHid.begin();

  Serial.begin(115200);
  delay(200);

  analogReadResolution(ADC_RESOLUTION_BITS);
  analogSetAttenuation(ADC_11db); // full 0-3.3V input range
  pinMode(PIN_SLIDE_POT, INPUT);

  int initialRaw = analogRead(PIN_SLIDE_POT);
  lastRawPosition = rawToSlidePosition(initialRaw);
  stableSlidePosition = lastRawPosition;

  setupMic();

  Serial.println("Trombone HID controller ready.");
  Serial.println("Slide potentiometer = GPIO16 -> buttons 2-8 (positions 1-7)");
  Serial.println("Mic (INMP441)       = SD:4 SCK:2 WS:3 -> button 1 (check)");
  Serial.println();
}

void loop() {
  unsigned long now = millis();

  // --- Slide position, debounced same way as the switch-based controllers ---
  int rawPosition = rawToSlidePosition(analogRead(PIN_SLIDE_POT));

  if (rawPosition != lastRawPosition) {
    lastSlideChangeTime = now;
    lastRawPosition = rawPosition;
  }

  bool slideChanged = false;
  if ((now - lastSlideChangeTime) > SLIDE_DEBOUNCE_MS) {
    if (stableSlidePosition != rawPosition) {
      stableSlidePosition = rawPosition;
      slideChanged = true;
    }
  }

  if (slideChanged) {
    Serial.print("Slide position: ");
    Serial.println(stableSlidePosition);
  }

  // --- Mic / blow detection ---
  long amplitude = readMicAmplitude();
  updateBlowState(amplitude);

  // --- Build and send HID report ---
  uint8_t report = 0;
  if (blowing) report |= (1 << 0);                                   // button 1
  report |= (1 << stableSlidePosition);                              // buttons 2-8
                                                                      // (position 1 -> bit 1 = button 2, ... position 7 -> bit 7 = button 8)

  if (report != lastSentReport && HID.ready()) {
    TromboneHid.send(report);
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
    Serial.print("  slidePos=");
    Serial.print(stableSlidePosition);
    Serial.print("  report=0b");
    for (int b = 7; b >= 0; b--) Serial.print((report >> b) & 1);
    Serial.println();
  }
}