---
title: Frontend Developer — Nuxt (kurum referans deseni)
---

Kurumun Nuxt panellerinin ortak deseni. Kaynak: referans Nuxt panelinin `CLAUDE.md`'si + `.claude/hooks/kurallar.py`
(2026-09-23). **Hedef projenin kökünde kendi `CLAUDE.md`'si varsa önce onu oku; çelişirse proje kazanır.**
Mevcut desene birebir uy, yeni desen icat etme; emin değilsen komşu modüle bak.

## Çalışma şekli

- Yalnız isteneni yap; yolda gördüğün başka sorunu düzeltme, çıktıda söyle.
- Backend davranışını tahmin etme: kodda kanıt ara, yoksa `blocked: true` ile sor.
- Önce sor (kendiliğinden yapma): dosya silme/yeniden adlandırma, **paket ekleme/çıkarma**,
  `nuxt.config.ts` değişikliği (tek istisna PrimeVue `include` listesi), geniş refactor, sürüm yükseltme.
- DB CLI (`psql`, `sqlite3`, `mongosh`…) ve `DELETE/UPDATE/DROP` çalıştırma. `git push` yapma.
- Hata raporunda önce hatayı üret, sonra düzelt. Varsayımını çıktıda açıkça yaz.

## Yığın

- **Nuxt 4** + Vue 3.5, her zaman `<script setup lang="ts">`. TypeScript `strict`.
- **PrimeVue 4** (Aura, TR locale) + PrimeIcons. **Auto-import KAPALI**: yalnız `nuxt.config.ts` →
  `primevue.components.include` listesindekiler çalışır; listede olmayan bileşen **hatasız boş render**
  olur. Önce listeye ekle, adı PrimeVue 4 dokümanından doğrula.
- **Zod v4**: her API yanıtı şemadan geçer. **Pinia** yalnız oturum (options API, yalnız bellek).
- **Nitro BFF** (`src/server/`): tarayıcı yalnız kendi origin'ini (`/api`) çağırır.
- Tailwind yardımcı; asıl stil `<style scoped>`. `srcDir: src/`, alias `@/` → `src/`. Paket yöneticisi npm.
- Auto-import: Vue/Nuxt API'leri, `src/components/**` (`pathPrefix: false`), composable'lar
  (`useGlobalToast`, `useGlobalLoader`, `useSession`).

## Modül anatomisi

```
pages/<Modul>/index.vue          # yalnız kompozisyon — iş mantığı YOK
pages/<Modul>/use<Modul>.ts      # tüm state + iş mantığı; index.vue destructuring ile alır
pages/<Modul>/components/        # <Modul>Table.vue, <Modul>FormDialog.vue, <Modul>HeroCard.vue
services/schemas/<modul>Schemas.ts   # önce yanıt, sonra payload şeması
services/services/<Modul>.ts         # her uç JSDoc'lu bir metod; dışa açılan <Modul>Services
```

- `<Modul>Filters.vue` **yazma**: filtre `index.vue` içinde `FilterCard` ile kurulur.
- Çok dosyalı bileşen kendi klasörüne: `index.vue` + yardımcıları. Barrel `index.ts` yok.
- Türkçe arama/sıralama: `toLocaleLowerCase('tr-TR')`.

**Composable iskeleti:** `items = ref<X[]>([])`, `isLoading`; filtre tek `reactive` + `filtered = computed`;
form `isFormVisible`, `isSubmitting`, `formMode: 'create' | 'update'`, `formState`, `resetFormState()`,
`openCreate` / `openUpdate` / `submitForm`; silme `confirmState { action }` + `runConfirm`, `cancelConfirm`,
`openDelete`. Sonda tek `return { ... }`.

**`submitForm` sırası — değiştirme:** (1) zorunlu alanları elle doğrula → eksikse
`showToast(mesaj, 'Uyari', 'warn')` + `return` · (2) `isSubmitting = true`, `try` · (3) `formMode` ile
create/update, payload kur, servisi çağır · (4) `showToast(mesaj, 'Basarili', 'success')` ·
(5) `isFormVisible = false` · (6) **`await fetchX()`** · (7) `catch {}` **boş**, `finally` → `isSubmitting = false`.
Silme ve durum değişikliği de aynı: işlem → toast → yeniden çek. Listeyi elle güncelleme (`push`/`splice`),
optimistic update yok.

Kullanıcı doğrulaması (`warn` toast) ile şema doğrulaması (`assertPayload`, geliştirici hatası için son
savunma) ayrıdır; şema mesajını arayüz metni gibi kullanma.

## Bileşenler

- **DataTable** (`components/common/DataTable`): ham `<table>` yasak. `pagination-mode`: backend paging varsa
  `server` (sayfa state'i composable'da, `@change` → fetch), yoksa `local`. Kolonlar `columns` config;
  `formatter` `unknown` gelir → `typeof` ile daralt. Satır tipi `Record<string, unknown>` ister:
  **`interface` değil `type X = {…}`** ya da `z.infer`. `:key` için `rowKeyOf(row)`.
  Slot'lar: `#toolbar`, `#cell-<key>`, `#actions`, `#title`, `#empty`, `#loading`.
- **FilterCard**: `fields: FilterField[]`, `key` composable'daki `filters` alanıyla birebir; `v-model` tek
  reactive obje. Client filtre `@clear`; server filtre `show-search` + `@search` (`pageNumber = 1` → fetch).
  Value ≠ label ise `#field-<key>` slot'u.
- **ConfirmDialog**: onay/silme; silmede `severity="danger"`, iş `confirmState.action`'da.
- **FormDialog**: create + update **tek** dialog, `mode` prop'u (`Olustur` / `Kaydet`);
  `:closable="!submitting"`, butonlar `:disabled="submitting"`, submit'te `pi pi-spin pi-spinner`;
  event'ler `update:visible`, `submit`, `cancel`.
- **Sayfa**: `definePageMeta({ title, layout: 'default' })`; kabuksuz ekranda `layout: false`. Header /
  GlobalToast / GlobalLoader'ı sayfada tekrar render etme. Ortak kabuk:
  `<style src="~/assets/styles/page.css"></style>`. Yeni global CSS açma.
- Yeni generic bileşen icat etmeden önce `components/common/`'a bak; varsa genişlet.

## Veri

- Tüm çağrılar `fetchClient` ile — `axios`, ham `fetch`, `useFetch`, `useAsyncData` yok.
  `isToast` / `isLoading` / `isSecure` varsayılan `true`, tekrar yazma (login gibi açık uçta `isSecure: false`).
  `FormData`'yı doğrudan `body` ver, `Content-Type` yazma. Hata `ApiError` (`status`, `data`).
- **`fetchClient`'a generic verme, serviste `unknown` yazma; tip şemadan türer.**
  - GET: `const raw = await fetchClient({...})` → `return XSchema.parse(resultOf(raw) ?? [])`
  - Veri dönmeyen mutation: `return actionResultSchema.parse(raw)`
  - Veri dönen mutation (ör. yeni id): o uca özel şema — `createXResultSchema.parse(resultOf(raw) ?? {})`
  - Gönderim öncesi `createXPayloadSchema.parse(payload)`; payload **Zod şemasıdır**, tip `z.infer`.
  - Sayfalı liste `pagedSchema(XItemSchema)`. Ortak taşlar `commonSchemas.ts`: `resultOf`,
    `actionResultSchema`, `pagedSchema`, `guidSchema`, `isoDateSchema` — ikinci kez tanımlama.
  - Domain enum'ları `constants/` altında, şemaya `z.enum(SABIT)` ile bağlanır.
- **Hata/bildirim:** `catch` boş — hata toast'ını `apiClient` gösterir. 401 = logout + `/login`,
  token yenileme/yeniden deneme kurgusu yazma. Loader `isLoading` ile otomatik.
- **Oturum:** token istemcide **yok** (şifreli httpOnly çerez, sunucuda). Arama, okuma, header'a ekleme.
  Tek kapı `useSession()` (`getUserId`, `getRoleId`, `isAuthenticated`, `logout`…); `useAuthStore()`'a
  doğrudan gitme. `localStorage`/`sessionStorage` yok. Yetki buton gizlemeyle değil backend 403'üyle.
- **BFF (`src/server/`):** köprüdür, iş kuralı taşıma. Kendi Nitro ucunu yalnız (1) tarayıcıda yapılamayan
  iş, (2) sır gerektiren çağrı için yaz. Dosya adı metodu taşır (`<isim>.post.ts`). Hata
  `createError({ statusCode, message })` — **`statusMessage` değil** (UI `data.detail` → `data.message` okur).
  Yeni özel başlık proxy'nin `FORWARD_REQUEST_HEADERS` listesine eklenmezse backend'e ulaşmaz. Veri
  değiştiren istek `assertSameOrigin`'den geçer. Geçici dosyayı `finally`'de sil. Sır `runtimeConfig.public`'e girmez.

## Kod kuralları

- `any` yok (`unknown` + daraltma / `z.infer`). `@ts-ignore`, `@ts-nocheck`, `console.log` bırakma.
- `import.meta.client` (`process.client` değil). `defineProps<{}>()`, `defineEmits<{}>()` tipli.
- Tarih/sayı/para `utils/formatters.ts`. `utils/validators.ts`'i yeni kodda kullanma.
- Kullanıcı girdisiyle `v-html` yok. Dış linkte `rel="noopener noreferrer"`. Upload: uzantı + MIME + boyut.
- İsimler: bileşen/servis PascalCase (`UsersTable.vue`, `Roles.ts`); şema dosyası `<modul>Schemas.ts`;
  yanıt şeması PascalCase (`RoleItemSchema`), payload camelCase (`createRolePayloadSchema`).
- UI metni Türkçe; dosyanın karakter tarzını koru (ASCII ise ASCII).
- Arayüz: kırılım **900px** (yeni kırılım uydurma), sabit `px` genişlik yok; yalnız ikonlu butonda
  `aria-label` (listede kayıt adıyla), tıklanabilir öğe `<button>`, `v-for` key kayıt kimliği; sayfa boyutu
  varsayılan 10; ağır dialog `v-if`. Renk: birincil `#0ea5b7`; hazır sınıflar `create-button`,
  `icon-action.{edit|delete|…}`, `status-badge.status-{active|draft|archived}`.
- **Yasak paketler:** `axios`, `dayjs`, `date-fns`, `moment`, `chart.js`, `apexcharts`, `vue-chartjs`, yeni UI
  kütüphanesi/ikon seti. Grafik `computed` oran + CSS. Yeni event bus / global store / provide-inject zinciri yok.
- **Burada bilinçli olarak yok:** i18n, ESLint/Prettier, vitest/birim test (istenmeden test dosyası üretme),
  route bazlı middleware (tek `auth.global.ts`), Pinia setup store.

## Doğrulama — bitti demeden önce

`npm run build` **tip denetlemez** (açık tip hatası `exit 0` geçer). İkisi de koşulur:

```bash
npm run build 2>&1 | tail -20 && npm run typecheck 2>&1 | tail -20
```

`typecheck` temiz (0 hata) başlar; kırmızıysa sebep senin değişikliğindir. Hedef projedeki hook'lar
(`.claude/hooks/kurallar.py`) **bu ofiste otomatik çalışmaz** — denetimi yazdığın dosyalarda kendin koş,
çıktı boş olmalı (yorum satırlarında yanlış alarm olabilir, onları ayıkla):

```bash
grep -nE 'console\.log\(|:\s*any\b|as any\b|<any>|any\[\]|process\.client|from\s*.(axios|dayjs|date-fns|moment|chart\.js|apexcharts|vue-chartjs)|\b(useFetch|useAsyncData)\s*[(<]|\b(localStorage|sessionStorage)\b|v-html|<table[ >]|^\s*(//|/\*+)\s*@ts-(ignore|nocheck)\b' <yazdigin-dosyalar>
```

`src/pages/**/index.vue` yazdıysan `definePageMeta` içerdiğini ayrıca kontrol et. Kullandığın PrimeVue
bileşenleri `include` listesinde mi, bak.

**Commit istenirse:** branch `<feature|bugfix|hotfix>/<task_id>-<is-ozeti>` (İngilizce, küçük harf, tire,
Türkçe karakter yok); mesaj `<onek>/<task_id> - is ozeti`, önek ve task_id branch'le birebir; `master`/`main`'e
doğrudan commit yok; `Co-Authored-By` / `Generated with` imzası **yazılmaz**.
