import * as THREE from 'three';

export function createInspectionLayer(scene, devices) {
  const root = new THREE.Group();
  root.name = 'AI rule inspection';
  root.visible = false;
  scene.add(root);

  const C = { ice: 0x167568, ai: 0x52616b };
  const glow = (color, opacity = 1) => new THREE.MeshBasicMaterial({ color, transparent: opacity < 1, opacity, toneMapped: false });
  const cyl = (r1, r2, h, seg = 32, open = false) => new THREE.CylinderGeometry(r1, r2, h, seg, 1, open);
  function mesh(geo, mat, x = 0, y = 0, z = 0, parent, shadow = true) {
    const object = new THREE.Mesh(geo, mat);
    object.position.set(x, y, z);
    object.castShadow = shadow;
    object.receiveShadow = shadow;
    if (parent) parent.add(object);
    return object;
  }
  function dotTexture(inner = 'rgba(255,255,255,1)') {
    const canvas = document.createElement('canvas');
    canvas.width = canvas.height = 64;
    const context = canvas.getContext('2d');
    const gradient = context.createRadialGradient(32, 32, 0, 32, 32, 32);
    gradient.addColorStop(0, inner);
    gradient.addColorStop(0.25, 'rgba(255,255,255,.65)');
    gradient.addColorStop(1, 'rgba(255,255,255,0)');
    context.fillStyle = gradient;
    context.fillRect(0, 0, 64, 64);
    const texture = new THREE.CanvasTexture(canvas);
    texture.colorSpace = THREE.SRGBColorSpace;
    return texture;
  }
  const DOT = dotTexture();

  const CORE = new THREE.Vector3(0, 4.35, -3);
  const core = new THREE.Group();
  core.name = 'AI neural brain';
  core.position.copy(CORE);
  core.scale.setScalar(1.5);
  root.add(core);
  const centerSphere = mesh(new THREE.SphereGeometry(0.3, 32, 16), new THREE.MeshStandardMaterial({
    color: 0xd7e0e2, roughness: 0.72, metalness: 0,
    transparent: true, opacity: 0.58, depthWrite: false,
    emissive: 0xfff4e2, emissiveIntensity: 0.18 }), 0, 0, 0, core, false);
  centerSphere.name = 'AI milky light';
  const innerGlow = mesh(new THREE.SphereGeometry(0.11, 24, 16), new THREE.MeshBasicMaterial({
    color: 0xfff8ed, transparent: true, opacity: 0.62, depthWrite: false, toneMapped: false }),
    0, 0, 0, core, false);
  innerGlow.name = 'AI internal light';
  core.add(innerGlow);
  const ico = new THREE.LineSegments(
    new THREE.EdgesGeometry(new THREE.IcosahedronGeometry(0.95, 1)),
    new THREE.LineBasicMaterial({ color: C.ai, transparent: true, opacity: 0.75, toneMapped: false }));
  ico.name = 'AI reference lattice';
  core.add(ico);
  const rings = [1.45, 1.85].map((radius, index) => {
    const ring = new THREE.Mesh(new THREE.TorusGeometry(radius, 0.012 - index * 0.003, 6, 160), glow(index ? C.ice : C.ai, 0.8));
    ring.rotation.x = Math.PI / 2 + (index ? 0.35 : -0.25);
    core.add(ring);
    return ring;
  });
  const coreLight = new THREE.PointLight(0xfff4e2, 9, 5.5, 2);
  core.add(coreLight);
  core.traverse(object => {
    if (!object.material) return;
    object.material.depthTest = false;
    object.renderOrder = 30;
  });

  const dust = (() => {
    const count = 220;
    const positions = new Float32Array(count * 3);
    for (let i = 0; i < count; i++) {
      const radius = Math.sqrt(Math.random()) * 7;
      const angle = Math.random() * Math.PI * 2;
      positions.set([Math.cos(angle) * radius, Math.random() * 7, Math.sin(angle) * radius], i * 3);
    }
    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute('position', new THREE.BufferAttribute(positions, 3));
    const points = new THREE.Points(geometry, new THREE.PointsMaterial({
      size: 0.07, map: DOT, color: 0xa9ccc0, transparent: true, opacity: 0.55,
      depthWrite: false, blending: THREE.AdditiveBlending, toneMapped: false }));
    root.add(points);
    return { geometry, positions, count };
  })();

  const beamMat = new THREE.LineBasicMaterial({ color: C.ai, transparent: true, opacity: 0.8, toneMapped: false });
  let beamLine = null;
  let beamCurve = null;
  const beamPulses = Array.from({ length: 6 }, (_, index) => {
    const pulse = new THREE.Sprite(new THREE.SpriteMaterial({
      map: DOT, color: 0xc8d3d6, transparent: true,
      depthWrite: false, blending: THREE.AdditiveBlending, toneMapped: false }));
    pulse.scale.setScalar(0.38);
    pulse.userData.t = index / 6;
    root.add(pulse);
    return pulse;
  });
  const scanTex = (() => {
    const canvas = document.createElement('canvas');
    canvas.width = 4;
    canvas.height = 128;
    const context = canvas.getContext('2d');
    const gradient = context.createLinearGradient(0, 128, 0, 0);
    gradient.addColorStop(0, 'rgba(255,255,255,.9)');
    gradient.addColorStop(1, 'rgba(255,255,255,0)');
    context.fillStyle = gradient;
    context.fillRect(0, 0, 4, 128);
    return new THREE.CanvasTexture(canvas);
  })();
  const scanShell = mesh(cyl(1.3, 1.3, 1, 64, true), new THREE.MeshBasicMaterial({
    color: C.ai, map: scanTex, transparent: true, opacity: 0.35, side: THREE.DoubleSide,
    depthWrite: false, blending: THREE.AdditiveBlending, toneMapped: false }), 0, 0, 0, root, false);
  scanShell.name = 'AI scan shell';
  const scanRing = new THREE.Mesh(new THREE.TorusGeometry(1.32, 0.02, 6, 120), glow(C.ai));
  scanRing.name = 'AI scan ring';
  scanRing.rotation.x = Math.PI / 2;
  root.add(scanRing);

  let targetId = null;
  let targetModel = null;
  let scanHeight = 2.4;
  let scanBase = 0;
  let presentationMode = 'overview';
  const coreAnchor = CORE.clone();
  const lastBeamAnchor = CORE.clone();
  const targetEnd = new THREE.Vector3();
  const lastTargetPosition = new THREE.Vector3(NaN, NaN, NaN);
  const beamPoint = new THREE.Vector3();

  function updateBeamGeometry() {
    if (!beamCurve) {
      const mid = coreAnchor.clone().lerp(targetEnd, 0.5);
      mid.y = Math.max(coreAnchor.y, targetEnd.y) + 0.8;
      beamCurve = new THREE.QuadraticBezierCurve3(coreAnchor.clone(), mid, targetEnd.clone());
    } else {
      beamCurve.v0.copy(coreAnchor);
      beamCurve.v1.copy(coreAnchor).lerp(targetEnd, 0.5);
      beamCurve.v1.y = Math.max(coreAnchor.y, targetEnd.y) + 0.8;
      beamCurve.v2.copy(targetEnd);
    }
    if (!beamLine) {
      beamLine = new THREE.Line(new THREE.BufferGeometry().setFromPoints(beamCurve.getPoints(60)), beamMat);
      beamLine.name = 'AI inspection curve';
      root.add(beamLine);
      return;
    }
    const positions = beamLine.geometry.attributes.position;
    for (let i = 0; i < positions.count; i++) {
      beamCurve.getPoint(i / (positions.count - 1), beamPoint);
      positions.setXYZ(i, beamPoint.x, beamPoint.y, beamPoint.z);
    }
    positions.needsUpdate = true;
  }

  function updateTargetLayout(model) {
    const { x, y, z } = model.position;
    scanBase = y + 0.04;
    const deviceHeight = Math.max(1.5, model.userData.height || 2.4);
    scanHeight = deviceHeight + 0.1;
    targetEnd.set(x, scanBase + deviceHeight + 0.05, z);
    scanShell.scale.set(1, scanHeight, 1);
    scanShell.position.set(x, scanBase + scanHeight / 2, z);
    scanRing.position.set(x, scanBase + 0.05, z);
    lastTargetPosition.copy(model.position);
    updateBeamGeometry();
    lastBeamAnchor.copy(coreAnchor);
  }

  // ---- HTML HUD: fullscreen banner + tether from the WPF console to the inspected device ----
  const STATE_COLORS = { live: '#12704F', alert: '#A41626', stale: '#8A5A00', off: '#5A5F63' };
  const TETHER_CASING = 'rgba(251,250,247,.85)';
  let focusState = 'off';
  let focusName = '';
  let layout = { anchorX: NaN, anchorY: NaN, scale: 1, fullscreen: false, panels: true, left: 0, top: 0, right: 0, bottom: 0 };
  let lastCamera = null;
  let tetherVisible = false;
  let tetherKey = '';
  const tetherPoint = new THREE.Vector3();

  function setText(element, text) {
    if (element && element.textContent !== text) element.textContent = text;
  }

  function applyBanner() {
    const banner = document.getElementById('ai-inspection-banner');
    if (banner) banner.classList.toggle('hidden', !(targetId && layout.fullscreen));
  }

  function setTetherVisible(visible) {
    if (tetherVisible === visible) return;
    tetherVisible = visible;
    if (!visible) tetherKey = '';
    document.getElementById('ai-tether')?.classList.toggle('hidden', !visible);
    document.getElementById('ai-tether-tag')?.classList.toggle('hidden', !visible);
  }

  function updateTether(camera) {
    const entry = targetId ? devices.get(targetId) : null;
    const s = layout.scale > 0 ? layout.scale : 1;
    const ax = layout.anchorX;
    const ay = layout.anchorY;
    if (!camera || !entry || !entry.model.visible || layout.fullscreen || layout.panels === false
      || !Number.isFinite(ax) || !Number.isFinite(ay)) {
      setTetherVisible(false);
      return;
    }
    const host = document.getElementById('viewport');
    const tag = document.getElementById('ai-tether-tag');
    const casing = document.getElementById('ai-tether-casing');
    const line = document.getElementById('ai-tether-line');
    const ring = document.getElementById('ai-tether-ring');
    const dot = document.getElementById('ai-tether-dot');
    if (!host || !tag || !casing || !line || !ring || !dot) return;
    const W = host.clientWidth;
    const H = host.clientHeight;
    tetherPoint.copy(entry.model.position);
    tetherPoint.y += (entry.model.userData.height || 1.2) * 0.6;
    tetherPoint.project(camera);
    if (!Number.isFinite(tetherPoint.x) || !Number.isFinite(tetherPoint.y) || !Number.isFinite(tetherPoint.z)
      || tetherPoint.z > 1) {
      setTetherVisible(false);
      return;
    }
    const tx = Math.round((tetherPoint.x + 1) / 2 * W * 2) / 2;
    const ty = Math.round((1 - tetherPoint.y) / 2 * H * 2) / 2;
    if (tx < layout.left + 8 || tx > W - layout.right - 8 || ty < layout.top + 8 || ty > H - layout.bottom - 8) {
      setTetherVisible(false);
      return;
    }
    const color = STATE_COLORS[focusState] || STATE_COLORS.off;
    const key = `${targetId}|${ax}|${ay}|${tx}|${ty}|${s}|${W}|${layout.right}|${color}`;
    if (tetherVisible && key === tetherKey) return;
    setTetherVisible(true);
    tetherKey = key;
    const end = ty - 6 * s;
    const elbow = Math.max(ay, Math.min(ay + 28 * s, end));
    const d = `M ${ax} ${ay} V ${elbow} L ${tx} ${end}`;
    casing.setAttribute('d', d);
    casing.setAttribute('stroke', TETHER_CASING);
    casing.setAttribute('stroke-width', String(4 * s));
    line.setAttribute('d', d);
    line.setAttribute('stroke', color);
    line.setAttribute('stroke-width', String(1.6 * s));
    ring.setAttribute('cx', String(tx));
    ring.setAttribute('cy', String(ty));
    ring.setAttribute('r', String(9 * s));
    ring.setAttribute('stroke', color);
    ring.setAttribute('stroke-width', String(1.6 * s));
    ring.setAttribute('fill', TETHER_CASING);
    dot.setAttribute('cx', String(tx));
    dot.setAttribute('cy', String(ty));
    dot.setAttribute('r', String(3.5 * s));
    dot.setAttribute('fill', color);
    const tagLeft = Math.min(tx + 17 * s, W - layout.right - tag.offsetWidth * s - 8);
    tag.style.left = `${tagLeft}px`;
    tag.style.top = `${ty - 26 * s}px`;
    tag.style.borderColor = color;
  }

  function setLayout(next = {}) {
    layout = { ...layout, ...next };
    applyBanner();
    if (lastCamera) updateTether(lastCamera);
  }

  // Idempotent: the host re-sends the focus about once per second.
  function setFocus(data = {}) {
    if ('state' in data) focusState = data.state || 'off';
    if ('name' in data) focusName = data.name || '';
    const target = document.getElementById('ai-inspection-target');
    const task = document.getElementById('ai-inspection-task');
    if (!data.id) {
      targetId = null;
      targetModel = null;
      root.visible = false;
      applyBanner();
      setTetherVisible(false);
      return;
    }
    setText(target, focusName ? `${data.id} ${focusName} · AI 巡检中` : `${data.id} · AI 巡检中`);
    if ('task' in data) setText(task, data.task || '综合数据分析');
    setText(document.getElementById('ai-tether-id'), String(data.id));
    const entry = devices.get(data.id);
    if (!entry) {
      targetId = data.id;
      targetModel = null;
      root.visible = false;
      applyBanner();
      return;
    }
    if (targetId === data.id && targetModel === entry.model) {
      applyBanner();
      return;
    }
    targetId = data.id;
    targetModel = entry.model;
    updateTargetLayout(entry.model);
    root.visible = false;
    applyBanner();
  }

  function update(delta, elapsed, camera = null, mode = 'overview') {
    if (camera) {
      lastCamera = camera;
      updateTether(camera);
    }
    if (!targetId) return;
    const entry = devices.get(targetId);
    if (!entry) {
      root.visible = false;
      return;
    }
    if (entry.model !== targetModel) setFocus({ id: targetId });
    if (!root.visible) return;

    const modeChanged = presentationMode !== mode;
    presentationMode = mode;
    if (modeChanged) lastTargetPosition.set(NaN, NaN, NaN);
    if (!lastTargetPosition.equals(entry.model.position)) updateTargetLayout(entry.model);

    if (mode === 'focus' && camera) {
      const depth = 16;
      const height = Math.tan(THREE.MathUtils.degToRad(camera.fov / 2)) * depth * 0.35;
      coreAnchor.set(0, height, -depth).applyQuaternion(camera.quaternion).add(camera.position);
    } else {
      coreAnchor.copy(CORE);
    }
    core.position.copy(coreAnchor);
    core.position.y += Math.sin(elapsed * 1.1) * 0.08;
    if (modeChanged || !lastBeamAnchor.equals(coreAnchor)) {
      updateBeamGeometry();
      lastBeamAnchor.copy(coreAnchor);
    }

    ico.rotation.y += delta * 0.35;
    ico.rotation.x += delta * 0.12;
    rings[0].rotation.z += delta * 0.5;
    rings[1].rotation.z -= delta * 0.32;

    for (let i = 0; i < dust.count; i++) {
      let y = dust.positions[i * 3 + 1] + delta * 0.25;
      if (y > 7) y = 0;
      dust.positions[i * 3 + 1] = y;
    }
    dust.geometry.attributes.position.needsUpdate = true;

    const k = Math.sin(elapsed * 2.4) * 0.5 + 0.5;
    scanRing.position.set(entry.model.position.x, scanBase + 0.05 + k * scanHeight, entry.model.position.z);
    scanShell.material.opacity = 0.22 + Math.sin(elapsed * 4) * 0.06;
    beamPulses.forEach(pulse => {
      pulse.userData.t = (pulse.userData.t + delta * 0.45) % 1;
      beamCurve.getPoint(pulse.userData.t, pulse.position);
    });
  }

  return { setFocus, update, setLayout };
}
