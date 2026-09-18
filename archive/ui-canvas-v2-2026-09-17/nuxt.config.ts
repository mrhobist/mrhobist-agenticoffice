export default defineNuxtConfig({
  compatibilityDate: '2026-09-17',

  // Tek kullanicili yerel arac: SSR'a gerek yok, 3B sahne zaten istemcide.
  ssr: false,

  devServer: { host: '127.0.0.1', port: 3000 },

  css: ['~/assets/css/main.css'],

  runtimeConfig: {
    public: {
      // Api yalniz loopback dinler (CLAUDE.md §3).
      apiBase: 'http://127.0.0.1:5080',
    },
  },

  typescript: { strict: true, typeCheck: false },
})
