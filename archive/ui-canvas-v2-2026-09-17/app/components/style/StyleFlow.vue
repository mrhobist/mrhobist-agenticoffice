<template>
  <div class="wrap">
    <div ref="host" class="fill" />
    <div class="hud">
      <span v-for="a in busy" :key="a.key" class="chip note" :style="{ '--c': a.hex }">
        {{ a.short }} · {{ a.task }}
      </span>
    </div>
  </div>
</template>

<script setup lang="ts">
import * as THREE from 'three'
import { EffectComposer } from 'three/examples/jsm/postprocessing/EffectComposer.js'
import { RenderPass } from 'three/examples/jsm/postprocessing/RenderPass.js'
import { UnrealBloomPass } from 'three/examples/jsm/postprocessing/UnrealBloomPass.js'
import { type RunView, STATE_TINT, type AgentState } from '~/composables/useTeamState'

/**
 * USLUP A — Akis grafigi.
 *
 * Ofis metaforu yok; boru hattinin kendisi gorsel. Dugumler roller, hatlar is
 * devri. Sadece AKTIF hatlarda parcacik akar, boylece o an nerede oldugu
 * tek bakista okunur. Durum halkanin rengiyle, mesgullik cekirdegin
 * parlakligiyla tasinir.
 */

const host = useTemplateRef<HTMLDivElement>('host')
const props = defineProps<{ run: RunView }>()
const emit = defineEmits<{ select: [key: string] }>()
const { run } = toRefs(props)
const busy = computed(() => run.value.agents.filter(a => a.task))

/** Dugum yerlesimi — boru hatti sirasi soldan saga, ask hatti asagi. */
const LAYOUT: Record<string, [number, number]> = {
  analyst: [-9.5, 2.6],
  designer: [-5, -1.4],
  organizer: [-0.4, 2.4],
  developer: [4.4, -1.6],
  tester: [9.4, 2.2],
  manager: [2.0, -6.2],
}

function labelSprite(text: string, color: string): THREE.Sprite {
  const c = document.createElement('canvas')
  c.width = 256; c.height = 80
  const x = c.getContext('2d')!
  x.font = 'bold 44px Inter, system-ui, sans-serif'
  x.textAlign = 'center'; x.textBaseline = 'middle'
  x.fillStyle = color
  x.fillText(text, 128, 42)
  const t = new THREE.CanvasTexture(c)
  t.colorSpace = THREE.SRGBColorSpace
  const s = new THREE.Sprite(new THREE.SpriteMaterial({ map: t, transparent: true, depthWrite: false }))
  s.scale.set(3.0, 0.94, 1)
  return s
}

let stop: (() => void) | undefined

onMounted(() => {
  const el = host.value
  if (!el) return

  const scene = new THREE.Scene()
  scene.background = new THREE.Color(0x05060a)

  const camera = new THREE.PerspectiveCamera(42, 1, 0.1, 200)
  camera.position.set(0, 0.6, 25)

  const renderer = new THREE.WebGLRenderer({ antialias: true })
  renderer.setPixelRatio(Math.min(devicePixelRatio, 2))
  renderer.toneMapping = THREE.ACESFilmicToneMapping
  renderer.outputColorSpace = THREE.SRGBColorSpace
  el.appendChild(renderer.domElement)

  const composer = new EffectComposer(renderer)
  composer.addPass(new RenderPass(scene, camera))
  const bloom = new UnrealBloomPass(new THREE.Vector2(1, 1), 1.05, 0.62, 0.14)
  composer.addPass(bloom)

  scene.add(new THREE.AmbientLight(0xffffff, 0.3))

  // Derinlik icin cok sonuk bir yildiz alani — bosluk olu durmasin.
  {
    const n = 420
    const pos = new Float32Array(n * 3)
    for (let i = 0; i < n; i++) {
      pos[i * 3] = (Math.random() - 0.5) * 70
      pos[i * 3 + 1] = (Math.random() - 0.5) * 44
      pos[i * 3 + 2] = -14 - Math.random() * 26
    }
    const g = new THREE.BufferGeometry()
    g.setAttribute('position', new THREE.BufferAttribute(pos, 3))
    scene.add(new THREE.Points(g, new THREE.PointsMaterial({
      color: 0x3b4a6b, size: 0.11, transparent: true, opacity: 0.6,
    })))
  }

  // ─── Dugumler ───────────────────────────────────────────────────────────
  type Node = {
    key: string
    core: THREE.Mesh
    ring: THREE.Mesh
    light: THREE.PointLight
    pos: THREE.Vector3
  }
  const nodes = new Map<string, Node>()

  for (const a of run.value.agents) {
    const [x, y] = LAYOUT[a.key]!
    const pos = new THREE.Vector3(x, y, 0)
    const col = new THREE.Color(a.hex)

    const core = new THREE.Mesh(
      new THREE.IcosahedronGeometry(0.74, 3),
      new THREE.MeshBasicMaterial({ color: col }),
    )
    core.position.copy(pos)
    scene.add(core)

    const ring = new THREE.Mesh(
      new THREE.TorusGeometry(1.32, 0.05, 12, 72),
      new THREE.MeshBasicMaterial({ color: col, transparent: true }),
    )
    ring.position.copy(pos)
    scene.add(ring)

    const light = new THREE.PointLight(col, 8, 9, 2)
    light.position.copy(pos)
    scene.add(light)

    const lbl = labelSprite(a.short, a.hex)
    lbl.position.set(x, y - 2.15, 0)
    scene.add(lbl)

    nodes.set(a.key, { key: a.key, core, ring, light, pos })
  }

  // ─── Hatlar ─────────────────────────────────────────────────────────────
  type Flow = {
    id: string
    curve: THREE.QuadraticBezierCurve3
    line: THREE.Line
    pts: THREE.Points
    kind: string
  }
  const flows: Flow[] = []

  for (const e of run.value.edges) {
    const p0 = nodes.get(e.from)!.pos
    const p1 = nodes.get(e.to)!.pos
    const mid = p0.clone().add(p1).multiplyScalar(0.5)
    if (e.kind === 'reject') { mid.y += 3.4; mid.z -= 2.4 }
    else if (e.kind === 'ask') { mid.z += 2.2 }
    else { mid.z += 1.4 }

    const curve = new THREE.QuadraticBezierCurve3(p0, mid, p1)
    const color = e.kind === 'reject' ? 0xe05252 : e.kind === 'ask' ? 0x8b7ff0 : 0x5f7fd0

    const line = new THREE.Line(
      new THREE.BufferGeometry().setFromPoints(curve.getPoints(64)),
      new THREE.LineBasicMaterial({ color, transparent: true, opacity: 0.14 }),
    )
    scene.add(line)

    const n = 16
    const pg = new THREE.BufferGeometry()
    pg.setAttribute('position', new THREE.BufferAttribute(new Float32Array(n * 3), 3))
    const pts = new THREE.Points(pg, new THREE.PointsMaterial({
      color, size: 0.3, transparent: true, opacity: 0,
    }))
    scene.add(pts)

    flows.push({ id: `${e.from}>${e.to}`, curve, line, pts, kind: e.kind })
  }

  const resize = () => {
    const w = el.clientWidth, h = el.clientHeight
    if (!w || !h) return
    camera.aspect = w / h
    camera.updateProjectionMatrix()
    renderer.setSize(w, h, false)
    composer.setSize(w, h)
    bloom.resolution.set(w, h)
  }
  resize()
  const ro = new ResizeObserver(resize); ro.observe(el)

  // Dugume tiklaninca panel o ajanda acilir.
  const ray = new THREE.Raycaster()
  const ndc = new THREE.Vector2()
  const cores = [...nodes.values()].map(n => n.core)
  const onClick = (ev: MouseEvent) => {
    const r = el.getBoundingClientRect()
    ndc.x = ((ev.clientX - r.left) / r.width) * 2 - 1
    ndc.y = -((ev.clientY - r.top) / r.height) * 2 + 1
    ray.setFromCamera(ndc, camera)
    const hit = ray.intersectObjects(cores, false)[0]
    if (!hit) return
    for (const [key, n] of nodes) if (n.core === hit.object) { emit('select', key); return }
  }
  el.addEventListener('click', onClick)

  // Durum -> gorsel yogunluk. `done` sonuk, `working` parlak ve nabizli.
  const INTENSITY: Record<AgentState, { core: number; ring: number; light: number; pulse: number }> = {
    idle: { core: 0.16, ring: 0.18, light: 1.2, pulse: 0 },
    working: { core: 1.0, ring: 0.95, light: 11, pulse: 1 },
    blocked: { core: 0.85, ring: 0.9, light: 9, pulse: 2.6 },
    waiting: { core: 0.5, ring: 0.6, light: 5, pulse: 0.5 },
    done: { core: 0.34, ring: 0.42, light: 2.6, pulse: 0 },
  }

  let frame = 0
  const tick = () => {
    frame = requestAnimationFrame(tick)
    const t = performance.now() / 1000
    const snap = run.value

    for (const a of snap.agents) {
      const n = nodes.get(a.key)!
      const k = INTENSITY[a.state]
      const p = k.pulse ? (Math.sin(t * (1.6 + k.pulse)) + 1) / 2 : 0

      ;(n.core.material as THREE.MeshBasicMaterial).color
        .set(a.hex).multiplyScalar(k.core + p * 0.28)
      n.core.scale.setScalar(1 + p * 0.07)

      const rm = n.ring.material as THREE.MeshBasicMaterial
      rm.color.set(a.state === 'idle' || a.state === 'done' ? STATE_TINT[a.state] : a.hex)
      rm.opacity = k.ring * (0.7 + p * 0.5)
      n.ring.rotation.z += a.state === 'working' ? 0.012 : 0.002
      n.ring.scale.setScalar(1 + p * 0.06)

      n.light.intensity = k.light + p * 3
    }

    const active = new Set(snap.edges.filter(e => e.active).map(e => `${e.from}>${e.to}`))
    for (const f of flows) {
      const on = active.has(f.id)
      const pm = f.pts.material as THREE.PointsMaterial
      pm.opacity += ((on ? 0.95 : 0) - pm.opacity) * 0.12
      ;(f.line.material as THREE.LineBasicMaterial).opacity +=
        ((on ? 0.55 : 0.12) - (f.line.material as THREE.LineBasicMaterial).opacity) * 0.12

      if (pm.opacity > 0.01) {
        const arr = f.pts.geometry.attributes.position!.array as Float32Array
        const n = arr.length / 3
        const speed = f.kind === 'reject' ? 0.34 : 0.22
        for (let i = 0; i < n; i++) {
          const u = (t * speed + i / n) % 1
          const v = f.curve.getPoint(u)
          arr[i * 3] = v.x; arr[i * 3 + 1] = v.y; arr[i * 3 + 2] = v.z
        }
        f.pts.geometry.attributes.position!.needsUpdate = true
      }
    }

    scene.rotation.y = Math.sin(t * 0.1) * 0.06
    composer.render()
  }
  tick()

  stop = () => {
    cancelAnimationFrame(frame); ro.disconnect()
    el.removeEventListener('click', onClick)
    composer.dispose(); renderer.dispose(); el.removeChild(renderer.domElement)
  }
})

onBeforeUnmount(() => stop?.())
</script>

<style scoped>
.wrap { position: relative; width: 100%; height: 100%; }
.fill { width: 100%; height: 100%; cursor: pointer; }

.hud {
  position: absolute;
  left: 16px; bottom: 14px;
  display: flex; flex-wrap: wrap; gap: 6px;
  pointer-events: none;
}

.chip {
  background: #151a26cc;
  border: 1px solid #2b3550;
  border-radius: 999px;
  padding: 4px 11px;
  font-size: 12px;
  color: var(--ink);
  backdrop-filter: blur(6px);
}

.chip.note {
  border-color: color-mix(in srgb, var(--c) 55%, transparent);
  color: var(--c);
}
</style>
