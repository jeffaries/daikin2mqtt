// Description of the config.yml settings shown in the interface. `path` is the key path in the YAML tree.
// Texts live in locales/*.js: section 's.<id>', group 'g.<id>' (+ 'n.<id>' note), field 'f.<path>' (+ 'h.<path>' hint).
// The list of units (Daikin.Units) and the entities have their own tabs.
export const sections = [
  {
    id: 'mqtt', icon: 'mdi-server-network',
    groups: [
      { id: 'mqtt', fields: [
        { path: 'Mqtt.Host', type: 'text', default: 'localhost' },
        { path: 'Mqtt.Port', type: 'number', default: 1883 },
        { path: 'Mqtt.Username', type: 'text' },
        { path: 'Mqtt.Password', type: 'password' },
        { path: 'Mqtt.UseTls', type: 'bool', default: false },
        { path: 'Mqtt.ClientId', type: 'text', default: 'daikin2mqtt' },
        { path: 'Mqtt.BaseTopic', type: 'text', default: 'daikin' },
      ] },
      { id: 'ha', fields: [
        { path: 'Discovery.Prefix', type: 'text', default: 'homeassistant' },
        { path: 'Device.NodeId', type: 'text', default: 'daikin' },
        { path: 'Device.Manufacturer', type: 'text', default: 'Daikin' },
      ] },
    ],
  },
  { id: 'units', icon: 'mdi-air-conditioner', special: 'units', groups: [] },
  {
    id: 'daikin', icon: 'mdi-thermostat',
    groups: [
      { id: 'general', fields: [
        { path: 'Daikin.AutoDiscover', type: 'bool', default: true },
        { path: 'Daikin.EnableEnergy', type: 'bool', default: true },
        { path: 'Daikin.Fake', type: 'bool', default: false },
      ] },
      { id: 'delays', fields: [
        { path: 'Daikin.RequestTimeoutSeconds', type: 'number', default: 10 },
        { path: 'Daikin.RequestRetries', type: 'number', default: 2 },
        { path: 'Daikin.RetryDelayMilliseconds', type: 'number', default: 500 },
        { path: 'Daikin.OfflineGraceSeconds', type: 'number', default: 30 },
        { path: 'Daikin.ScanTimeoutSeconds', type: 'number', default: 3 },
      ] },
      { id: 'polling', fields: [
        { path: 'Daikin.Polling.IntervalSeconds', type: 'number', default: 15 },
        { path: 'Daikin.Polling.EnergyIntervalSeconds', type: 'number', default: 300 },
      ] },
    ],
  },
  {
    id: 'services', icon: 'mdi-web',
    groups: [
      { id: 'web', fields: [
        { path: 'Web.Enabled', type: 'bool', default: true },
        { path: 'Web.Port', type: 'number', default: 8080 },
        { path: 'Web.Username', type: 'text', default: 'admin' },
        { path: 'Web.Password', type: 'password' },
      ] },
    ],
  },
]

// Platforms of a hand-declared entity, and where it reads its value.
export const componentTypes = ['sensor', 'binary_sensor']
export const sourceTypes = ['control', 'sensor', 'basic']

// Same list as the server's UnitEntityBuilder.KnownKeys (the server sends it too, this is the fallback).
export const defaultEntityKeys = [
  'climate', 'indoor_temperature', 'outdoor_temperature', 'indoor_humidity', 'compressor_frequency', 'error_code',
  'wifi_rssi', 'special_mode', 'today_runtime', 'energy_today', 'energy_today_heat', 'energy_today_cool', 'energy_week',
  'energy_month', 'energy_month_heat', 'energy_month_cool', 'energy_year', 'energy_year_heat', 'energy_year_cool',
  'alert', 'compressor_running', 'holiday_mode', 'power', 'streamer', 'firmware', 'mac', 'ip',
]

export function getPath(obj, path) {
  return path.split('.').reduce((o, k) => (o == null ? undefined : o[k]), obj)
}

export function setPath(obj, path, value) {
  const keys = path.split('.')
  const last = keys.pop()
  const parent = keys.reduce((o, k) => (o[k] = o[k] && typeof o[k] === 'object' ? o[k] : {}), obj)
  parent[last] = value
}

// Removes empty values so that the written YAML only holds what was filled in.
export function prune(value) {
  if (Array.isArray(value)) return value.map(prune)
  if (value && typeof value === 'object') {
    const out = {}
    for (const [k, v] of Object.entries(value)) {
      const p = prune(v)
      const empty = p === '' || p === null || p === undefined || (typeof p === 'object' && !Array.isArray(p) && Object.keys(p).length === 0)
      if (!empty) out[k] = p
    }
    return out
  }
  return value
}

// "Clim salon" -> "clim_salon": a suggested unit Id (letters, digits, '_' and '-').
export function slugify(text) {
  const slug = String(text ?? '')
    .normalize('NFD')
    .replace(/[̀-ͯ]/g, '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '_')
    .replace(/^_+|_+$/g, '')
  return slug || 'unit'
}
