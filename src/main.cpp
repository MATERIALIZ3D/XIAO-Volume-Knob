// =============================================================================
//  XIAO ESP32-C3  -  Bluetooth Volume Knob with WS2812 LED ring
// =============================================================================
//
//  Hardware
//    - Seeed Studio XIAO ESP32-C3
//    - Rotary encoder 16mm 24 P/R with push switch
//    - LED ring: 12 x WS2812 (5050 RGB)
//
//  Behaviour
//    - Rotate            -> volume up / down  (BLE HID media keys)
//    - Short tap         -> play / pause  (2 taps = next, 3 = previous)
//    - Medium hold       -> mute toggle (committed on release, ~0.6-5 s)
//    - Long hold (5 s)   -> show battery gauge on the ring (does NOT mute)
//    - LED ring          -> slow rainbow cycle while connected (no state colour:
//                           HID is one-way so play/pause can't be tracked)
//                           solid red while muted
//                           breathing blue while waiting for a BLE connection
//                           filled arc = battery charge when held (green/amber/red)
//                           red pulse = low battery (auto, < LOW_BATT_MV)
//    - Battery saver     -> after IDLE_DIM_MS with no input the ring fades to a
//                           dim glow, after IDLE_OFF_MS it blanks entirely; any
//                           turn or click wakes it instantly.
//
//  Note: HID volume / transport keys are one-way — the PC never reports the real
//  volume or play state back. That's why the ring no longer tries to show a
//  play/pause colour; it just cycles a rainbow when active.
// =============================================================================

#include <Arduino.h>
#include <BleKeyboard.h>
#include <FastLED.h>
#include <Preferences.h>
#if defined(USE_NIMBLE)
#include <NimBLEDevice.h>
#endif

// ---------------------------------------------------------------------------
//  Pin map  (XIAO silk label -> GPIO).  All chosen to avoid the C3 strapping
//  pins (GPIO2 / GPIO8 / GPIO9) so a half-turned encoder can't disturb boot.
// ---------------------------------------------------------------------------
#define PIN_ENC_A   6    // XIAO D4  - encoder phase A
#define PIN_ENC_B   7    // XIAO D5  - encoder phase B
#define PIN_ENC_SW  21   // XIAO D6  - encoder push switch (to GND)
#define PIN_LED     10   // XIAO D10 - WS2812 data in
#define PIN_VBAT    3    // XIAO D1  - battery sense (BAT+ via 100k/100k divider, ADC1)

// ---------------------------------------------------------------------------
//  LED ring config
// ---------------------------------------------------------------------------
#define NUM_LEDS        12
#define LED_BRIGHTNESS  45          // master brightness 0-255 (kept low to protect the BLE radio rail)
#define LED_MAX_MA      250         // current cap so the ring can't brown-out USB / the radio
CRGB leds[NUM_LEDS];

static const CRGB COLOR_PLAY   = CRGB(0,   220, 40);   // green
static const CRGB COLOR_PAUSE  = CRGB(255, 200, 0);    // yellow
static const CRGB COLOR_WAIT   = CRGB(0,   60,  255);  // blue (no BLE link)
static const CRGB COLOR_NEXT   = CRGB(0,   120, 255);  // cyan flash
static const CRGB COLOR_PREV   = CRGB(160, 0,   255);  // purple flash
static const CRGB COLOR_MUTE   = CRGB(255, 0,   0);    // red (muted)

// ---------------------------------------------------------------------------
//  Tuning
// ---------------------------------------------------------------------------
#define MULTICLICK_GAP_MS  350      // window to collect 2nd / 3rd click
#define LONGPRESS_MS       600      // hold this long -> mute / unmute toggle
#define DEBOUNCE_MS        25
#define FLASH_MS           140      // next/prev action flash duration
#define FLICKER_MS         130      // per-click input-acknowledge flicker length
#define RING_REST_LEVEL    125      // ring resting brightness; a click pops it to full
#define FRAME_MS           4        // ~250 FPS render (smooth motion; dithering is off)

// ---- Battery saver ---------------------------------------------------------
// With no turn/click, the ring eases to a dim glow, then blanks entirely so the
// WS2812 elements stop drawing current (the big load on battery). Any turn or
// click wakes it instantly. Once fully blanked the render loop also slows down
// and yields, letting the BLE radio modem-sleep. Set IDLE_OFF_MS very large to
// effectively disable blanking, or IDLE_DIM_MS to disable dimming too.
#define IDLE_DIM_MS     15000       // idle this long -> ring fades to a dim glow
#define IDLE_OFF_MS     45000       // idle this long -> ring blanks (LEDs off)
#define IDLE_DIM_LEVEL  36          // ring colour scale (0-255) while dimmed
#define IDLE_FRAME_MS   60          // slow render rate once the ring is fully off

// ---- Battery monitor -------------------------------------------------------
// BAT+ is read through an external 100k/100k divider on PIN_VBAT (the XIAO does
// not sense the battery on its own). analogReadMilliVolts() applies the chip's
// factory ADC calibration, so we just multiply back up by the divider ratio.
// On USB the pad sits near 4.2 V, so the low-battery warning never false-fires.
#define VBAT_DIVIDER    2.0f        // 100k/100k -> Vbat = Vadc * 2
#define VBAT_CAL        0.990f      // trimmed to meter: 4.10 V read vs 4.14 V raw
#define VBAT_SAMPLES    16          // ADC samples averaged per reading
#define VBAT_READ_MS    5000        // sample interval (battery moves slowly)
#define LOW_BATT_MV     3500        // below this -> red low-battery pulse
#define CRIT_BATT_MV    3350        // below this -> faster (more urgent) pulse

// ---- Battery gauge on demand + rainbow idle look ---------------------------
#define BATT_HOLD_MS       5000     // hold the button this long -> show battery gauge
#define BATT_LINGER_MS     4000     // keep the gauge up this long after release
#define RAINBOW_SPREAD     16       // hue step between adjacent LEDs (ring gradient)
#define RAINBOW_MS_PER_HUE 26       // ms per hue advance (higher = slower cycle)

// ---------------------------------------------------------------------------
//  BLE HID
// ---------------------------------------------------------------------------
BleKeyboard bleKeyboard("Eugene's Knob", "Seeed", 100);

// ===========================================================================
//  Rotary encoder  -  Ben Buxton full-step state-machine decoder.
//  Reads both phases on every edge; emits exactly one count per detent WITH
//  direction, and rejects contact bounce (invalid transitions return to
//  R_START without emitting). Correct choice for clean quadrature wiring.
// ===========================================================================
#define R_START     0x0
#define R_CW_FINAL  0x1
#define R_CW_BEGIN  0x2
#define R_CW_NEXT   0x3
#define R_CCW_BEGIN 0x4
#define R_CCW_FINAL 0x5
#define R_CCW_NEXT  0x6
#define DIR_CW      0x10
#define DIR_CCW     0x20

static const uint8_t ttable[7][4] = {
  {R_START,    R_CW_BEGIN,  R_CCW_BEGIN, R_START},                 // R_START
  {R_CW_NEXT,  R_START,     R_CW_FINAL,  R_START | DIR_CW},        // R_CW_FINAL
  {R_CW_NEXT,  R_CW_BEGIN,  R_START,     R_START},                 // R_CW_BEGIN
  {R_CW_NEXT,  R_CW_BEGIN,  R_CW_FINAL,  R_START},                 // R_CW_NEXT
  {R_CCW_NEXT, R_START,     R_CCW_BEGIN, R_START},                 // R_CCW_BEGIN
  {R_CCW_NEXT, R_CCW_FINAL, R_START,     R_START | DIR_CCW},       // R_CCW_FINAL
  {R_CCW_NEXT, R_CCW_FINAL, R_CCW_BEGIN, R_START},                 // R_CCW_NEXT
};

volatile uint8_t encState = R_START;
volatile int8_t  encDelta = 0;    // accumulated detents since last read

void IRAM_ATTR encoderISR() {
  uint8_t pins = (digitalRead(PIN_ENC_B) << 1) | digitalRead(PIN_ENC_A);
  encState = ttable[encState & 0x0F][pins];
  uint8_t dir = encState & 0x30;
  if      (dir == DIR_CW)  encDelta++;
  else if (dir == DIR_CCW) encDelta--;
}

// ===========================================================================
//  Application state
// ===========================================================================
bool     isPlaying   = false;
bool     isMuted     = false;
bool     wasConnected = false;

uint32_t flashUntil   = 0;
CRGB     flashColor   = CRGB::Black;
uint32_t flickerStart = 0;          // last encoder-click time (drives the flicker)
CRGB     baseColor    = CRGB::Black; // smoothly fades toward the current state color

uint32_t lastActivityMs = 0;        // last turn/click; drives the idle dim/blank
uint8_t  idleScale      = 255;      // eased 0-255 master dim applied when idle

uint16_t batteryMv   = 0;           // last battery reading, mV (0 = not yet read)
uint8_t  batteryPct  = 0;           // rough state-of-charge, %
uint32_t lastBattMs  = 0;           // last battery sample time
uint32_t batteryShowUntil = 0;      // ring shows the battery gauge until this time

// ===========================================================================
//  Runtime config  -  tunable over BLE by the companion app, saved to flash.
// ===========================================================================
enum RingMode : uint8_t { MODE_RAINBOW = 0, MODE_SOLID = 1, MODE_BREATHE = 2 };

struct __attribute__((packed)) Config {
  uint8_t  mode;          // RingMode: rainbow / solid / breathe
  uint8_t  r, g, b;       // colour for solid + breathe modes
  uint8_t  brightness;    // master brightness 0-255
  uint8_t  rainbowSpeed;  // ms per hue advance (bigger = slower cycle)
  uint8_t  rainbowSpread; // hue step between adjacent LEDs
  uint16_t idleDimS;      // seconds idle -> dim
  uint16_t idleOffS;      // seconds idle -> blank
  uint16_t lowBattMv;     // low-battery warning threshold, mV
  uint8_t  breatheSpeed;  // breathing rate (bigger = faster)
};  // 14 bytes, packed — MUST match the companion app's byte layout

static const Config DEFAULT_CFG = {
  MODE_RAINBOW, 0, 120, 255, 45, 26, 16, 15, 45, 3500, 14
};

Config      cfg = DEFAULT_CFG;
bool        cfgDirty   = false;   // config changed over BLE, needs saving
uint32_t    cfgDirtyMs = 0;       // when it last changed (debounces flash writes)
Preferences prefs;

void loadConfig() {
  prefs.begin("knob", true);
  size_t n = prefs.getBytes("cfg", &cfg, sizeof(cfg));
  prefs.end();
  if (n != sizeof(cfg)) cfg = DEFAULT_CFG;   // first boot or layout change
}

void saveConfig() {
  prefs.begin("knob", false);
  prefs.putBytes("cfg", &cfg, sizeof(cfg));
  prefs.end();
}

#if defined(USE_NIMBLE)
// ---- Custom GATT service for the companion app ----------------------------
// Status (read/notify): [u16 mV][u8 pct][u8 flags]   flags bit0 = low battery
// Config (read/write) : the packed Config struct above
#define SVC_UUID    "5da10000-9f2b-4c7e-8a3d-2b6c1e4f7a90"
#define STATUS_UUID "5da10001-9f2b-4c7e-8a3d-2b6c1e4f7a90"
#define CONFIG_UUID "5da10002-9f2b-4c7e-8a3d-2b6c1e4f7a90"

NimBLECharacteristic *statusChar = nullptr;
NimBLECharacteristic *configChar = nullptr;

void applyConfig() { FastLED.setBrightness(cfg.brightness); }

class ConfigCallbacks : public NimBLECharacteristicCallbacks {
  void onWrite(NimBLECharacteristic *c) override {
    std::string v = c->getValue();
    if (v.size() == sizeof(Config)) {
      memcpy(&cfg, v.data(), sizeof(Config));
      applyConfig();
      lastActivityMs = millis();   // wake the ring so the change is visible
      cfgDirty = true; cfgDirtyMs = millis();
      Serial.printf("Config via BLE: mode=%u rgb=%u,%u,%u bri=%u\n",
                    cfg.mode, cfg.r, cfg.g, cfg.b, cfg.brightness);
    } else {
      Serial.printf("Config write ignored: %u bytes (want %u)\n",
                    (unsigned)v.size(), (unsigned)sizeof(Config));
    }
  }
};
ConfigCallbacks configCallbacks;

// Create the service on the server BEFORE bleKeyboard.begin() starts the stack,
// so it lands in the GATT table alongside the HID service. Not advertised (a
// 128-bit UUID would overflow the adv packet) — the app finds it after connect.
void setupConfigService() {
  NimBLEServer *srv  = NimBLEDevice::createServer();     // returns existing if any
  NimBLEService *svc = srv->createService(SVC_UUID);
  statusChar = svc->createCharacteristic(STATUS_UUID,
                 NIMBLE_PROPERTY::READ | NIMBLE_PROPERTY::NOTIFY);
  configChar = svc->createCharacteristic(CONFIG_UUID,
                 NIMBLE_PROPERTY::READ | NIMBLE_PROPERTY::WRITE);
  configChar->setCallbacks(&configCallbacks);
  configChar->setValue((uint8_t *)&cfg, sizeof(cfg));
  svc->start();
}

void notifyStatus() {
  if (!statusChar) return;
  uint8_t buf[4] = {
    (uint8_t)(batteryMv & 0xFF), (uint8_t)(batteryMv >> 8), batteryPct,
    (uint8_t)((batteryMv > 2500 && batteryMv < cfg.lowBattMv) ? 0x01 : 0x00)
  };
  statusChar->setValue(buf, sizeof(buf));
  statusChar->notify();
}
#endif

// ---------------------------------------------------------------------------
//  Helpers
// ---------------------------------------------------------------------------
void triggerFlash(const CRGB &c) {
  flashColor = c;
  flashUntil = millis() + FLASH_MS;
}

// Any user input calls this; it resets the idle timer so the ring wakes.
inline void noteActivity() { lastActivityMs = millis(); }

// ---------------------------------------------------------------------------
//  Battery: read the divider, convert to cell mV and a rough %.
// ---------------------------------------------------------------------------
uint8_t vbatToPercent(uint16_t mv) {
  // Approximate LiPo discharge curve (resting). Under load the reading sags a
  // little, so the reported % is conservative — fine for a "time to charge" cue.
  static const uint16_t curveMv[]  = {4200,4100,4000,3900,3800,3700,3600,3500,3400,3300};
  static const uint8_t  curvePct[] = { 100,  87,  75,  60,  45,  30,  18,  10,   5,   0};
  const int n = sizeof(curveMv) / sizeof(curveMv[0]);
  if (mv >= curveMv[0])     return 100;
  if (mv <= curveMv[n - 1]) return 0;
  for (int i = 1; i < n; i++) {
    if (mv >= curveMv[i]) {
      return curvePct[i] + (uint32_t)(mv - curveMv[i]) *
             (curvePct[i - 1] - curvePct[i]) / (curveMv[i - 1] - curveMv[i]);
    }
  }
  return 0;
}

void readBattery() {
  uint32_t sum = 0;
  for (int i = 0; i < VBAT_SAMPLES; i++) sum += analogReadMilliVolts(PIN_VBAT);
  uint32_t adcMv = sum / VBAT_SAMPLES;
  batteryMv  = (uint16_t)(adcMv * VBAT_DIVIDER * VBAT_CAL);
  batteryPct = vbatToPercent(batteryMv);
}

// Ask the host for a fast connection interval (7.5-15 ms). The default BLE
// interval can be 30-50 ms, which makes each volume keypress crawl out and
// the volume feel laggy. Called once each time a central connects.
#if defined(USE_NIMBLE)
void requestFastConnParams() {
  NimBLEServer *srv = NimBLEDevice::getServer();
  if (srv && srv->getConnectedCount() > 0) {
    uint16_t h = srv->getPeerInfo(0).getConnHandle();
    srv->updateConnParams(h, 6, 12, 0, 200);   // min 7.5ms, max 15ms, 2s timeout
    Serial.println("Requested fast connection interval");
  }
}
#endif

// ===========================================================================
//  Encoder handling  ->  volume
// ===========================================================================
void handleEncoder() {
  noInterrupts();
  int8_t d = encDelta;
  encDelta = 0;
  interrupts();
  if (d == 0) return;

  flickerStart = millis();           // flicker the ring to acknowledge the input
  noteActivity();                    // wake the ring out of any idle dim/blank
  isMuted = false;                   // changing volume unmutes on the PC, so sync the model

  // One media-key (= one Windows 2% step) per detent.
  int steps = abs(d);
  for (int i = 0; i < steps; i++) {
    if (bleKeyboard.isConnected())
      bleKeyboard.write(d > 0 ? KEY_MEDIA_VOLUME_UP : KEY_MEDIA_VOLUME_DOWN);
  }
}

// ===========================================================================
//  Push switch  ->  multi-click  ->  transport control
// ===========================================================================
void handleButton() {
  static bool     lastStable   = HIGH;   // switch is to GND, INPUT_PULLUP
  static bool     lastRaw      = HIGH;
  static uint32_t lastChangeMs = 0;
  static uint8_t  clickCount   = 0;
  static uint32_t lastClickMs  = 0;
  static uint32_t pressStartMs = 0;
  static bool     battArmed    = false;  // this hold has reached the battery-gauge threshold

  uint32_t now = millis();
  bool raw = digitalRead(PIN_ENC_SW);

  if (raw != lastRaw) {                  // raw edge -> restart debounce timer
    lastRaw = raw;
    lastChangeMs = now;
  }

  if ((now - lastChangeMs) > DEBOUNCE_MS && raw != lastStable) {
    lastStable = raw;
    if (lastStable == LOW) {             // pressed (active low)
      noteActivity();                    // wake the ring out of any idle dim/blank
      pressStartMs = now;
      battArmed    = false;
    } else {                             // released -> decide the gesture by hold length
      uint32_t held = now - pressStartMs;
      if (battArmed) {
        // Battery-gauge gesture (>= BATT_HOLD_MS): swallow it — no click, no mute.
      } else if (held >= LONGPRESS_MS) { // medium hold -> mute toggle (on release)
        isMuted = !isMuted;
        if (bleKeyboard.isConnected()) bleKeyboard.write(KEY_MEDIA_MUTE);
        Serial.println(isMuted ? "Muted" : "Unmuted");
      } else {                           // short tap -> feeds the multi-click dispatch
        clickCount++;
        lastClickMs = now;
      }
    }
  }

  // Held past BATT_HOLD_MS -> show the battery gauge. Grab one fresh reading, then
  // keep the gauge up while held plus a short linger after release.
  if (lastStable == LOW && (now - pressStartMs) >= BATT_HOLD_MS) {
    if (!battArmed) {
      battArmed = true;
      readBattery();
      Serial.printf("Battery (hold): %u mV (%u%%)\n", batteryMv, batteryPct);
    }
    batteryShowUntil = now + BATT_LINGER_MS;
    noteActivity();                      // keep the ring awake through the hold
  }

  // Dispatch the transport action once the multi-click window closes.
  if (clickCount > 0 && lastStable == HIGH && (now - lastClickMs) > MULTICLICK_GAP_MS) {
    switch (clickCount) {
      case 1:                            // play / pause
        isPlaying = !isPlaying;
        if (bleKeyboard.isConnected()) bleKeyboard.write(KEY_MEDIA_PLAY_PAUSE);
        Serial.println(isPlaying ? "Play" : "Pause");
        break;
      case 2:                            // next track
        if (bleKeyboard.isConnected()) bleKeyboard.write(KEY_MEDIA_NEXT_TRACK);
        triggerFlash(COLOR_NEXT);
        Serial.println("Next");
        break;
      default:                           // 3+ clicks -> previous track
        if (bleKeyboard.isConnected()) bleKeyboard.write(KEY_MEDIA_PREVIOUS_TRACK);
        triggerFlash(COLOR_PREV);
        Serial.println("Previous");
        break;
    }
    clickCount = 0;
  }
}

// ===========================================================================
//  Rendering
// ===========================================================================
void renderRainbow() {
  // HID gives us no real play/pause state to show (and any guessed state desyncs
  // fast), so the default connected look is just a slow rainbow drifting around
  // the ring. Each encoder click still pops the brightness up from its resting
  // level and decays back — a quick flicker acknowledging the input.
  uint8_t spd = cfg.rainbowSpeed ? cfg.rainbowSpeed : 1;   // guard divide-by-zero
  uint8_t baseHue = (uint8_t)(millis() / spd);
  fill_rainbow(leds, NUM_LEDS, baseHue, cfg.rainbowSpread);

  uint8_t  level = RING_REST_LEVEL;
  uint32_t e = millis() - flickerStart;
  if (e < FLICKER_MS) {
    level += (uint8_t)((uint32_t)(FLICKER_MS - e) * (255 - RING_REST_LEVEL) / FLICKER_MS);
  }
  for (int i = 0; i < NUM_LEDS; i++) leds[i].nscale8(level);
}

// Solid colour (from config), with the same click flicker as the rainbow.
void renderSolid() {
  CRGB     base  = CRGB(cfg.r, cfg.g, cfg.b);
  uint8_t  level = RING_REST_LEVEL;
  uint32_t e = millis() - flickerStart;
  if (e < FLICKER_MS) {
    level += (uint8_t)((uint32_t)(FLICKER_MS - e) * (255 - RING_REST_LEVEL) / FLICKER_MS);
  }
  base.nscale8(level);
  fill_solid(leds, NUM_LEDS, base);
}

// Breathing fade of the configured colour (like the wait screen, but tunable).
void renderBreathe() {
  const uint8_t PEAK = 200, FLOOR = 25;
  float rate  = 20000.0f / (cfg.breatheSpeed ? cfg.breatheSpeed : 1);  // bigger cfg = faster
  float phase = (1.0f - cosf(millis() / rate)) * 0.5f;
  float curve = phase * phase;
  uint8_t scale = FLOOR + (uint8_t)(curve * (PEAK - FLOOR) + 0.5f);
  CRGB c = CRGB(cfg.r, cfg.g, cfg.b);
  c.nscale8(scale);
  fill_solid(leds, NUM_LEDS, c);
}

void renderBatteryGauge() {
  // Filled arc proportional to charge: green (>50%), amber (>20%), red otherwise.
  // Shown on demand when the button is held BATT_HOLD_MS — see handleButton().
  int lit = (batteryPct * NUM_LEDS + 50) / 100;   // 0..NUM_LEDS, rounded
  CRGB col = (batteryPct > 50) ? CRGB(0, 220, 40)
           : (batteryPct > 20) ? CRGB(255, 150, 0)
           :                      CRGB(255, 0, 0);
  FastLED.setBrightness(cfg.brightness);
  fill_solid(leds, NUM_LEDS, CRGB::Black);
  for (int i = 0; i < lit; i++) leds[i] = col;
}

void renderWaiting() {
  // Slow breathing blue while no BLE central is connected. We run at FULL master
  // brightness and fade by scaling the colour value across its whole 8-bit range
  // (~PEAK levels, vs ~45 if we faded the dim master brightness). A gamma curve
  // evens out the perceived change, and a small floor keeps it off the ugly
  // near-zero steps. No dithering, so nothing flickers.
  const uint8_t PEAK  = 160;   // breath peak (color scale, not master brightness)
  const uint8_t FLOOR = 30;    // dimmest point ~19% of peak
  float phase = (1.0f - cosf(millis() / 900.0f)) * 0.5f;    // 0..1, smooth
  float curve = phase * phase;                              // gamma ~2
  uint8_t scale = FLOOR + (uint8_t)(curve * (PEAK - FLOOR) + 0.5f);

  FastLED.setBrightness(255);                  // full range; power cap still applies
  CRGB c = COLOR_WAIT;
  c.nscale8(scale);
  fill_solid(leds, NUM_LEDS, c);
}

// Unmistakable red breathing pulse for low battery. Overrides the normal
// display and ignores the idle blank so the warning is always visible.
void renderLowBattery(bool crit) {
  float rate  = crit ? 450.0f : 950.0f;                  // faster when critical
  float phase = (1.0f - cosf(millis() / rate)) * 0.5f;   // 0..1 smooth
  uint8_t scale = 30 + (uint8_t)(phase * 205);
  FastLED.setBrightness(cfg.brightness);
  CRGB c = CRGB(255, 0, 0);
  c.nscale8(scale);
  fill_solid(leds, NUM_LEDS, c);
}

void render() {
  uint32_t now = millis();

  // ---- idle energy gate: full -> dim -> off, eased for a smooth fade --------
  uint32_t idle   = now - lastActivityMs;
  uint8_t  target = (idle < cfg.idleDimS * 1000UL) ? 255
                  : (idle < cfg.idleOffS * 1000UL) ? IDLE_DIM_LEVEL
                  : 0;
  int step = (target > idleScale) ? 16 : -3;   // wake fast, fade gently
  int ns   = (int)idleScale + step;
  if (step > 0 && ns > target) ns = target;
  if (step < 0 && ns < target) ns = target;
  idleScale = (uint8_t)ns;

  // On-demand battery gauge (button held) takes top priority and stays full.
  if (now < batteryShowUntil) {
    renderBatteryGauge();
    FastLED.show();
    return;
  }

  // Auto low-battery warning. The >2500 mV guard means a disconnected/unread
  // divider never trips a false warning.
  uint16_t critMv = (cfg.lowBattMv > 200) ? cfg.lowBattMv - 150 : cfg.lowBattMv;
  if (batteryMv > 2500 && batteryMv < cfg.lowBattMv) {
    renderLowBattery(batteryMv < critMv);
    FastLED.show();
    return;
  }

  // Default brightness for all connected states; renderWaiting overrides it to
  // animate the breathing fade.
  FastLED.setBrightness(cfg.brightness);

  if (!bleKeyboard.isConnected()) {
    renderWaiting();                             // breathing blue: waiting to pair
  } else if (now < flashUntil) {
    fill_solid(leds, NUM_LEDS, flashColor);      // cyan/purple flash on next/prev
  } else if (isMuted) {
    fill_solid(leds, NUM_LEDS, COLOR_MUTE);      // solid red while muted
  } else {
    switch (cfg.mode) {                          // configurable idle look
      case MODE_SOLID:   renderSolid();   break;
      case MODE_BREATHE: renderBreathe(); break;
      default:           renderRainbow(); break;
    }
  }

  // Apply the idle dim over whatever was drawn. Scaling the pixel colours (not
  // the master brightness) means it works the same for the connected state and
  // the full-brightness breathing-blue wait screen.
  if (idleScale < 255) {
    for (int i = 0; i < NUM_LEDS; i++) leds[i].nscale8(idleScale);
  }

  FastLED.show();
}

// ===========================================================================
//  Setup / loop
// ===========================================================================
void setup() {
  Serial.begin(115200);
  delay(200);
  Serial.println("\nXIAO Volume Knob starting...");

  loadConfig();   // restore saved LED colours/behaviour (or defaults on first boot)

  pinMode(PIN_ENC_A, INPUT_PULLUP);
  pinMode(PIN_ENC_B, INPUT_PULLUP);
  pinMode(PIN_ENC_SW, INPUT_PULLUP);

  // Battery sense: 11 dB attenuation covers the divided range (~2.1 V at 4.2 V).
  analogSetPinAttenuation(PIN_VBAT, ADC_11db);

  // Seed the decoder with the current pin state, then watch both phases.
  encState = ttable[R_START][(digitalRead(PIN_ENC_B) << 1) | digitalRead(PIN_ENC_A)];
  attachInterrupt(digitalPinToInterrupt(PIN_ENC_A), encoderISR, CHANGE);
  attachInterrupt(digitalPinToInterrupt(PIN_ENC_B), encoderISR, CHANGE);

  FastLED.addLeds<WS2812B, PIN_LED, GRB>(leds, NUM_LEDS);
  FastLED.setBrightness(cfg.brightness);
  FastLED.setDither(DISABLE_DITHER);    // dithering flickers under BLE timing jitter
  FastLED.setMaxPowerInVoltsAndMilliamps(5, LED_MAX_MA);

  // Quick rainbow spin on boot so you know it's alive.
  for (int s = 0; s < NUM_LEDS; s++) {
    fill_solid(leds, NUM_LEDS, CRGB::Black);
    leds[s] = CHSV(s * (255 / NUM_LEDS), 255, 255);
    FastLED.show();
    delay(40);
  }

#if defined(USE_NIMBLE)
  // Bring up NimBLE and register our custom service BEFORE the HID stack starts,
  // so both land in the GATT table together.
  NimBLEDevice::init("Eugene's Knob");

  // One-time: wipe any stale bond left over from earlier pairings so the knob
  // and the host negotiate fresh keys. A bond mismatch shows up as "connected
  // but HID/GATT all fail" (host kept its keys, we kept ours). Flagged in NVS
  // so this runs exactly once, not on every boot.
  {
    prefs.begin("knob", false);
    if (!prefs.getBool("bondwipe1", false)) {
      NimBLEDevice::deleteAllBonds();
      prefs.putBool("bondwipe1", true);
      Serial.println("Cleared stale BLE bonds (one-time) — re-pair in Windows.");
    }
    prefs.end();
  }

  setupConfigService();
#endif

  bleKeyboard.begin();
  bleKeyboard.setDelay(1);   // cut the library's per-report blocking delay (default 7ms)
#if defined(USE_NIMBLE)
  // Debug: valid (non-zero, non-0xFFFF) handles mean the custom service really
  // landed in the GATT table alongside HID.
  Serial.printf("Config service handles: status=%u config=%u\n",
                statusChar ? statusChar->getHandle() : 0,
                configChar ? configChar->getHandle() : 0);
#endif
  lastActivityMs = millis(); // start the idle timer fresh (ring stays lit after boot)
  readBattery();             // seed a first reading so the % is valid immediately
  Serial.printf("Battery at boot: %u mV (%u%%)\n", batteryMv, batteryPct);
  Serial.println("Advertising BLE as 'Eugene's Knob' - pair from your PC.");
}

void loop() {
  static uint32_t lastFrame = 0;

  handleEncoder();
  handleButton();

  bool connected = bleKeyboard.isConnected();
  if (connected != wasConnected) {
    wasConnected = connected;
    Serial.println(connected ? "BLE connected" : "BLE disconnected");
#if defined(USE_NIMBLE)
    if (connected) requestFastConnParams();
#endif
  }

  uint32_t now = millis();

  // Sample the battery on a slow cadence (it moves slowly, ADC read is cheap).
  if (now - lastBattMs >= VBAT_READ_MS) {
    lastBattMs = now;
    readBattery();
    Serial.printf("Battery: %u mV (%u%%)\n", batteryMv, batteryPct);
#if defined(USE_NIMBLE)
    notifyStatus();   // push the fresh reading to the companion app
#endif
  }

  // Persist BLE config changes, debounced so a slider drag doesn't thrash flash.
  if (cfgDirty && (now - cfgDirtyMs) > 800) {
    cfgDirty = false;
    saveConfig();
    Serial.println("Config saved to flash");
  }

  // Render fast for smooth motion; once the ring has fully blanked, slow the
  // render rate and yield so the BLE radio can modem-sleep between input checks.
  // (The encoder is interrupt-driven and the button poll stays responsive, so
  // the short delay costs no counts or clicks.)
  uint32_t frameInterval = (idleScale == 0) ? IDLE_FRAME_MS : FRAME_MS;
  if (now - lastFrame >= frameInterval) {
    lastFrame = now;
    render();
  }
  if (idleScale == 0) delay(4);
}
