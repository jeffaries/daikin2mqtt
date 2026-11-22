# daikin2mqtt

**Daikin → MQTT** gateway for **Home Assistant**, written in C# (.NET 10).

The service polls one or several **Daikin indoor units** (Wi-Fi module **BRP069C4x**, classic local API), publishes
their state to an MQTT broker and declares a **climate** entity plus sensors to Home Assistant through **MQTT device
discovery**. No HACS integration, no cloud.

It is the second adapter of the `xxx2mqtt` family (after [ipx2mqtt](https://github.com/)): everything related to MQTT /
discovery / availability lives in `Daikin2Mqtt.Core` and does not depend on Daikin.

> **Firmware**: only modules with firmware **below 2.8.0** are supported (newer ones use another API, see
> [Known limitations](#known-limitations)). Protocol notes: [docs/daikin-api.md](docs/daikin-api.md).

## Contents

- [How it works](#how-it-works)
- [Quick start (Docker)](#quick-start-docker)
- [Configuration](#configuration)
- [Declaring entities](#declaring-entities)
- [MQTT topics](#mqtt-topics)
- [Environment variables](#environment-variables)
- [Web interface](#web-interface)
- [Troubleshooting with MQTT Explorer](#troubleshooting-with-mqtt-explorer)
- [Development](#development)
- [Known limitations](#known-limitations)

## How it works

```
  Daikin unit(s)  ── HTTP GET, polling ──►  daikin2mqtt  ──►  Mosquitto  ◄──►  Home Assistant
  (BRP069C4x)     ◄── set_control_info ──   (one device per unit)
```

- **Polling only**: the module has no push channel. Control + sensors every `Polling.IntervalSeconds` (15 s), energy
  every `Polling.EnergyIntervalSeconds` (300 s). After a command the state is read back at once.
- **One Home Assistant device per unit**, published as soon as that unit has answered once, independently of the others
  (its entities depend on what `get_model_info` says it supports: nothing unsupported is published).
- **Commands are read-modify-write**: `set_control_info` needs all six parameters, so the service reads the current
  values, changes only the requested field and sends the six back, atomically per unit. Dry mode sends `stemp=M`, Fan
  mode `stemp=--`, and changing the mode restores the temperature the unit remembers for it.
- **Friendly to the module**: it serves ~4 connections at 200-500 ms each, so the requests of a unit are serialised;
  timeouts are configurable, network errors are retried with a doubling delay.
- **Availability**: `<BaseTopic>/status` is the bridge (MQTT Last Will `offline`), `<BaseTopic>/<unit>/status` the unit
  (`offline` once it has been silent for `OfflineGraceSeconds`). The entities are available only when both are `online`.
- **Only changes** are published, *retained*. On MQTT reconnection or when Home Assistant restarts
  (`homeassistant/status` = `online`) the discovery and the states are republished.
- A unit that is down at startup keeps its retained discovery; one removed from the configuration is cleaned up.

## Quick start (Docker)

1. Copy the templates and edit them (the folder is mounted on `/config`):

   ```bash
   cp config/config.example.yml config/config.yml      # MQTT broker, units (IP of each Wi-Fi module)
   cp config/entities.example.yml config/entities.yml  # optional: rename / hide / declare entities
   cp .env.example .env                                # optional: PUBLIC_PORT, MQTT_PORT
   ```

   Or skip the files and use the web interface, which creates them.
2. `docker compose up --build -d` starts daikin2mqtt **and** Mosquitto (remove the `mosquitto` service if you already
   have a broker and set `Mqtt.Host`).
3. Open `http://localhost:8080` (or `PUBLIC_PORT`), declare your units (the **Scan the network** button lists the units
   that answer), save and restart. The entities appear in Home Assistant (MQTT integration, discovery prefix
   `homeassistant`).

No hardware yet? Set `Daikin.Fake: true` (or `Daikin__Fake=true`) with no unit configured: two simulated units
(`salon`, `chambre`) appear in Home Assistant.

The container runs as a non-root user (uid 1654). On Linux the `config/` folder must be writable by it for the web
interface to save: `sudo chown -R 1654 config`.

A prebuilt image is published to the GitHub Container Registry by the workflow
([.github/workflows/docker.yml](.github/workflows/docker.yml)) on `main` and on `v*` tags.

## Configuration

Two YAML files in `/config` (found via `DAIKIN2MQTT_CONFIG_DIR`, `/config`, or `config/` in the current directory or a
parent). Every key is optional. Templates with comments: [config.example.yml](config/config.example.yml),
[entities.example.yml](config/entities.example.yml).

```yaml
Mqtt:      { Host, Port, Username, Password, UseTls, ClientId: daikin2mqtt, BaseTopic: daikin }
Discovery: { Prefix: homeassistant }
Device:    { NodeId: daikin, Manufacturer: Daikin }
Daikin:
  AutoDiscover: true            # publish every supported entity of each unit
  RequestTimeoutSeconds: 10
  RequestRetries: 2             # after a network error, delay doubled each time (RetryDelayMilliseconds: 500)
  OfflineGraceSeconds: 30
  EnableEnergy: true
  Fake: false
  ScanTimeoutSeconds: 3
  Polling: { IntervalSeconds: 15, EnergyIntervalSeconds: 300 }
  Units:                        # one unit = one Home Assistant device
    - { Id: salon, Host: 192.168.1.60, Name: Clim salon, Area: Salon }
Web:  { Enabled: true, Port: 8080, Username: admin, Password: "" }
```

- `Units[].Id`: letters, digits, `_`, `-`; used in topics and unique ids. `Host`: IP or host name, optionally `:port`.
  `Name` defaults to the name stored in the module, then the Id. Optional `MinTemperature` / `MaxTemperature` (defaults
  10 and 32: the module does not report its range).
- `Device.NodeId` prefixes the discovery node ids (`<NodeId>_<unit id>`); keep it unique per daikin2mqtt instance.
- The model, firmware and MAC address of each device are read from the module.

## Declaring entities

By default every entity a unit supports is published; `entities.yml` customises that. It holds one list, `Entities`,
with two kinds of entries.

### Published entities, per unit

| Key | Platform | Notes |
|---|---|---|
| `climate` | climate | modes `off/auto/cool/heat/dry/fan_only`, target (0.5 step), current temperature, fan `auto/quiet/1..5`, swing `off/vertical/horizontal/both` (only what the unit supports) |
| `indoor_temperature`, `outdoor_temperature`, `indoor_humidity` | sensor | humidity only if the unit has the sensor |
| `compressor_frequency` | sensor | Hz, `0` when idle (the module reports 999) |
| `compressor_running`, `alert` | binary_sensor | |
| `error_code`, `wifi_rssi`, `firmware`, `mac`, `ip` | sensor (diagnostic) | |
| `special_mode` | sensor (enum) | `none/powerful/eco/streamer`, **read-only** |
| `holiday_mode` | switch | |
| `today_runtime` | sensor (min) | with `EnableEnergy` and a unit that answers the energy endpoints |
| `energy_today`, `energy_month`, `energy_year` (+ `_heat`, `_cool`), `energy_week` | sensor (kWh) | `total_increasing`; `energy_week` is a rolling 7-day window, so it has no state class |

### Customising: `Entities` with a `Key`

`Key` is `<unit id>.<entity>`:

```yaml
Entities:
  - { Key: "salon.outdoor_temperature", Name: Outdoor, Area: Terrace }
  - { Key: "salon.firmware", Enabled: false }
```

`Name` renames (the unique id does not change, the entity is not recreated), `Enabled: false` stops publishing it.
`Area`: Home Assistant has no area per entity in MQTT discovery, only per device, so the entities of a same area form one
device named `<unit name> - <area>` attached to the unit's device. Without `Area` an entity stays in the unit's device
(whose area is `Units[].Area`).

### Manual declaration (no `Key`)

A `sensor` or `binary_sensor` reading a raw value from one of the unit's answers:

```yaml
  - UnitId: salon
    Id: cool_target            # topic: daikin/salon/cool_target/state
    Name: Stored cooling target
    Component: sensor          # sensor | binary_sensor
    Source: control            # control | sensor | basic  (get_control_info | get_sensor_info | basic_info)
    Field: dt3
    Unit: "°C"
    DeviceClass: temperature
    StateClass: measurement
    Decimals: 1
```

Other fields: `Area`, `Icon`, `Invert` (binary_sensor), `Enabled`. Missing or `-` values are not published.

## MQTT topics

`<BaseTopic>` (default `daikin`), `<u>` = unit id. States are retained.

| Topic | Direction | Payload |
|---|---|---|
| `<BaseTopic>/status` | → | `online` / `offline` (bridge, Last Will) |
| `<BaseTopic>/<u>/status` | → | `online` / `offline` (unit) |
| `<BaseTopic>/<u>/climate/mode/state` · `/set` | ↔ | `off auto cool heat dry fan_only` |
| `<BaseTopic>/<u>/climate/temperature/state` · `/set` | ↔ | `22.5` (°C) |
| `<BaseTopic>/<u>/climate/current_temperature/state` | → | `24.0` |
| `<BaseTopic>/<u>/climate/fan_mode/state` · `/set` | ↔ | `auto quiet 1 2 3 4 5` |
| `<BaseTopic>/<u>/climate/swing_mode/state` · `/set` | ↔ | `off vertical horizontal both` |
| `<BaseTopic>/<u>/<entity>/state` | → | sensors, binary sensors (`ON`/`OFF`) |
| `<BaseTopic>/<u>/holiday_mode/state` · `/set` | ↔ | `ON` / `OFF` |
| `<prefix>/device/<NodeId>_<u>/config` | → | discovery (retained) |

The state is published only once the unit confirms it (read back after the command).

## Environment variables

Any key can be overridden with `Section__Key` (nested with `__`, list items by index):
`Mqtt__Host`, `Mqtt__Password`, `Daikin__Fake=true`, `Daikin__Units__0__Id=salon`, `Daikin__Units__0__Host=192.168.1.60`,
`Web__Password`... **They win over the files**, hence over what the web interface saves. Also: `DAIKIN2MQTT_CONFIG_DIR`,
and for docker compose `PUBLIC_PORT`, `MQTT_PORT` (see [.env.example](.env.example)).

## Web interface

On `Web.Port` (default 8080), protected by HTTP Basic (user `Web.Username`, default `admin`) when `Web.Password` is set;
without a password it is open to anyone who reaches the port. Tabs:

- **MQTT**, **Daikin**, **Web interface**: the settings of `config.yml`.
- **Units**: list, add, edit; **Scan the network** sends the UDP broadcast (port 30050) and offers the units found.
- **Entities**: customised and declared entries of `entities.yml`.
- **Monitor**: live MQTT messages and Daikin events (SignalR): unit states, commands sent, availability.

Saving validates everything, keeps the previous files as `.bak` (YAML comments are not preserved), then offers a restart
(the container restart policy brings the service back). Languages: fr, en, es, it, de.

## Troubleshooting with MQTT Explorer

Connect [MQTT Explorer](https://mqtt-explorer.com) to the broker (`MQTT_PORT`, default 1883):

- `daikin/status` and `daikin/<unit>/status` must be `online`. A unit `offline` means it does not answer HTTP: check its
  IP address (`curl http://<ip>/common/basic_info` must return `ret=OK,...`) and the service logs.
- `homeassistant/device/daikin_<unit>/config` holds the discovery (retained); the Monitor tab hides it by default.
- Publish `heat` on `daikin/<unit>/climate/mode/set`: the state topic changes once the unit confirms. Nothing happens →
  see the logs (`Command to unit ... failed`) and the Monitor tab.
- Raise logging with `Logging__LogLevel__Default=Debug` (every published change, retries, rejected answers).
- **Scan finds nothing in Docker**: a UDP broadcast does not cross the Docker bridge network; use `network_mode: host`
  or enter the IP addresses.
- **Log `firmware not supported`**: the module runs firmware 2.8.0+ (see below).
- **The web interface cannot save**: the `/config` folder is not writable by uid 1654.

## Development

```bash
dotnet build
dotnet test                      # xUnit: parser, value mapping, requests, discovery, bridge, mock Daikin server...
cd web && npm ci && npm run build   # writes src/Daikin2Mqtt.Service/wwwroot (npm run dev for hot reload)
npm run check:i18n               # every language has the same keys
dotnet run --project src/Daikin2Mqtt.Service    # Daikin__Fake=true for simulated units
```

- `src/Daikin2Mqtt.Core`: MQTT, discovery, state (no Daikin dependency). Beyond the ipx2mqtt version it gained three
  generic additions: several availability topics per discovery message (`avty` + `avty_mode: all`), device `connections`
  (MAC), and `DiscoveryPublisher.KeepTopic`.
- `src/Daikin2Mqtt.Daikin`: `IDaikinClient` (one unit), `DaikinPollingClient` over `DaikinHttpApi` (classic firmware),
  `FakeDaikinClient`, `DaikinValues` (the single Daikin ↔ Home Assistant table), `Mapping/UnitEntityBuilder`.
- `src/Daikin2Mqtt.Service`: `BridgeWorker`, config files, web interface, monitor.
- `tests/Daikin2Mqtt.Tests`: includes `MockDaikinServer` (HttpListener) for the HTTP client.
- A second driver (firmware 2.8.0+) means a new `IDaikinClient` chosen in `DaikinClientFactory`.

## Known limitations

- **Firmware 2.8.0 and newer is not supported** (API `/dsiot/multireq` in JSON). Such a unit answers 404 on the classic
  endpoints: it is reported with a clear error in the log and stays unavailable. Do not update the module if you can
  avoid it.
- **Special modes (powerful, eco, streamer) are read-only**: the upstream API documentation has no endpoint to change
  them, so `special_mode` is a sensor and the climate has no preset modes. No LED switch for the same reason.
- **Target humidity is not exposed** (`humd` is read but unused); the humidity sensor is.
- **Swing**: when `s_fdir` is `1` all four swing modes are offered; a unit that only swings vertically may refuse
  `horizontal` / `both`.
- **Temperature range**: the module does not report it; defaults 10-32 °C, adjustable per unit.
- **Polling latency**: up to `Polling.IntervalSeconds` for a change made on the remote control to reach Home Assistant.
- **Energy**: the 7-day, monthly and yearly figures depend on how the module orders its lists; `energy_week` is the sum of
  `get_week_power` `datas`. Not every model answers the energy endpoints (then no energy entity is published).
- **Not validated on real hardware**: development used the upstream documentation, a mock server and simulated units.
- **Network scan** does not work through a Docker bridge network.
- No TLS / authentication on the Daikin side (the module has none): keep the units on a trusted LAN.
