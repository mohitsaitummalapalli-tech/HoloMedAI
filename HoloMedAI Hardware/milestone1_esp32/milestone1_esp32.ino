/*
 * HoloMed AI - Milestone 1 Hardware Test Firmware
 * File: HoloMedAI Hardware/milestone1_esp32.ino
 *
 * Target: ESP32 Dev Module
 * Features:
 * - Onboard Status LED on GPIO 2
 * - Tactile / Jumper Pull-up Button Input on GPIO 19
 * - Serial Telemetry at 115200 baud (JSON event broadcasting)
 */

#include <Arduino.h>

#define LED_PIN 2
#define BUTTON_PIN 19


void setup() {
  // Initialize Serial communication at 115200 baud
  Serial.begin(115200);

  // Configure I/O pins
  pinMode(LED_PIN, OUTPUT);
  pinMode(BUTTON_PIN, INPUT_PULLUP);

  // Ensure initial LED state is OFF
  digitalWrite(LED_PIN, LOW);

  // Broadcast readiness handshake
  Serial.println("ESP32_READY");
}

void loop() {
  int buttonState = digitalRead(BUTTON_PIN);

  if (buttonState == LOW) {
    // Button pressed (GPIO 19 grounded)
    digitalWrite(LED_PIN, HIGH);
    Serial.println("{\"event\":\"BUTTON_PRESS\",\"pin\":19,\"state\":\"PRESSED\"}");
    delay(250); // Debounce delay
  } else {
    // Button released / floating (internal pull-up keeps GPIO 19 HIGH)
    digitalWrite(LED_PIN, LOW);
    delay(10);  // Small polling delay to reduce CPU spin
  }
}
