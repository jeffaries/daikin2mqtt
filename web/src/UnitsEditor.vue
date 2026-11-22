<script setup>
import { computed, ref } from 'vue'
import { t } from './i18n'
import { slugify } from './schema'

const props = defineProps({ units: { type: Array, default: () => [] } })
const emit = defineEmits(['update:units'])

const headers = computed(() => [
  { title: t('u.colId'), key: 'Id' },
  { title: t('u.colHost'), key: 'Host' },
  { title: t('u.colName'), key: 'Name' },
  { title: t('u.colArea'), key: 'Area' },
  { title: '', key: 'actions', sortable: false, align: 'end', width: 110 },
])

// ---- add / edit ----
const form = ref(null)
const dialog = ref({ show: false, index: -1, item: {} })
const deleteIndex = ref(null)

const identifier = (v) => /^[A-Za-z0-9_-]+$/.test(v ?? '') || t('d.identifier')
const required = (v) => (v !== undefined && v !== null && String(v).trim() !== '') || t('d.required')
const host = (v) => /^[A-Za-z0-9]([A-Za-z0-9._-]*[A-Za-z0-9])?(:\d{1,5})?$/.test((v ?? '').trim()) || t('u.hostRule')
const unique = (v) =>
  !props.units.some((u, i) => i !== dialog.value.index && String(u.Id).toLowerCase() === String(v ?? '').toLowerCase()) || t('u.idUsed')

function bind(key, numeric = false) {
  return {
    modelValue: dialog.value.item[key] ?? '',
    'onUpdate:modelValue': (v) => {
      dialog.value.item[key] = numeric ? (v === '' || v === null ? undefined : Number(v)) : v
    },
  }
}

function add(prefill = {}) {
  dialog.value = { show: true, index: -1, item: { Id: '', Host: '', Name: '', Area: '', ...prefill } }
}
function edit(index) {
  dialog.value = { show: true, index, item: JSON.parse(JSON.stringify(props.units[index])) }
}
async function commit() {
  const { valid } = await form.value.validate()
  if (!valid) return
  const item = { ...dialog.value.item, Id: dialog.value.item.Id.trim(), Host: dialog.value.item.Host.trim() }
  const list = [...props.units]
  if (dialog.value.index >= 0) list[dialog.value.index] = item
  else list.push(item)
  emit('update:units', list)
  dialog.value.show = false
}
function confirmDelete() {
  emit('update:units', props.units.filter((_, i) => i !== deleteIndex.value))
  deleteIndex.value = null
}

// ---- network scan ----
const scan = ref({ show: false, loading: false, units: [], error: '', done: false })
const scanHeaders = computed(() => [
  { title: t('u.colHost'), key: 'host' },
  { title: t('u.colName'), key: 'name' },
  { title: t('u.colMac'), key: 'mac' },
  { title: t('u.colFirmware'), key: 'firmware' },
  { title: '', key: 'actions', sortable: false, align: 'end', width: 130 },
])
const isConfigured = (found) => props.units.some((u) => String(u.Host).split(':')[0] === found.host)

async function runScan() {
  scan.value = { show: true, loading: true, units: [], error: '', done: false }
  try {
    const res = await fetch('api/scan', { method: 'POST' })
    if (!res.ok) throw new Error(`HTTP ${res.status}`)
    const body = await res.json()
    scan.value.units = body.units ?? []
    scan.value.error = body.error ?? ''
  } catch (e) {
    scan.value.error = e.message
  } finally {
    scan.value.loading = false
    scan.value.done = true
  }
}

function uniqueId(base) {
  let id = base
  for (let n = 2; props.units.some((u) => String(u.Id).toLowerCase() === id); n++) id = `${base}_${n}`
  return id
}

function addFound(found) {
  const name = found.name && found.name.trim() ? found.name.trim() : ''
  scan.value.show = false
  add({ Id: uniqueId(slugify(name || 'daikin')), Host: found.host, Name: name === 'Daikin' ? '' : name })
}
</script>

<template>
  <v-card variant="outlined">
    <v-card-text>
      <v-row align="center">
        <v-col cols="12" md="">
          <div class="text-body-2">{{ t('u.intro') }}</div>
        </v-col>
        <v-col cols="12" md="auto" class="d-flex ga-2">
          <v-btn prepend-icon="mdi-radar" variant="outlined" @click="runScan">{{ t('u.scan') }}</v-btn>
          <v-btn color="primary" prepend-icon="mdi-plus" @click="add()">{{ t('u.add') }}</v-btn>
        </v-col>
      </v-row>
    </v-card-text>

    <v-data-table :headers="headers" :items="units" density="comfortable" hover :items-per-page="25">
      <template #item.Id="{ item }"><code>{{ item.Id }}</code></template>
      <template #item.actions="{ index }">
        <v-btn icon="mdi-pencil" size="small" variant="text" @click="edit(index)" />
        <v-btn icon="mdi-delete" size="small" variant="text" color="error" @click="deleteIndex = index" />
      </template>
      <template #no-data>{{ t('u.noData') }}</template>
    </v-data-table>
  </v-card>

  <v-dialog v-model="dialog.show" max-width="640" persistent>
    <v-card :title="dialog.index >= 0 ? t('u.editTitle') : t('u.addTitle')">
      <v-card-text>
        <v-form ref="form" @submit.prevent="commit">
          <v-row>
            <v-col cols="12" sm="6">
              <v-text-field v-bind="bind('Id')" :label="t('u.colId')" :hint="t('u.idHint')" persistent-hint :rules="[required, identifier, unique]" />
            </v-col>
            <v-col cols="12" sm="6">
              <v-text-field v-bind="bind('Host')" :label="t('u.colHost')" :hint="t('u.hostHint')" persistent-hint :rules="[required, host]" />
            </v-col>
            <v-col cols="12" sm="6"><v-text-field v-bind="bind('Name')" :label="t('u.colName')" :hint="t('u.nameHint')" persistent-hint /></v-col>
            <v-col cols="12" sm="6"><v-text-field v-bind="bind('Area')" :label="t('u.colArea')" :hint="t('u.areaHint')" persistent-hint /></v-col>
            <v-col cols="12" sm="6"><v-text-field v-bind="bind('MinTemperature', true)" type="number" :label="t('u.min')" placeholder="10" /></v-col>
            <v-col cols="12" sm="6"><v-text-field v-bind="bind('MaxTemperature', true)" type="number" :label="t('u.max')" placeholder="32" :hint="t('u.boundsHint')" persistent-hint /></v-col>
          </v-row>
        </v-form>
      </v-card-text>
      <v-card-actions>
        <v-spacer />
        <v-btn @click="dialog.show = false">{{ t('d.cancel') }}</v-btn>
        <v-btn color="primary" variant="flat" @click="commit">{{ t('d.ok') }}</v-btn>
      </v-card-actions>
    </v-card>
  </v-dialog>

  <v-dialog :model-value="deleteIndex !== null" max-width="420" @update:model-value="deleteIndex = null">
    <v-card :title="t('u.deleteTitle')">
      <v-card-text>{{ t('u.deleteText') }}</v-card-text>
      <v-card-actions>
        <v-spacer />
        <v-btn @click="deleteIndex = null">{{ t('app.cancel') }}</v-btn>
        <v-btn color="error" variant="flat" @click="confirmDelete">{{ t('e.delete') }}</v-btn>
      </v-card-actions>
    </v-card>
  </v-dialog>

  <v-dialog v-model="scan.show" max-width="860">
    <v-card :title="t('u.scanTitle')">
      <v-card-text>
        <v-progress-linear v-if="scan.loading" indeterminate class="mb-4" />
        <div v-if="scan.loading" class="text-body-2">{{ t('u.scanning') }}</div>
        <v-alert v-if="scan.error" type="warning" variant="tonal" class="mb-3">{{ t('u.scanError', { message: scan.error }) }}</v-alert>
        <v-data-table v-if="scan.done && scan.units.length" :headers="scanHeaders" :items="scan.units" density="comfortable" hide-default-footer :items-per-page="-1">
          <template #item.actions="{ item }">
            <v-chip v-if="isConfigured(item)" size="small" color="success" variant="tonal">{{ t('u.alreadyConfigured') }}</v-chip>
            <v-btn v-else size="small" color="primary" variant="tonal" prepend-icon="mdi-plus" @click="addFound(item)">{{ t('u.addFound') }}</v-btn>
          </template>
        </v-data-table>
        <v-alert v-else-if="scan.done && !scan.error" type="info" variant="tonal">{{ t('u.scanEmpty') }}</v-alert>
      </v-card-text>
      <v-card-actions>
        <v-btn :disabled="scan.loading" prepend-icon="mdi-refresh" @click="runScan">{{ t('u.scanAgain') }}</v-btn>
        <v-spacer />
        <v-btn @click="scan.show = false">{{ t('r.close') }}</v-btn>
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>
