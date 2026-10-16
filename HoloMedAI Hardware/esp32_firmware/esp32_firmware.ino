/*
 * HoloMed AI - ESP32 Embedded Hardware Controller Firmware
 * File: hardware/esp32_firmware.ino
 * 
 * Hardware Setup:
 * - HC-SR04 Ultrasonic Distance Sensor: TRIG = GPIO 5, ECHO = GPIO 18
 * - Tactile Button 1 (RESET): GPIO 19 (Pull-up)
 * - Tactile Button 2 (SECTION): GPIO 21 (Pull-up)
 * - Rotary Encoder: CLK = GPIO 22, DT = GPIO 23
 * - Status LED: GPIO 2
 *
 * Serial Baud: 115200
 */

#include <Arduino.h>

#define TRIG_PIN 5

#define ECHO_PIN 18
#define BTN_RESET_PIN 19
#define BTN_SECTION_PIN 21
#define ENCODER_CLK 22
#define ENCODER_DT 23
#define STATUS_LED 2

long duration;
float distance_cm;
int lastClkState;
unsigned long lastSensorTime = 0;
const unsigned long sensorInterval = 100; // 10Hz sampling

void setup() {
  Serial.begin(115200);
  pinMode(TRIG_PIN, OUTPUT);
  pinMode(ECHO_PIN, INPUT);
  pinMode(BTN_RESET_PIN, INPUT_PULLUP);
  pinMode(BTN_SECTION_PIN, INPUT_PULLUP);
  pinMode(ENCODER_CLK, INPUT);
  pinMode(ENCODER_DT, INPUT);
  pinMode(STATUS_LED, OUTPUT);

  lastClkState = digitalRead(ENCODER_CLK);
  digitalWrite(STATUS_LED, HIGH);
  delay(200);
  digitalWrite(STATUS_LED, LOW);
  
  Serial.println("{\"status\":\"ESP32_READY\",\"module\":\"HoloMed_Hardware\"}");
}

float readUltrasonic() {
  digitalWrite(TRIG_PIN, LOW);
  delayMicroseconds(2);
  digitalWrite(TRIG_PIN, HIGH);
  delayMicroseconds(10);
  digitalWrite(TRIG_PIN, LOW);

  duration = pulseIn(ECHO_PIN, HIGH, 25000); // 25ms timeout (~4m max)
  if (duration == 0) return 999.0;
  return (duration * 0.0343) / 2.0;
}

void loop() {
  unsigned long now = millis();

  // 1. Periodic Ultrasonic Distance Reading
  if (now - lastSensorTime >= sensorInterval) {
    lastSensorTime = now;
    distance_cm = readUltrasonic();
    if (distance_cm < 200.0) { // Only report when in range
      Serial.print("{\"DISTANCE\":");
      Serial.print(distance_cm, 1);
      Serial.println("}");
    }
  }

  // 2. Button Debounce & Triggering
  if (digitalRead(BTN_RESET_PIN) == LOW) {
    Serial.println("{\"BUTTON\":\"RESET\"}");
    digitalWrite(STATUS_LED, HIGH);
    delay(250); // Simple debounce
    digitalWrite(STATUS_LED, LOW);
  }

  if (digitalRead(BTN_SECTION_PIN) == LOW) {
    Serial.println("{\"BUTTON\":\"SECTION\"}");
    digitalWrite(STATUS_LED, HIGH);
    delay(250);
    digitalWrite(STATUS_LED, LOW);
  }

  // 3. Rotary Encoder Rotation
  int currentClk = digitalRead(ENCODER_CLK);
  if (currentClk != lastClkState && currentClk == 1) {
    if (digitalRead(ENCODER_DT) != currentClk) {
      Serial.println("{\"ENCODER\":15.0}"); // Clockwise
    } else {
      Serial.println("{\"ENCODER\":-15.0}"); // Counter-Clockwise
    }
  }
  lastClkState = currentClk;
}
