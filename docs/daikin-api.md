# Daikin BRP069C4x local API (classic firmware)

Summary of the unofficial documentation of the Wi-Fi module local REST API, reverse-engineered by the
[Daikin-BRP069C4x-Local-API](https://github.com/daimik/Daikin-BRP069C4x-Local-API) project (cross-checked with
[pydaikin](https://github.com/fredrike/pydaikin)). This is what `Daikin2Mqtt.Daikin` implements.

> **Firmware warning.** Firmware **2.8.0 and newer** uses a completely different API (`/dsiot/multireq`, JSON). This
> document and daikin2mqtt cover the classic API of firmware **below 2.8.0** (tested upstream on 1_14_88, `pv=2`,
> `cpv=2.00`). Do not update the module firmware if you rely on local access. A unit on a newer firmware answers 404 on
> `common/basic_info`: daikin2mqtt logs `firmware not supported` for it.

## Transport

- HTTP `GET` on port 80, **no authentication**, LAN only.
- Answer: one line of `key=value` pairs separated by commas, starting with `ret=OK` (`ret=PARAM NG` for invalid
  parameters, `ret=ADV_NG`...). Values may be URL-encoded (`name=%44%61%69%6b%69%6e` = `Daikin`), may be empty (`adv=`)
  and `mondata` contains `=` and `,` itself.
- Discovery: UDP broadcast to port `30050` with the payload `DAIKIN_UDP/common/basic_info`; every module answers with
  its `basic_info`.
- About 4 simultaneous connections, 200-500 ms per request: daikin2mqtt sends the requests of a unit one at a time.
- **No push, no WebSocket**: polling only.

## Read endpoints used

| Endpoint | Content |
|---|---|
| `common/basic_info` | `ver` (firmware), `mac`, `name` (URL-encoded), `radio1` (Wi-Fi dBm), `en_hol` (holiday), `led`, `err`, `pow` |
| `aircon/get_control_info` | `pow`, `mode`, `stemp`, `shum`, `f_rate`, `f_dir`, `adv`, `alert`, stored per-mode values `dt1..dt7`, `dh*`, `dfr*`, `dfd*` |
| `aircon/get_sensor_info` | `htemp` (indoor), `hhum` (humidity, `-` if none), `otemp` (outdoor), `err`, `cmpfreq` (`999` = idle) |
| `aircon/get_model_info` | capabilities: `model`, `type`, `humd`, `s_humd`, `temp`, `en_frate`, `en_fdir`, `s_fdir`, `en_spmode`, `en_onofftmr`... |
| `aircon/get_week_power` | `today_runtime` (minutes), `datas` (last 7 days) |
| `aircon/get_day_power_ex` | `curr_day_heat` / `curr_day_cool`: 24 hourly values |
| `aircon/get_month_power_ex` | `curr_month_heat` / `curr_month_cool`: daily values |
| `aircon/get_year_power_ex` | yearly figures per month (`curr_year_heat` / `curr_year_cool`, or `this_year`) |

Energy values are in units of **100 Wh** (divide by 10 for kWh).

## Write endpoints used

### `aircon/set_control_info`

```
GET /aircon/set_control_info?pow={pow}&mode={mode}&stemp={stemp}&shum={shum}&f_rate={f_rate}&f_dir={f_dir}
```

**The six parameters are mandatory in every call**, even those that do not change: daikin2mqtt reads
`get_control_info`, replaces the requested field and sends all six back (atomically per unit).

| Case | Rule |
|---|---|
| Dry mode (`mode=2`) | `stemp=M` |
| Fan mode (`mode=6`) | `stemp=--` |
| Mode change | the temperature the unit stored for the new mode (`dt3` cool, `dt4` heat, `dt1`/`dt7` auto), else the current one, else 22.0 |
| Turn off | `pow=0`, the other values unchanged |

### `common/set_holiday?en_hol=0|1`

Holiday mode.

### Never used

`common/set_regioncode`, Wi-Fi settings and every other configuration endpoint are never called.

## Values

| `mode` | Meaning | | `f_rate` | Meaning | | `f_dir` | Meaning |
|---|---|---|---|---|---|---|---|
| `0` `1` `7` | auto | | `A` | auto | | `0` | off |
| `2` | dry | | `B` | silent (quiet) | | `1` | vertical |
| `3` | cool | | `3`..`7` | level 1..5 | | `2` | horizontal |
| `4` | heat | | | | | `3` | both |
| `6` | fan only | | | | | | |

`adv` (special mode): empty = none, `13` = powerful, `12` = economy, `2` = streamer (some units report several, `2/13`).
`stemp`: `10.0`-`41.0` (0.5 steps on some models), `M` = none (dry), `--` = n/a (fan). `shum`: `0` off, `AUTO` or a
percentage. `alert`: `255` = no alert.

## Not documented (hence not implemented)

The upstream documentation lists no endpoint to change the special modes (`adv`), nor the LED: the `special_mode`
entity is read-only and there is no LED switch. See "Known limitations" in the [README](../README.md).
