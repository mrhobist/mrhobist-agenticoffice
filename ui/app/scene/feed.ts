import type { FeedStatus, SceneEvent } from './contract'
import { EVENT_TYPES } from './contract'
import { tokenQuery } from '~/composables/useAuth'

/**
 * Olay kaynagi. Once Api SSE denenir; baglanti kurulamazsa ayni sekildeki olaylari
 * ureten bir sahte yonetmen (mock) devreye girer ki sahne bos durmasin. SSE gelince
 * mock durur. Iki kaynak da AYNI `onEvent`'e yazar; sahne farki bilmez.
 */
export function connectFeed(
  apiBase: string,
  onEvent: (e: SceneEvent) => void,
  onStatus: (s: FeedStatus) => void,
): { stop: () => void } {
  let es: EventSource | null = null
  let mock: { stop: () => void } | null = null
  let live = false
  let stopped = false

  const startMock = () => {
    if (mock || live || stopped) return
    onStatus('mock')
    mock = runMockDirector(onEvent)
  }
  const stopMock = () => { mock?.stop(); mock = null }

  const open = () => {
    if (stopped) return
    onStatus(live ? 'reconnecting' : 'connecting')
    es = new EventSource(`${apiBase}/api/v1/scene/events${tokenQuery()}`)
    es.onopen = () => { live = true; stopMock(); onStatus('live') }
    es.onerror = () => {
      // EventSource kendi yeniden baglanir; biz bu arada sahneyi mock ile besleriz.
      live = false
      onStatus('reconnecting')
      startMock()
    }
    for (const type of EVENT_TYPES) {
      es.addEventListener(type, ev => {
        try {
          const data = JSON.parse((ev as MessageEvent<string>).data) as never
          onEvent({ type, data } as SceneEvent)
        } catch (err) {
          console.warn('scene event parse', type, err)
        }
      })
    }
  }

  open()
  // Ilk 2.5 s icinde baglanti yoksa mock baslar.
  const timer = setTimeout(() => { if (!live) startMock() }, 2500)

  return {
    stop() {
      stopped = true
      clearTimeout(timer)
      es?.close()
      stopMock()
    },
  }
}

// --------------------------------------------------------------------------- //

interface Beat { at: number; events: SceneEvent[] }

/** Gercek bir calismanin ritmini taklit eden senaryo; sonunda basa doner. */
const SCRIPT: Beat[] = [
  { at: 0, events: [
    { type: 'board.set', data: { tasks: [
      { id: 'T1', title: 'slugify fonksiyonu', stage: 'analiz', state: 'active' },
      { id: 'T2', title: 'Türkçe karakter tablosu', stage: 'analiz', state: 'queued' },
      { id: 'T3', title: 'Pytest testleri', stage: 'analiz', state: 'queued' },
      { id: 'T4', title: 'Modül dokümantasyonu', stage: 'analiz', state: 'queued' },
    ] } },
    { type: 'run.stage', data: { stage: 'Analiz', task: 'T1', round: 1 } },
    { type: 'door', data: { state: 'open' } },
    { type: 'agent.state', data: { agent: 'analyst', state: 'working', note: 'Brief çözümleniyor' } },
  ] },
  { at: 4000, events: [
    { type: 'agent.state', data: { agent: 'analyst', state: 'thinking' } },
    { type: 'agent.say', data: { agent: 'analyst', kind: 'talk', ms: 3000 } },
  ] },
  { at: 8000, events: [
    { type: 'agent.state', data: { agent: 'analyst', state: 'done', note: '4 görev çıktı' } },
    { type: 'meet', data: { from: 'analyst', to: 'designer', kind: 'handoff' } },
    { type: 'board.move', data: { task: 'T1', stage: 'tasarim', state: 'active' } },
    { type: 'run.stage', data: { stage: 'Tasarım', task: 'T1', round: 1 } },
  ] },
  { at: 16000, events: [
    { type: 'agent.state', data: { agent: 'designer', state: 'working', note: 'Akış ve ekranlar' } },
    { type: 'agent.state', data: { agent: 'analyst', state: 'idle' } },
  ] },
  { at: 22000, events: [
    { type: 'agent.state', data: { agent: 'designer', state: 'done' } },
    { type: 'meet', data: { from: 'organizer', to: 'designer', kind: 'handoff' } },
    { type: 'board.move', data: { task: 'T1', stage: 'devir-1', state: 'active' } },
    { type: 'run.stage', data: { stage: 'Devir', task: 'T1', round: 1 } },
  ] },
  { at: 27000, events: [
    { type: 'agent.leave', data: { agent: 'manager' } },
    { type: 'cafe.special', data: { text: 'Sprint Latte ☕' } },
  ] },
  { at: 30000, events: [
    { type: 'meet', data: { from: 'organizer', to: 'developer', kind: 'handoff' } },
    { type: 'board.move', data: { task: 'T1', stage: 'gelistirme', state: 'active' } },
    { type: 'board.move', data: { task: 'T2', stage: 'tasarim', state: 'active' } },
    { type: 'run.stage', data: { stage: 'Geliştirme', task: 'T1', round: 1 } },
  ] },
  { at: 38000, events: [
    { type: 'agent.state', data: { agent: 'developer', state: 'working', note: 'slugify fonksiyonu' } },
    { type: 'agent.state', data: { agent: 'designer', state: 'working', note: 'T2 tasarım' } },
    { type: 'cat', data: { action: 'wander' } },
  ] },
  { at: 46000, events: [
    { type: 'agent.state', data: { agent: 'developer', state: 'waiting', note: 'Manager bekleniyor' } },
    { type: 'meet', data: { from: 'developer', to: 'manager', kind: 'ask', ms: 5000 } },
    { type: 'board.move', data: { task: 'T1', stage: 'gelistirme', state: 'blocked' } },
  ] },
  { at: 55000, events: [
    { type: 'agent.state', data: { agent: 'developer', state: 'working', note: 'tur 1/3' } },
    { type: 'board.move', data: { task: 'T1', stage: 'gelistirme', state: 'active' } },
  ] },
  { at: 60000, events: [
    { type: 'agent.enter', data: { agent: 'manager' } },
    { type: 'cafe.special', data: { text: null } },
  ] },
  { at: 63000, events: [
    { type: 'agent.state', data: { agent: 'developer', state: 'done' } },
    { type: 'agent.state', data: { agent: 'tester', state: 'working', note: 'Kurallar denetleniyor' } },
    { type: 'board.move', data: { task: 'T1', stage: 'test', state: 'active' } },
    { type: 'run.stage', data: { stage: 'Test', task: 'T1', round: 1 } },
  ] },
  { at: 71000, events: [
    { type: 'agent.state', data: { agent: 'tester', state: 'blocked', note: '2 ihlal' } },
    { type: 'meet', data: { from: 'tester', to: 'developer', kind: 'reject' } },
    { type: 'board.move', data: { task: 'T1', stage: 'gelistirme', state: 'blocked' } },
    { type: 'run.stage', data: { stage: 'Geliştirme', task: 'T1', round: 2 } },
  ] },
  { at: 80000, events: [
    { type: 'agent.state', data: { agent: 'tester', state: 'idle' } },
    { type: 'agent.state', data: { agent: 'developer', state: 'working', note: 'tur 2/3 — docstring' } },
    { type: 'board.move', data: { task: 'T1', stage: 'gelistirme', state: 'active' } },
    { type: 'board.move', data: { task: 'T2', stage: 'gelistirme', state: 'queued' } },
  ] },
  { at: 88000, events: [
    { type: 'agent.state', data: { agent: 'developer', state: 'done' } },
    { type: 'agent.state', data: { agent: 'tester', state: 'working', note: 'Onay kapısı' } },
    { type: 'board.move', data: { task: 'T1', stage: 'onay', state: 'active' } },
    { type: 'run.stage', data: { stage: 'Onay', task: 'T1', round: 2 } },
  ] },
  { at: 95000, events: [
    { type: 'agent.state', data: { agent: 'tester', state: 'done', note: 'Onaylandı' } },
    { type: 'agent.say', data: { agent: 'tester', kind: 'talk', text: '✓', ms: 3000 } },
    { type: 'board.move', data: { task: 'T1', stage: 'onay', state: 'done' } },
    { type: 'board.move', data: { task: 'T2', stage: 'gelistirme', state: 'active' } },
    { type: 'cat', data: { action: 'sleep' } },
  ] },
  { at: 104000, events: [
    { type: 'agent.state', data: { agent: 'developer', state: 'idle' } },
    { type: 'agent.state', data: { agent: 'tester', state: 'idle' } },
    { type: 'agent.state', data: { agent: 'designer', state: 'idle' } },
    { type: 'agent.home', data: { agent: 'organizer' } },
  ] },
]

const LOOP_MS = 112_000

function runMockDirector(onEvent: (e: SceneEvent) => void): { stop: () => void } {
  let timers: ReturnType<typeof setTimeout>[] = []
  let stopped = false
  const cycle = () => {
    if (stopped) return
    timers = SCRIPT.map(b => setTimeout(() => { if (!stopped) b.events.forEach(onEvent) }, b.at))
    timers.push(setTimeout(cycle, LOOP_MS))
  }
  cycle()
  return {
    stop() {
      stopped = true
      timers.forEach(clearTimeout)
    },
  }
}
