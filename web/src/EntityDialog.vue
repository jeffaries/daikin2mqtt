<script setup>
import { computed, ref, watch } from 'vue'
import { t } from './i18n'

const props = defineProps({
  modelValue: Boolean,
  item: Object,
  components: Array,
  sources: Array,
  unitIds: { type: Array, default: () => [] },
  entityKeys: { type: Array, default: () => [] },
})
const emit = defineEmits(['update:modelValue', 'save'])

const form = ref(null)
const draft = ref({})
const unit = ref('') // customised entity: the two halves of "<unit>.<entity>"
const entity = ref('')

watch(
  () => [props.modelValue, props.item],
  () => {
    if (!props.modelValue) return
    draft.value = { ...(props.item ?? {}) }
    const [u, ...rest] = String(draft.value.Key ?? '').split('.')
    unit.value = rest.length ? u : ''
    entity.value = rest.join('.')
  },
  { immediate: true },
)

const isCustom = computed(() => draft.value.Key !== undefined)
const component = computed(() => draft.value.Component)

const identifier = (v) => /^[A-Za-z0-9_-]+$/.test(v ?? '') || t('d.identifier')
const required = (v) => (v !== undefined && v !== null && String(v).trim() !== '') || t('d.required')

function bind(key, numeric = false) {
  return {
    modelValue: draft.value[key] ?? '',
    'onUpdate:modelValue': (v) => {
      draft.value[key] = numeric ? (v === '' || v === null ? undefined : Number(v)) : v
    },
  }
}

async function submit() {
  const { valid } = await form.value.validate()
  if (!valid) return
  if (isCustom.value) {
    draft.value.Key = `${String(unit.value).trim()}.${String(entity.value).trim()}`
  } else {
    draft.value.UnitId = String(draft.value.UnitId).trim()
    if (component.value !== 'binary_sensor') delete draft.value.Invert
    if (component.value !== 'sensor') {
      delete draft.value.Unit
      delete draft.value.StateClass
      delete draft.value.Decimals
    }
  }
  emit('save', { ...draft.value })
}
</script>

<template>
  <v-dialog :model-value="modelValue" max-width="720" scrollable persistent @update:model-value="emit('update:modelValue', $event)">
    <v-card :title="isCustom ? t('d.customTitle') : t('d.manualTitle')">
      <v-card-text>
        <v-form ref="form" @submit.prevent="submit">
          <v-row>
            <template v-if="isCustom">
              <v-col cols="12" sm="6">
                <v-combobox v-model="unit" :items="unitIds" :label="t('d.unitId')" :hint="t('d.unitIdHint')" persistent-hint :rules="[required, identifier]" />
              </v-col>
              <v-col cols="12" sm="6">
                <v-combobox v-model="entity" :items="entityKeys" :label="t('d.entity')" :hint="t('d.entityHint')" persistent-hint :rules="[required]" />
              </v-col>
              <v-col cols="12" sm="6"><v-text-field v-bind="bind('Name')" :label="t('d.name')" :hint="t('d.nameHint')" persistent-hint /></v-col>
              <v-col cols="12" sm="6"><v-text-field v-bind="bind('Area')" :label="t('d.area')" :hint="t('d.areaHint')" persistent-hint /></v-col>
              <v-col cols="12">
                <v-switch
                  :model-value="draft.Enabled !== false"
                  :label="t('d.enabled')"
                  :hint="t('d.enabledHint')"
                  persistent-hint
                  color="primary"
                  @update:model-value="draft.Enabled = $event ? undefined : false"
                />
              </v-col>
            </template>

            <template v-else>
              <v-col cols="12" sm="6">
                <v-combobox v-model="draft.UnitId" :items="unitIds" :label="t('d.unitId')" :hint="t('d.unitIdHint')" persistent-hint :rules="[required, identifier]" />
              </v-col>
              <v-col cols="12" sm="6"><v-text-field v-bind="bind('Id')" :label="t('d.id')" :hint="t('d.idHint')" persistent-hint :rules="[required, identifier]" /></v-col>
              <v-col cols="12" sm="6"><v-text-field v-bind="bind('Name')" :label="t('d.name')" :rules="[required]" /></v-col>
              <v-col cols="12" sm="6"><v-text-field v-bind="bind('Area')" :label="t('d.area')" /></v-col>
              <v-col cols="12" sm="4"><v-select v-bind="bind('Component')" :items="components" :label="t('d.component')" :rules="[required]" /></v-col>
              <v-col cols="12" sm="4"><v-select v-bind="bind('Source')" :items="sources" :label="t('d.source')" :hint="t('d.sourceHint')" persistent-hint :rules="[required]" /></v-col>
              <v-col cols="12" sm="4"><v-text-field v-bind="bind('Field')" :label="t('d.field')" :hint="t('d.fieldHint')" persistent-hint :rules="[required]" /></v-col>
              <v-col cols="12" sm="6"><v-text-field v-bind="bind('DeviceClass')" :label="t('d.deviceClass')" :hint="t('d.deviceClassHint')" persistent-hint /></v-col>
              <v-col cols="12" sm="6"><v-text-field v-bind="bind('Icon')" :label="t('d.icon')" :hint="t('d.iconHint')" persistent-hint /></v-col>
              <template v-if="component === 'sensor'">
                <v-col cols="12" sm="4"><v-text-field v-bind="bind('Unit')" :label="t('d.unit')" /></v-col>
                <v-col cols="12" sm="4"><v-text-field v-bind="bind('Decimals', true)" type="number" :label="t('d.decimals')" /></v-col>
                <v-col cols="12" sm="4"><v-text-field v-bind="bind('StateClass')" :label="t('d.stateClass')" :hint="t('d.stateClassHint')" persistent-hint /></v-col>
              </template>
              <v-col v-if="component === 'binary_sensor'" cols="12">
                <v-switch v-model="draft.Invert" :label="t('d.invert')" color="primary" hide-details />
              </v-col>
              <v-col cols="12">
                <v-switch
                  :model-value="draft.Enabled !== false"
                  :label="t('d.enabled')"
                  color="primary"
                  hide-details
                  @update:model-value="draft.Enabled = $event ? undefined : false"
                />
              </v-col>
            </template>
          </v-row>
        </v-form>
      </v-card-text>
      <v-card-actions>
        <v-spacer />
        <v-btn @click="emit('update:modelValue', false)">{{ t('d.cancel') }}</v-btn>
        <v-btn color="primary" variant="flat" @click="submit">{{ t('d.ok') }}</v-btn>
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>
