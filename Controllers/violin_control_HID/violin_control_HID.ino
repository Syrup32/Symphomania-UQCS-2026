// ============================================================================
// Virtual Band - Violin Controller
// ESP32-S3, native USB HID gamepad
//
// Board setting required (Arduino IDE > Tools):
//   USB Mode: "USB-OTG (TinyUSB)"
//   USB CDC On Boot: "Enabled"   (keeps Serial available for debug alongside HID)
//
// HID report: 1 byte, 8 buttons.
//   Button 1 (bit 0) = CHECK input  -> bow/rotation detected (encoder activity)
//   Button 2 (bit 1) = note switch, neck position 1 (nearest scroll)
//   Button 3 (bit 2) = note switch, neck position 2
//   Button 4 (bit 3) = note switch, neck position 3
//   Button 5 (bit 4) = note switch, neck position 4
//   Button 6 (bit 5) = note switch, neck position 5
//   Button 7 (bit 6) = note switch, neck position 6
//   Button 8 (bit 7) = note switch, neck position 7 (at upper bout)
//
// VID:PID = 0x1209:0x0004 (pid.codes shared VID, test-range PID reserved for
// this instrument). Trumpet/Sax/Drums/Trombone should use 0x0001/0x0002/
// 0x0003/0x0005 respectively so Unity can tell controllers apart by PID.
// ============================================================================

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
  0x75, 0x01,        //   Report Size (1 bit)
  0x95, 0x08,        //   Report Count (8)
  0x81, 0x02,        //   Input (Data,Var,Abs)
  0xC0                // End Collection
};

USBHID HID;

class ViolinHIDDevice : public USBHIDDevice {
public:
  ViolinHIDDevice(void) {
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

ViolinHIDDevice ViolinHid;

// ---------------------------------------------------------------------------
// Rotary encoder (mouse-wheel style, activity only) -> Button 1 / bit 0
// ---------------------------------------------------------------------------
const int PIN_A = 4;
const int PIN_B = 6;
const unsigned long ROTATING_TIMEOUT_MS = 80;

volatile unsigned long lastEdgeMillis = 0;

void IRAM_ATTR onEncoderEdge() {
  lastEdgeMillis = millis();
}

// ---------------------------------------------------------------------------
// Note switches (SS-5GL: NC->GND, NO->3.3V, C->GPIO)
// Idle = LOW, Pressed = HIGH. Indexed by neck POSITION 1..7.
// -> Buttons 2..8 / bits 1..7
// ---------------------------------------------------------------------------
const int NUM_BUTTONS = 7;
const int BUTTON_PINS[NUM_BUTTONS] = {
  8,   // position 1 (scroll end)
  9,   // position 2
  14,  // position 3
  15,  // position 4
  12,  // position 5
  10,  // position 6
  17   // position 7 (upper bout end)
};

const unsigned long DEBOUNCE_MS = 25;

bool buttonState[NUM_BUTTONS];
bool lastRawState[NUM_BUTTONS];
unsigned long lastChangeMillis[NUM_BUTTONS];

// ---------------------------------------------------------------------------

uint8_t lastSentReport = 0xFF; // force first send

void setup() {
  // USB identity must be set and USB.begin() called before anything else
  // that could delay setup() -- with "USB CDC On Boot" enabled, the host
  // can enumerate the device very early, and once enumeration completes
  // with default descriptors, changing VID/PID/strings afterward has no
  // effect until the next physical reconnect. Doing this first minimizes
  // that race.
  USB.VID(0x1209);
  USB.PID(0x0004);
  USB.productName("VirtualBand Violin");
  USB.manufacturerName("Virtual Band Project");
  USB.begin();

  ViolinHid.begin();

  Serial.begin(115200);
  delay(200);

  // Encoder
  pinMode(PIN_A, INPUT_PULLUP);
  pinMode(PIN_B, INPUT_PULLUP);
  attachInterrupt(digitalPinToInterrupt(PIN_A), onEncoderEdge, CHANGE);
  attachInterrupt(digitalPinToInterrupt(PIN_B), onEncoderEdge, CHANGE);

  // Note switches
  for (int i = 0; i < NUM_BUTTONS; i++) {
    pinMode(BUTTON_PINS[i], INPUT);
    lastRawState[i] = digitalRead(BUTTON_PINS[i]);
    buttonState[i] = (lastRawState[i] == HIGH);
    lastChangeMillis[i] = 0;
  }

  Serial.println("Violin HID controller ready.");
}

void loop() {
  unsigned long now = millis();
  uint8_t report = 0;

  // --- Bit 0: check input (encoder rotation activity) ---
  unsigned long lastEdge;
  noInterrupts();
  lastEdge = lastEdgeMillis;
  interrupts();

  bool isRotating = (now - lastEdge) < ROTATING_TIMEOUT_MS;
  if (isRotating) {
    report |= (1 << 0);
  }

  // --- Bits 1..7: note switches, debounced ---
  for (int i = 0; i < NUM_BUTTONS; i++) {
    bool raw = digitalRead(BUTTON_PINS[i]);

    if (raw != lastRawState[i]) {
      lastChangeMillis[i] = now;
      lastRawState[i] = raw;
    }

    if ((now - lastChangeMillis[i]) > DEBOUNCE_MS) {
      buttonState[i] = (raw == HIGH);
    }

    if (buttonState[i]) {
      report |= (1 << (i + 1)); // button 2..8
    }
  }

  // Only send when the report actually changes -- avoids flooding the HID
  // endpoint, keeps behavior clean to watch in joy.cpl / Unity.
  if (report != lastSentReport && HID.ready()) {
    ViolinHid.send(report);
    lastSentReport = report;

    // Debug echo
    Serial.print("[HID] report = 0b");
    for (int b = 7; b >= 0; b--) {
      Serial.print((report >> b) & 1);
    }
    Serial.println();
  }

  delay(2);
}