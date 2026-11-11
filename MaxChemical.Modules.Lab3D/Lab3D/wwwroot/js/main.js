import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { RoomEnvironment } from 'three/addons/environments/RoomEnvironment.js';
import { RectAreaLightUniformsLib } from 'three/addons/lights/RectAreaLightUniformsLib.js';
import { RGBELoader } from 'three/addons/loaders/RGBELoader.js';
import { CSS2DObject, CSS2DRenderer } from 'three/addons/renderers/CSS2DRenderer.js';
import { EffectComposer } from 'three/addons/postprocessing/EffectComposer.js';
import { RenderPass } from 'three/addons/postprocessing/RenderPass.js';
import { SSAOPass } from 'three/addons/postprocessing/SSAOPass.js';
import { OutlinePass } from 'three/addons/postprocessing/OutlinePass.js';
import { UnrealBloomPass } from 'three/addons/postprocessing/UnrealBloomPass.js';
import { OutputPass } from 'three/addons/postprocessing/OutputPass.js';
import { buildDevice, createMaterials } from './models.js';
import { buildContinuousCounters, buildDeviceInfoPanel, buildEnvironment } from './showroom.js';
import { SHOWROOM_SCALE } from './layout.js';
import { createInspectionLayer } from './inspection.js';

const viewport = document.getElementById('viewport');
const devices = new Map();
const fadingWalls = [];
const raycaster = new THREE.Raycaster();
const pointer = new THREE.Vector2();
const overviewPosition = new THREE.Vector3(1.8, 5.1, 12.2);
const overviewTarget = new THREE.Vector3(0, 1.0, -2.7);
let stableDistance = overviewPosition.distanceTo(overviewTarget);
const roomMin = new THREE.Vector3(-7.85 * SHOWROOM_SCALE, 0.65, -10.95 * SHOWROOM_SCALE);
const roomMax = new THREE.Vector3(7.85 * SHOWROOM_SCALE, 5.45, 11.35 * SHOWROOM_SCALE);
const targetMin = new THREE.Vector3(-7.4 * SHOWROOM_SCALE, 0.75, -9.5 * SHOWROOM_SCALE);
const targetMax = new THREE.Vector3(7.4 * SHOWROOM_SCALE, 4.4, 10.5 * SHOWROOM_SCALE);
const clock = new THREE.Clock();
let renderer;
let composer;
let labelRenderer;
let camera;
let controls;
let scene;
let ssaoPass;
let bloomPass;
let outlinePass;
let selectionRing;
let deviceMaterials;
let showroomRoot;
let inspectionLayer;
let focusStage;
const FOCUS_STAGE_Z = -1.4;
const FOCUS_STAGE_TOP = 0.1;
let counterGroups = [];
let reflection;
let reflectionFrame = 0;
let hudFrame = 0;
let lastZoomText = '';
let lastCompassAngle = NaN;
let selectedId = null;
let viewMode = 'overview';
let focusSnapshot = null;
let focusPauseUntil = 0;
const focusCameraMin = new THREE.Vector3();
const focusCameraMax = new THREE.Vector3();
let cameraTween = null;
let hoveredDeviceId = null;
let pinnedDeviceId = null;
let initialized = false;
let quality = 2;
let pointerStart = null;
const touchPointers = new Map();
let pinchState = null;
let lastHover = 0;
let toastTimer;
let frameCounter = 0;
let fpsElapsed = 0;
let lowFpsSamples = 0;
let pendingModelLoads = 0;
let modelLoadFailed = false;
let resolveModelLoads = () => {};

function waitForModelLoads() {
  if (pendingModelLoads === 0) return Promise.resolve();
  return new Promise(resolve => { resolveModelLoads = resolve; });
}

function completeModelLoad(success) {
  if (!success) modelLoadFailed = true;
  pendingModelLoads = Math.max(0, pendingModelLoads - 1);
  if (pendingModelLoads === 0) resolveModelLoads();
}

function post(type, data = {}) {
  if (window.chrome?.webview) window.chrome.webview.postMessage({ type, ...data });
}

function initializeRenderer() {
  scene = new THREE.Scene();
  scene.background = new THREE.Color(0x172128);
  camera = new THREE.PerspectiveCamera(56, viewport.clientWidth / viewport.clientHeight, 0.08, 160);
  camera.position.copy(overviewPosition);
  camera.lookAt(overviewTarget);

  renderer = new THREE.WebGLRenderer({ antialias: true, powerPreference: 'high-performance', stencil: false });
  renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 1.35));
  renderer.setSize(viewport.clientWidth, viewport.clientHeight);
  renderer.outputColorSpace = THREE.SRGBColorSpace;
  renderer.toneMapping = THREE.ACESFilmicToneMapping;
  renderer.toneMappingExposure = 0.96;
  renderer.shadowMap.enabled = true;
  renderer.shadowMap.type = THREE.PCFSoftShadowMap;
  renderer.shadowMap.autoUpdate = false;
  renderer.shadowMap.needsUpdate = true;
  const gl = renderer.getContext();
  const gpuInfo = gl.getExtension('WEBGL_debug_renderer_info');
  if (gpuInfo) document.getElementById('fps-label').title = gl.getParameter(gpuInfo.UNMASKED_RENDERER_WEBGL);
  renderer.domElement.style.position = 'absolute';
  renderer.domElement.style.inset = '0';
  renderer.domElement.tabIndex = 0;
  viewport.prepend(renderer.domElement);

  renderer.domElement.addEventListener('webglcontextlost', event => {
    event.preventDefault();
    window.reportSceneError('WebGL 上下文已丢失，请重新加载场景。');
  });

  controls = new OrbitControls(camera, renderer.domElement);
  controls.target.copy(overviewTarget);
  controls.enableDamping = true;
  controls.dampingFactor = 0.08;
  controls.enableZoom = false;
  controls.rotateSpeed = 0.65;
  controls.panSpeed = 0.65;
  controls.screenSpacePanning = true;
  controls.minPolarAngle = 0;
  controls.maxPolarAngle = Math.PI;
  controls.minAzimuthAngle = -Infinity;
  controls.maxAzimuthAngle = Infinity;
  controls.minDistance = 2.2;
  controls.maxDistance = 28;
  controls.update();
  constrainCamera();
  controls.addEventListener('start', () => {
    cameraTween = null;
    if (viewMode === 'focus') {
      controls.autoRotate = false;
      focusPauseUntil = performance.now() + 3000;
    }
    document.getElementById('focus-badge').classList.add('hidden');
  });
  controls.addEventListener('end', () => {
    if (viewMode === 'focus') focusPauseUntil = performance.now() + 3000;
  });

  labelRenderer = new CSS2DRenderer();
  labelRenderer.setSize(viewport.clientWidth, viewport.clientHeight);
  Object.assign(labelRenderer.domElement.style, { position: 'absolute', inset: '0', pointerEvents: 'none', zIndex: '4' });
  viewport.appendChild(labelRenderer.domElement);

  const pmrem = new THREE.PMREMGenerator(renderer);
  const room = new RoomEnvironment();
  const environment = pmrem.fromScene(room, 0.045);
  scene.environment = environment.texture;
  scene.environmentIntensity = 0.78;
  room.dispose();
  pmrem.dispose();
  new RGBELoader().load('assets/hdr/studio_small_09_1k.hdr', texture => {
    texture.mapping = THREE.EquirectangularReflectionMapping;
    scene.environment = texture;
    scene.environmentIntensity = 0.72;
    environment.dispose();
  }, undefined, () => {
    scene.environmentIntensity = 0.72;
  });

  deviceMaterials = createMaterials();
  showroomRoot = buildEnvironment(scene, deviceMaterials);
  counterGroups = buildContinuousCounters(scene, deviceMaterials);
  showroomRoot.traverse(object => {
    if (object.userData.fadeWall) registerFadeGroup(object);
    if (object.userData.isFloorReflection) {
      reflection = object;
      reflection.getRenderTarget().setSize(512, 512);
      const renderReflection = reflection.onBeforeRender.bind(reflection);
      reflection.onBeforeRender = (...args) => {
        if (quality === 0 || args[1].overrideMaterial) return;
        reflectionFrame++;
        const interval = viewMode === 'focus' ? 4 : quality === 2 ? 2 : 4;
        if (reflectionFrame % interval === 0) renderReflection(...args);
      };
    }
  });
  addLighting();
  focusStage = createFocusStage();
  scene.add(focusStage);

  selectionRing = new THREE.Group();
  const ringMaterial = new THREE.MeshBasicMaterial({ color: 0xd4a34d, transparent: true, opacity: 0.82, side: THREE.DoubleSide, depthWrite: false });
  const ring = new THREE.Mesh(new THREE.RingGeometry(1.23, 1.27, 96), ringMaterial);
  ring.rotation.x = -Math.PI / 2;
  selectionRing.add(ring);
  const outer = new THREE.Mesh(new THREE.RingGeometry(1.42, 1.435, 96), ringMaterial.clone());
  outer.material.opacity = 0.25;
  outer.rotation.x = -Math.PI / 2;
  selectionRing.add(outer);
  selectionRing.visible = false;
  scene.add(selectionRing);

  const renderTarget = new THREE.WebGLRenderTarget(viewport.clientWidth, viewport.clientHeight, { type: THREE.HalfFloatType, samples: 2 });
  composer = new EffectComposer(renderer, renderTarget);
  composer.addPass(new RenderPass(scene, camera));
  ssaoPass = new SSAOPass(scene, camera, viewport.clientWidth, viewport.clientHeight);
  ssaoPass.kernelRadius = 11;
  ssaoPass.minDistance = 0.0015;
  ssaoPass.maxDistance = 0.115;
  composer.addPass(ssaoPass);
  outlinePass = new OutlinePass(new THREE.Vector2(viewport.clientWidth, viewport.clientHeight), scene, camera);
  outlinePass.edgeStrength = 3.2;
  outlinePass.edgeGlow = 0.16;
  outlinePass.edgeThickness = 1.1;
  outlinePass.pulsePeriod = 0;
  outlinePass.visibleEdgeColor.set('#d4a34d');
  outlinePass.hiddenEdgeColor.set('#6b4a2c');
  composer.addPass(outlinePass);
  bloomPass = new UnrealBloomPass(new THREE.Vector2(viewport.clientWidth, viewport.clientHeight), 0.12, 0.24, 1.2);
  composer.addPass(bloomPass);
  composer.addPass(new OutputPass());
  ssaoPass.setSize(Math.max(1, Math.floor(viewport.clientWidth * 0.5)), Math.max(1, Math.floor(viewport.clientHeight * 0.5)));

  inspectionLayer = createInspectionLayer(scene, devices);
  bindInteraction();
  const observer = new ResizeObserver(resize);
  observer.observe(viewport);
  renderer.setAnimationLoop(animate);
  post('scene-ready');
}

function addLighting() {
  scene.add(new THREE.HemisphereLight(0xe8edf0, 0x343239, 0.75));
  const key = new THREE.DirectionalLight(0xfff0d8, 1.35);
  key.position.set(3, 10, 8);
  key.castShadow = true;
  key.shadow.mapSize.set(2048, 2048);
  key.shadow.camera.left = -11;
  key.shadow.camera.right = 11;
  key.shadow.camera.top = 12;
  key.shadow.camera.bottom = -12;
  key.shadow.camera.near = 1;
  key.shadow.camera.far = 35;
  key.shadow.bias = -0.00035;
  key.shadow.normalBias = 0.025;
  key.shadow.radius = 4;
  scene.add(key);
  const rim = new THREE.DirectionalLight(0x9dcfff, 0.5);
  rim.position.set(-6, 6, -2);
  scene.add(rim);
  RectAreaLightUniformsLib.init();
  for (const z of [-6, 0, 6]) {
    const light = new THREE.RectAreaLight(0xffedcf, 3.8, 7.2, 3.2);
    light.position.set(0, 5.65, z);
    light.lookAt(0, 0, z);
    scene.add(light);
  }
  for (const z of [-4, 3]) {
    const blue = new THREE.PointLight(0x4aa9ff, 10, 11, 2);
    blue.position.set(0, 5.5, z);
    scene.add(blue);
  }
  const backLight = new THREE.PointLight(0xffc76d, 16, 10, 2);
  backLight.position.set(0, 4.2, -9.4);
  scene.add(backLight);
}

function liveParameterText(value, unit, online, hasFreshData) {
  if (!online || !hasFreshData || value === null || value === undefined) return '';
  const text = String(value).trim();
  if (!text || text === '--') return '';
  return `${text}${unit ? ` ${unit}` : ''}`;
}

function makeLabel(device) {
  const element = document.createElement('div');
  element.className = `device-label${device.online ? '' : ' offline'}`;
  element.title = `${device.name} · ${device.online ? '已连接' : '未连接'}`;
  element.style.opacity = '0';
  element.style.pointerEvents = 'none';
  const header = document.createElement('div');
  header.className = 'label-head';
  const id = document.createElement('span');
  id.className = 'label-id';
  id.textContent = device.id;
  const status = document.createElement('span');
  status.className = 'label-status';
  status.textContent = device.online ? '已连接' : '未连接';
  header.append(id, status);
  const name = document.createElement('div');
  name.className = 'label-name';
  name.textContent = device.name;
  const english = document.createElement('div');
  english.className = 'label-en';
  english.textContent = device.englishName;
  const value = document.createElement('div');
  value.className = 'label-value';
  value.textContent = liveParameterText(device.primaryValue, device.primaryUnit, device.online, device.hasFreshData);
  element.append(header, name, english, value);
  element.addEventListener('pointerdown', event => event.stopPropagation());
  element.addEventListener('click', event => {
    event.stopPropagation();
    selectDevice(device.id, false, true);
  });
  const label = new CSS2DObject(element);
  label.position.set(0, 0, 0);
  label.center.set(0.5, 1);
  return { label, element, value, status };
}

function updateLabelVisibility() {
  const visibleId = pinnedDeviceId;
  for (const [id, entry] of devices) {
    const visible = id === visibleId;
    entry.element.style.opacity = visible ? '1' : '0';
    entry.element.style.pointerEvents = visible ? 'auto' : 'none';
  }
}

function setHoveredDevice(id) {
  if (hoveredDeviceId === id) return;
  hoveredDeviceId = id;
  if (id && id !== pinnedDeviceId) pinnedDeviceId = null;
  updateLabelVisibility();
}

function pinDeviceLabel(id) {
  pinnedDeviceId = id;
  hoveredDeviceId = null;
  updateLabelVisibility();
}

function clampOverviewTarget(target) {
  target.x = THREE.MathUtils.clamp(target.x, overviewTarget.x - 0.85, overviewTarget.x + 0.85);
  target.y = THREE.MathUtils.clamp(target.y, 0.85, 2.2);
  target.z = THREE.MathUtils.clamp(target.z, overviewTarget.z - 2, overviewTarget.z + 2);
  return target.clamp(targetMin, targetMax);
}

function constrainCamera() {
  controls.target.clamp(targetMin, targetMax);
  camera.position.clamp(roomMin, roomMax);
  camera.lookAt(controls.target);
  stableDistance = camera.position.distanceTo(controls.target);
}

function registerFadeGroup(group) {
  const wall = { group, side: group.userData.fadeWall, opacity: 1, materials: [] };
  const clones = new Map();
  const cloneMaterial = source => {
    if (!clones.has(source)) {
      const material = source.clone();
      clones.set(source, material);
      wall.materials.push({
        material,
        opacity: source.opacity,
        transparent: source.transparent,
        depthWrite: source.depthWrite
      });
    }
    return clones.get(source);
  };
  group.traverse(object => {
    if (!object.isMesh) return;
    object.material = Array.isArray(object.material) ? object.material.map(cloneMaterial) : cloneMaterial(object.material);
  });
  fadingWalls.push(wall);
}

function updateWallTransparency(delta) {
  if (viewMode === 'focus') return;
  const scaleX = showroomRoot?.scale.x ?? SHOWROOM_SCALE;
  const scaleZ = showroomRoot?.scale.z ?? SHOWROOM_SCALE;
  for (const wall of fadingWalls) {
    let outside = 0;
    if (wall.side === 'left') outside = THREE.MathUtils.smoothstep(-camera.position.x, 4.5 * scaleX, 8 * scaleX);
    if (wall.side === 'right') outside = THREE.MathUtils.smoothstep(camera.position.x, 4.5 * scaleX, 8 * scaleX);
    if (wall.side === 'back') outside = THREE.MathUtils.smoothstep(-camera.position.z, 7.5 * scaleZ, 11 * scaleZ);
    const target = 1 - outside * 0.92;
    if (wall.opacity > 0.985 && Math.abs(wall.opacity - target) < 0.004) continue;
    wall.opacity = THREE.MathUtils.damp(wall.opacity, target, 7, delta);
    const fading = wall.opacity < 0.985;
    for (const record of wall.materials) {
      const transparent = fading || record.transparent;
      if (record.material.transparent !== transparent) {
        record.material.transparent = transparent;
        record.material.needsUpdate = true;
      }
      record.material.depthWrite = fading ? false : record.depthWrite;
      record.material.opacity = record.opacity * wall.opacity;
    }
  }
}

function getCounterRanges() {
  const ranges = { left: null, right: null };
  for (const entry of devices.values()) {
    const side = entry.model.position.x < 0 ? 'left' : 'right';
    const z = entry.model.position.z;
    if (!ranges[side]) {
      ranges[side] = { minZ: z - 0.65, maxZ: z + 0.65 };
    } else {
      ranges[side].minZ = Math.min(ranges[side].minZ, z - 0.65);
      ranges[side].maxZ = Math.max(ranges[side].maxZ, z + 0.65);
    }
  }
  if (ranges.left && ranges.right) {
    const minZ = Math.min(ranges.left.minZ, ranges.right.minZ);
    const maxZ = Math.max(ranges.left.maxZ, ranges.right.maxZ);
    ranges.left = { minZ, maxZ };
    ranges.right = { minZ, maxZ };
  }
  return ranges;
}

function disposeCounterGroup(group) {
  const sharedMaterials = new Set(Object.values(deviceMaterials));
  const materials = new Set();
  group.traverse(object => {
    if (!object.isMesh) return;
    object.geometry?.dispose();
    for (const material of Array.isArray(object.material) ? object.material : [object.material]) {
      if (!sharedMaterials.has(material)) materials.add(material);
    }
  });
  for (const material of materials) material.dispose();
  scene.remove(group);
}

function rebuildContinuousCounters() {
  for (const group of counterGroups) disposeCounterGroup(group);
  counterGroups = buildContinuousCounters(scene, deviceMaterials, getCounterRanges());
}

function updateOverviewForRoom() {
  overviewPosition.set(1.2, 5.35, roomMax.z - 0.65);
  overviewTarget.set(0, 1.3, roomMin.z + (roomMax.z - roomMin.z) * 0.45);
}

function ensureRoomForDevice(z, panelCount) {
  const requiredFront = z + 2.4;
  const panelLength = ((Math.max(1, panelCount) - 1) * 3.3 + 4.8) * SHOWROOM_SCALE;
  const scale = Math.max(showroomRoot.scale.z, requiredFront / (11.35 * SHOWROOM_SCALE), panelLength / 24);
  if (scale <= showroomRoot.scale.z + 0.0001) return;
  showroomRoot.scale.z = scale;
  roomMin.z = -10.95 * scale;
  roomMax.z = 11.35 * scale;
  targetMin.z = -9.5 * scale;
  targetMax.z = 10.5 * scale;
  updateOverviewForRoom();
}

function layoutDeviceInfoPanels() {
  const panels = [...devices.values()].map(entry => entry.infoPanel);
  const spacing = 3.3 * SHOWROOM_SCALE;
  const firstZ = -0.25 * SHOWROOM_SCALE - (panels.length - 1) * spacing / 2;
  for (const [index, panel] of panels.entries()) panel.position.z = firstZ + index * spacing;
}

function updateDeviceCountText() {
  const element = document.querySelector('.scene-info-sub');
  if (element) element.textContent = `REALTIME ENVIRONMENT · ${devices.size} DEVICES`;
}

function addDeviceToScene(device) {
  if (devices.has(device.id)) return;
  ensureRoomForDevice(device.position[2], devices.size + 1);
  if (!initialized && device.kind === 'Custom') pendingModelLoads++;
  const model = buildDevice(device, deviceMaterials, (success = true) => {
    renderer.shadowMap.needsUpdate = true;
    if (!initialized) completeModelLoad(success);
  });
  model.position.fromArray(device.position);
  const angle = device.kind === 'Custom' ? 0.18 : Math.PI / 4;
  const side = device.position[0] < 0 ? 1 : -1;
  model.rotation.y = side * angle;
  const counterOffset = 1.9;
  model.userData.kiosk.position.set(side * Math.cos(angle) * counterOffset, -device.position[1], Math.sin(angle) * counterOffset);
  scene.add(model);
  const infoPanel = buildDeviceInfoPanel(scene, device, deviceMaterials);
  registerFadeGroup(infoPanel);
  const labelParts = makeLabel(device);
  labelParts.label.position.set(0, model.userData.height, 0);
  model.add(labelParts.label);
  devices.set(device.id, { data: device, model, infoPanel, ...labelParts });
  layoutDeviceInfoPanels();
  updateDeviceCountText();
}

function removeDeviceFromScene(id) {
  const entry = devices.get(id);
  if (!entry) return;
  if (locatedDeviceId === id) returnOverview();
  if (viewMode === 'focus' && selectedId === id) returnOverview();

  renderer.setAnimationLoop(null);
  try {
    scene.remove(entry.model);
    scene.remove(entry.infoPanel);
    devices.delete(id);
    for (let i = fadingWalls.length - 1; i >= 0; i--) {
      if (fadingWalls[i].group === entry.infoPanel) fadingWalls.splice(i, 1);
    }

    selectedId = null;
    hoveredDeviceId = null;
    pinnedDeviceId = null;
    outlinePass.selectedObjects = [];
    selectionRing.visible = false;
    document.getElementById('focus-badge').classList.add('hidden');
    updateLabelVisibility();

    layoutDeviceInfoPanels();
    rebuildContinuousCounters();
    disposeCounterGroup(entry.model);
    disposeCounterGroup(entry.infoPanel);

    renderer.shadowMap.needsUpdate = true;
    updateDeviceCountText();

    const nextId = devices.keys().next().value;
    if (nextId) {
      selectDevice(nextId, false, true);
    } else {
      setViewName('数字展厅总览');
    }
  } catch (error) {
    window.reportSceneError(`删除设备失败：${error?.message || error}`);
  } finally {
    renderer.setAnimationLoop(animate);
  }
}

function updateDeviceInScene(device) {
  if (!initialized) return;
  const wasSelected = selectedId === device.id;
  removeDeviceFromScene(device.id);
  addDeviceToScene(device);
  rebuildContinuousCounters();
  if (wasSelected) {
    selectDevice(device.id, false, true);
  }
  renderer.shadowMap.needsUpdate = true;
}

async function initializeDevices(payload) {
  if (initialized) return;
  pendingModelLoads = 0;
  modelLoadFailed = false;
  resolveModelLoads = () => {};
  deviceMaterials ??= createMaterials();
  for (const device of payload.devices) ensureRoomForDevice(device.position[2], payload.devices.length);
  for (const device of payload.devices) addDeviceToScene(device);
  rebuildContinuousCounters();
  updateOverviewForRoom();
  stableDistance = overviewPosition.distanceTo(overviewTarget);
  camera.position.copy(overviewPosition);
  controls.target.copy(overviewTarget);
  controls.update();
  constrainCamera();
  initialized = true;
  selectDevice(payload.selectedId || payload.devices[0]?.id, false, false);
  renderer.shadowMap.needsUpdate = true;
  post('scene-loading-progress', { stage: 'models' });
  await waitForModelLoads();
  if (modelLoadFailed) return;
  const inspectionId = devices.has(payload.inspectionId) ? payload.inspectionId : payload.selectedId;
  inspectionLayer?.setFocus({ id: inspectionId, task: payload.inspectionTask });
  post('scene-loading-progress', { stage: 'compile' });
  await renderer.compileAsync(scene, camera);
  post('scene-loading-progress', { stage: 'first-frame' });
  composer.render();
  labelRenderer.render(scene, camera);
  renderer.getContext().finish();
  post('scene-initialized');
}

function setViewName(name) {
  const element = document.getElementById('view-name');
  if (element) element.textContent = name;
}

function selectDevice(id, focus = false, notifyHost = false) {
  const entry = devices.get(id);
  if (!entry) return;
  selectedId = id;
  for (const [deviceId, device] of devices) device.element.classList.toggle('selected', deviceId === id);
  outlinePass.selectedObjects = [entry.model];
  selectionRing.visible = true;
  selectionRing.position.set(entry.model.position.x, entry.model.position.y + 0.025, entry.model.position.z);
  selectionRing.scale.setScalar(entry.model.userData.ringScale ?? 0.86);
  setViewName(`${entry.data.id} · ${entry.data.name}`);
  if (notifyHost) post('device-selected', { id });
}

function createFocusStage() {
  const group = new THREE.Group();
  const platform = new THREE.Group();
  group.position.z = FOCUS_STAGE_Z;
  group.visible = false;
  group.userData.platform = platform;

  const bodyMaterial = new THREE.MeshStandardMaterial({ color: 0xe9e6df, metalness: 0.28, roughness: 0.42 });
  const trimMaterial = new THREE.MeshStandardMaterial({ color: 0xa41626, metalness: 0.35, roughness: 0.36 });
  const body = new THREE.Mesh(new THREE.CylinderGeometry(0.95, 1.02, FOCUS_STAGE_TOP, 72), bodyMaterial);
  body.position.y = FOCUS_STAGE_TOP / 2;
  body.castShadow = true;
  body.receiveShadow = true;
  platform.add(body);

  const trim = new THREE.Mesh(new THREE.TorusGeometry(0.956, 0.012, 8, 96), trimMaterial);
  trim.rotation.x = Math.PI / 2;
  trim.position.y = FOCUS_STAGE_TOP;
  platform.add(trim);

  const shadow = new THREE.Mesh(new THREE.CircleGeometry(1.24, 72), new THREE.ShadowMaterial({ opacity: 0.16 }));
  shadow.rotation.x = -Math.PI / 2;
  shadow.position.y = 0.009;
  shadow.receiveShadow = true;
  platform.add(shadow);
  group.add(platform);

  return group;
}

function captureDevicePose(entry) {
  return {
    position: entry.model.position.clone(),
    quaternion: entry.model.quaternion.clone(),
    scale: entry.model.scale.clone()
  };
}

function restoreFocusDevicePoses() {
  if (!focusSnapshot) return;
  for (const [id, pose] of focusSnapshot.devicePoses) {
    const entry = devices.get(id);
    if (!entry) continue;
    entry.model.position.copy(pose.position);
    entry.model.quaternion.copy(pose.quaternion);
    entry.model.scale.copy(pose.scale);
  }
}

function setNavigationHint(focused) {
  const hint = document.querySelector('.navigation-hint div');
  if (!hint) return;
  hint.innerHTML = focused
    ? '<b>左键</b> 旋转 <i></i><b>滚轮</b> 放大'
    : '<b>左键</b> 旋转 <i></i><b>右键</b> 平移 <i></i><b>滚轮</b> 缩放';
}

let locatedDeviceId = null;

function locateDevice(id) {
  const entry = devices.get(id);
  if (!entry) return;
  locatedDeviceId = id;
  pinnedDeviceId = null;
  hoveredDeviceId = null;
  updateLabelVisibility();
  if (reflection) reflection.visible = false;
  selectDevice(id, false, false);
  entry.model.updateWorldMatrix(true, true);
  const bounds = new THREE.Box3().setFromObject(entry.model);
  const size = bounds.getSize(new THREE.Vector3());
  const radius = Math.max(0.1, Math.max(size.x, size.y, size.z) * 0.5);
  const target = entry.model.position.clone();
  target.y += Math.max(0.35, (entry.model.userData.height || size.y) * 0.55);
  const distance = Math.max(2.2, radius * 3.2);
  const direction = camera.position.clone().sub(controls.target);
  if (direction.lengthSq() < 0.000001) direction.set(0, 0.2, 1);
  direction.normalize();
  const position = target.clone().addScaledVector(direction, distance);
  stableDistance = distance;
  startCameraTween(position, target, 650);
  setViewName(`${entry.data.id} · ${entry.data.name}`);
}

function focusDevice(id) {
  const entry = devices.get(id);
  if (!entry) return;

  if (viewMode !== 'focus') {
    focusSnapshot = {
      position: camera.position.clone(),
      target: controls.target.clone(),
      showroomVisible: showroomRoot.visible,
      counters: counterGroups.map(group => group.visible),
      infoPanels: new Map([...devices.values()].map(item => [item.data.id, item.infoPanel.visible])),
      devicePoses: new Map([...devices.values()].map(item => [item.data.id, captureDevicePose(item)])),
      stageVisible: focusStage.visible,
      background: scene.background.clone(),
      selectionVisible: selectionRing.visible,
      minPolarAngle: controls.minPolarAngle,
      maxPolarAngle: controls.maxPolarAngle,
      minAzimuthAngle: controls.minAzimuthAngle,
      maxAzimuthAngle: controls.maxAzimuthAngle,
      minDistance: controls.minDistance,
      maxDistance: controls.maxDistance,
      panEnabled: controls.enablePan
    };
  }
  for (const item of devices.values()) {
    if (!focusSnapshot.devicePoses.has(item.data.id))
      focusSnapshot.devicePoses.set(item.data.id, captureDevicePose(item));
  }
  restoreFocusDevicePoses();

  viewMode = 'focus';
  selectedId = id;
  showroomRoot.visible = true;
  focusStage.visible = true;
  for (const group of counterGroups) group.visible = false;
  for (const item of devices.values()) {
    item.model.visible = item === entry;
    item.model.userData.kiosk.visible = false;
    item.infoPanel.visible = false;
    item.element.style.display = item === entry ? '' : 'none';
  }
  entry.model.visible = true;
  entry.model.position.set(0, FOCUS_STAGE_TOP, FOCUS_STAGE_Z);
  entry.model.rotation.set(0, Math.PI / 4, 0);
  selectionRing.visible = false;
  outlinePass.selectedObjects = [entry.model];
  focusCameraMin.copy(roomMin).addScalar(0.35);
  focusCameraMin.y = 0.3;
  focusCameraMax.copy(roomMax).addScalar(-0.35);

  entry.model.updateWorldMatrix(true, true);
  const bounds = new THREE.Box3();
  entry.model.traverseVisible(object => {
    if (object.isMesh) bounds.expandByObject(object);
  });
  const sphere = bounds.getBoundingSphere(new THREE.Sphere());
  const size = bounds.getSize(new THREE.Vector3());
  const radius = Math.max(0.1, sphere.radius);
  const target = sphere.center.clone();
  target.y -= Math.min(0.06, radius * 0.04);
  const stageScale = Math.max(0.7, Math.min(1.7, radius * 0.65));
  focusStage.userData.platform.scale.set(stageScale, 1, stageScale);
  const verticalFov = THREE.MathUtils.degToRad(camera.fov);
  const horizontalFov = 2 * Math.atan(Math.tan(verticalFov / 2) * camera.aspect);
  const verticalDistance = size.y / (2 * Math.tan(verticalFov / 2) * 0.5);
  const horizontalDistance = Math.hypot(size.x, size.z) / (2 * Math.tan(horizontalFov / 2) * 0.62);
  const distance = Math.max(verticalDistance, horizontalDistance, radius * 1.05) * 1.15;
  const position = target.clone().add(new THREE.Vector3(1.15, 0.55, 3.2).normalize().multiplyScalar(distance));

  controls.minPolarAngle = 0;
  controls.maxPolarAngle = Math.PI;
  controls.minAzimuthAngle = -Infinity;
  controls.maxAzimuthAngle = Infinity;
  controls.minDistance = Math.max(0.3, radius * 1.05);
  controls.maxDistance = distance;
  controls.enablePan = false;
  controls.autoRotate = false;
  controls.autoRotateSpeed = 0.55;
  focusPauseUntil = performance.now() + 900;
  cameraTween = null;
  camera.position.copy(position);
  controls.target.copy(target);
  constrainCamera();
  controls.update();
  renderer.shadowMap.needsUpdate = true;
  renderer.render(scene, camera);
  setNavigationHint(true);
  setViewName(`${entry.data.id} · ${entry.data.name}`);
  post('device-focus-mode', { enabled: true, id });
}

function returnOverview() {
  locatedDeviceId = null;
  pinnedDeviceId = null;
  hoveredDeviceId = null;
  focusStage.visible = false;
  if (focusSnapshot) {
    const snapshot = focusSnapshot;
    showroomRoot.visible = snapshot.showroomVisible;
    focusStage.visible = snapshot.stageVisible;
    restoreFocusDevicePoses();
    counterGroups.forEach((group, index) => { group.visible = snapshot.counters[index] ?? true; });
    for (const [id, entry] of devices) {
      entry.model.visible = true;
      entry.model.userData.kiosk.visible = true;
      entry.infoPanel.visible = snapshot.infoPanels.get(id) ?? true;
      entry.element.style.display = '';
    }
    scene.background = snapshot.background;
    controls.minPolarAngle = snapshot.minPolarAngle;
    controls.maxPolarAngle = snapshot.maxPolarAngle;
    controls.minAzimuthAngle = snapshot.minAzimuthAngle;
    controls.maxAzimuthAngle = snapshot.maxAzimuthAngle;
    controls.minDistance = snapshot.minDistance;
    controls.maxDistance = snapshot.maxDistance;
    controls.enablePan = snapshot.panEnabled;
    selectionRing.visible = snapshot.selectionVisible;
    outlinePass.selectedObjects = selectedId && devices.has(selectedId) ? [devices.get(selectedId).model] : [];
    controls.autoRotate = false;
    startCameraTween(snapshot.position, snapshot.target, 850);
    renderer.shadowMap.needsUpdate = true;
    focusSnapshot = null;
  } else {
    updateOverviewForRoom();
    startCameraTween(overviewPosition, overviewTarget, 1250);
  }
  viewMode = 'overview';
  controls.autoRotate = false;
  if (reflection) reflection.getRenderTarget().setSize(512, 512);
  applyRenderQuality();
  setNavigationHint(false);
  updateLabelVisibility();
  setViewName('数字展厅总览');
  document.getElementById('focus-badge').classList.add('hidden');
  post('device-focus-mode', { enabled: false });
}

function startCameraTween(position, target, duration) {
  cameraTween = {
    fromPosition: camera.position.clone(),
    fromTarget: controls.target.clone(),
    position: position.clone(),
    target: target.clone(),
    started: performance.now(),
    duration
  };
}

function showToast(message) {
  const toast = document.getElementById('toast');
  toast.textContent = message;
  toast.classList.remove('hidden');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => toast.classList.add('hidden'), 2200);
}

function getQualityPixelRatio() {
  return quality === 2 ? Math.min(window.devicePixelRatio || 1, 1.35) : quality === 1 ? Math.min(window.devicePixelRatio || 1, 1.15) : 1;
}

function applyRenderQuality() {
  const ratio = getQualityPixelRatio();
  renderer.setPixelRatio(ratio);
  composer.setPixelRatio(ratio);
  ssaoPass.enabled = quality === 2;
  bloomPass.enabled = quality > 0;
  outlinePass.enabled = quality > 0;
  renderer.shadowMap.enabled = quality > 0;
  renderer.shadowMap.needsUpdate = true;
  scene.traverse(object => { if (object.userData.isFloorReflection) object.visible = quality > 0 && !locatedDeviceId; });
  resize();
}

function applyQuality(next, automatic = false) {
  quality = next;
  applyRenderQuality();
  document.getElementById('quality-label').textContent = ['LQ', 'MQ', 'HQ'][quality];
  if (automatic) {
    showToast('已自动调整画质以保持流畅');
    post('scene-event', { message: '3D 引擎已自动调整渲染画质' });
  } else {
    showToast({ 2: '高画质 · 半分辨率 SSAO / 2×MSAA / HDR', 1: '均衡画质 · 柔和阴影 / HDR', 0: '流畅画质 · 原生渲染' }[quality]);
  }
}

function resize() {
  if (!renderer || !camera) return;
  const width = Math.max(1, viewport.clientWidth);
  const height = Math.max(1, viewport.clientHeight);
  const baseFov = 56;
  const fitAspect = 16 / 9;
  camera.aspect = width / height;
  camera.fov = camera.aspect < fitAspect
    ? THREE.MathUtils.radToDeg(2 * Math.atan(Math.tan(THREE.MathUtils.degToRad(baseFov / 2)) * fitAspect / camera.aspect))
    : baseFov;
  camera.updateProjectionMatrix();
  renderer.setSize(width, height);
  composer?.setSize(width, height);
  ssaoPass?.setSize(Math.max(1, Math.floor(width * 0.5)), Math.max(1, Math.floor(height * 0.5)));
  labelRenderer?.setSize(width, height);
}

function raycastDevice(event) {
  const rect = renderer.domElement.getBoundingClientRect();
  pointer.x = ((event.clientX - rect.left) / rect.width) * 2 - 1;
  pointer.y = -((event.clientY - rect.top) / rect.height) * 2 + 1;
  raycaster.setFromCamera(pointer, camera);
  const hits = raycaster.intersectObjects([...devices.values()].filter(entry => entry.model.visible).map(entry => entry.model), true);
  for (const hit of hits) {
    let object = hit.object;
    while (object) {
      if (object.userData.deviceId) return object.userData.deviceId;
      object = object.parent;
    }
  }
  return null;
}

function getTouchDistance() {
  const [first, second] = [...touchPointers.values()];
  return first && second ? Math.hypot(first.x - second.x, first.y - second.y) : 0;
}

function beginPinch() {
  if (touchPointers.size < 2) return;
  pinchState = { distance: getTouchDistance() };
  controls.enabled = false;
  pointerStart = null;
  pinnedDeviceId = null;
  setHoveredDevice(null);
}

function updatePinch() {
  if (!pinchState || touchPointers.size < 2) return;
  const distance = getTouchDistance();
  if (distance < 8 || pinchState.distance < 8) return;
  zoomCamera(pinchState.distance / distance);
  pinchState.distance = distance;
}

function endTouch(event) {
  const wasPinching = pinchState !== null;
  touchPointers.delete(event.pointerId);
  if (touchPointers.size < 2) {
    pinchState = null;
    controls.enabled = true;
  }
  return wasPinching;
}

function bindInteraction() {
  const canvas = renderer.domElement;
  canvas.addEventListener('pointerdown', event => {
    if (event.pointerType === 'touch') {
      touchPointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
      canvas.setPointerCapture(event.pointerId);
      if (touchPointers.size === 2) {
        event.preventDefault();
        beginPinch();
        return;
      }
      if (pinchState) {
        event.preventDefault();
        return;
      }
    }
    pointerStart = { x: event.clientX, y: event.clientY, button: event.button };
  }, { passive: false });
  canvas.addEventListener('pointerup', event => {
    if (event.pointerType === 'touch') {
      const wasPinching = endTouch(event);
      if (canvas.hasPointerCapture(event.pointerId)) canvas.releasePointerCapture(event.pointerId);
      if (wasPinching) {
        pointerStart = null;
        event.preventDefault();
        return;
      }
    }
    if (!pointerStart || pointerStart.button !== 0) return;
    const distance = Math.hypot(event.clientX - pointerStart.x, event.clientY - pointerStart.y);
    pointerStart = null;
    if (distance < 5) {
      const id = raycastDevice(event);
      if (id) {
        selectDevice(id, false, true);
        locateDevice(id);
      }
    }
  });
  canvas.addEventListener('pointercancel', event => {
    endTouch(event);
    if (canvas.hasPointerCapture(event.pointerId)) canvas.releasePointerCapture(event.pointerId);
    pointerStart = null;
  });
  canvas.addEventListener('dblclick', event => {
    const id = raycastDevice(event);
    if (id) {
      selectDevice(id, false, true);
      locateDevice(id);
    }
  });
  canvas.addEventListener('contextmenu', event => {
    event.preventDefault();
    const id = raycastDevice(event);
    if (id) post('device-context-request', { id });
  });
  canvas.addEventListener('pointermove', event => {
    if (event.pointerType === 'touch') {
      if (touchPointers.has(event.pointerId)) touchPointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
      if (pinchState) {
        event.preventDefault();
        updatePinch();
        return;
      }
    }
    if (performance.now() - lastHover < 70) return;
    lastHover = performance.now();
    const id = raycastDevice(event);
    canvas.style.cursor = id ? 'pointer' : 'grab';
    setHoveredDevice(id);
  }, { passive: false });
  canvas.addEventListener('pointerleave', () => setHoveredDevice(null));
  canvas.addEventListener('wheel', event => {
    event.preventDefault();
    cameraTween = null;
    zoomCamera(Math.exp(event.deltaY * 0.001));
  }, { passive: false, capture: true });

  document.querySelectorAll('[data-action]').forEach(button => {
    button.addEventListener('click', () => {
      switch (button.dataset.action) {
        case 'overview':
        case 'reset': returnOverview(); break;
        case 'quality': applyQuality((quality + 2) % 3); break;
        case 'fullscreen': post('toggle-scene-fullscreen'); break;
      }
    });
  });
  window.addEventListener('keydown', event => {
    if (event.ctrlKey || event.altKey || event.metaKey) return;
    if (event.shiftKey && event.key === 'F10') {
      event.preventDefault();
      if (selectedId) post('device-context-request', { id: selectedId });
      return;
    }
    switch (event.key.toLowerCase()) {
      case 'escape':
        event.preventDefault();
        post('exit-scene-fullscreen');
        break;
      case 'home':
      case 'r': returnOverview(); break;
      case '+':
      case '=': zoomCamera(0.88); break;
      case '-': zoomCamera(1.12); break;
    }
  });
}

function zoomCamera(factor) {
  cameraTween = null;
  stableDistance = THREE.MathUtils.clamp(stableDistance * factor, controls.minDistance, controls.maxDistance);
  const direction = camera.position.clone().sub(controls.target).normalize();
  camera.position.copy(controls.target).addScaledVector(direction, stableDistance);
  constrainCamera();
}

function updateTelemetry(records) {
  for (const data of records) {
    const entry = devices.get(data.id);
    if (!entry) continue;
    Object.assign(entry.data, data);
    entry.element.classList.toggle('offline', !data.online);
    const connectionText = data.online ? (data.hasFreshData ? '已连接' : '已连接·等待数据') : '未连接';
    entry.status.textContent = connectionText;
    entry.element.title = `${entry.data.name} · ${connectionText}`;
    entry.value.textContent = liveParameterText(data.primaryValue, entry.data.primaryUnit, data.online, data.hasFreshData);
    for (const texture of entry.model.userData.screens) texture.userData.update({ ...entry.data, ...data });
    entry.infoPanel?.userData.infoTexture?.userData.update({ ...entry.data, ...data });
    for (const material of entry.model.userData.statusMaterials) {
      material.color.set(data.online ? 0x44d9c5 : 0xf19a5c);
      material.emissive.set(data.online ? 0x2abcab : 0xd36c31);
    }
  }
}

function updateSceneFullscreenButton(enabled) {
  const button = document.querySelector('[data-action="fullscreen"]');
  if (!button) return;
  document.body.classList.toggle('scene-fullscreen', enabled);
  if (controls) {
    controls.minPolarAngle = 0;
    controls.maxPolarAngle = Math.PI;
    controls.minAzimuthAngle = -Infinity;
    controls.maxAzimuthAngle = Infinity;
  }
  const title = enabled ? '退出全屏' : '全屏显示';
  button.title = title;
  button.setAttribute('aria-label', title);
  button.setAttribute('aria-pressed', String(enabled));
}

function handleHostMessage(message) {
  const type = message.type;
  const data = message.data || {};
  switch (type) {
    case 'initialize':
      initializeDevices(data).catch(error => window.reportSceneError(`场景首帧初始化失败：${error?.message || error}`));
      break;
    case 'add-device':
      if (!initialized) break;
      addDeviceToScene(data);
      rebuildContinuousCounters();
      selectDevice(data.id, false, false);
      renderer.shadowMap.needsUpdate = true;
      break;
    case 'select-device': selectDevice(data.id, false); break;
    case 'locate-device': locateDevice(data.id); break;
    case 'remove-device': removeDeviceFromScene(data.id); break;
    case 'update-device': updateDeviceInScene(data); break;
    case 'overview': returnOverview(); break;
    case 'scene-fullscreen-changed': updateSceneFullscreenButton(data.enabled); break;
    case 'scene-panels-width-changed':
      if (!document.body.classList.contains('scene-fullscreen')) {
        document.documentElement.style.setProperty('--panel-left', `${Number(data.left) || 240}px`);
        document.documentElement.style.setProperty('--panel-right', `${Number(data.right) || 244}px`);
      }
      break;
    case 'ai-inspection-focus': inspectionLayer?.setFocus(data); break;
    case 'telemetry': updateTelemetry(data); break;
  }
}

function animate() {
  const delta = Math.min(clock.getDelta(), 0.05);
  const elapsed = clock.elapsedTime;
  if (cameraTween) {
    const raw = Math.min(1, (performance.now() - cameraTween.started) / cameraTween.duration);
    const t = raw < 0.5 ? 4 * raw * raw * raw : 1 - Math.pow(-2 * raw + 2, 3) / 2;
    camera.position.lerpVectors(cameraTween.fromPosition, cameraTween.position, t);
    controls.target.lerpVectors(cameraTween.fromTarget, cameraTween.target, t);
    stableDistance = camera.position.distanceTo(controls.target);
    if (raw >= 1) cameraTween = null;
  }
  controls.autoRotate = false;
  controls.update(delta);
  constrainCamera();
  updateWallTransparency(delta);
  inspectionLayer?.update(delta, elapsed, camera, locatedDeviceId ? 'focus' : viewMode);
  if (selectionRing?.visible) {
    selectionRing.rotation.y = elapsed * 0.28;
    selectionRing.children[0].material.opacity = 0.68 + Math.sin(elapsed * 2.4) * 0.16;
  }
  for (const entry of devices.values()) {
    if (viewMode === 'focus' && entry.data.id !== selectedId) continue;
    for (const spinner of entry.model.userData.spinners) spinner.rotation.y = elapsed * 18;
  }
  if (viewMode === 'focus' || quality === 0) renderer.render(scene, camera); else composer.render();
  if (frameCounter % 2 === 0 && (viewMode !== 'focus' || hoveredDeviceId || pinnedDeviceId)) labelRenderer.render(scene, camera);

  hudFrame++;
  if (hudFrame >= 6) {
    hudFrame = 0;
    const offset = camera.position.clone().sub(controls.target);
    const yaw = Math.atan2(offset.x, offset.z) * 180 / Math.PI;
    const angle = Math.round(-yaw);
    if (angle !== lastCompassAngle) {
      document.getElementById('compass-arrow').style.transform = `rotate(${angle}deg)`;
      lastCompassAngle = angle;
    }
    const zoom = `${Math.round(overviewPosition.distanceTo(overviewTarget) / offset.length() * 100)}%`;
    if (zoom !== lastZoomText) {
      document.getElementById('zoom-label').textContent = zoom;
      lastZoomText = zoom;
    }
  }
  frameCounter++;
  fpsElapsed += delta;
  if (fpsElapsed >= 2) {
    const fps = Math.round(frameCounter / fpsElapsed);
    document.getElementById('fps-label').textContent = `${fps} FPS`;
    lowFpsSamples = fps < 45 ? lowFpsSamples + 1 : 0;
    if (lowFpsSamples >= 3 && quality > 0 && viewMode !== 'focus') {
      applyQuality(quality - 1, true);
      lowFpsSamples = 0;
    }
    frameCounter = 0;
    fpsElapsed = 0;
  }
}

try {
  initializeRenderer();
  if (window.chrome?.webview) {
    window.chrome.webview.addEventListener('message', event => {
      const message = typeof event.data === 'string' ? JSON.parse(event.data) : event.data;
      handleHostMessage(message);
    });
  } else {
    document.getElementById('scene-error').classList.remove('hidden');
    document.getElementById('scene-error-text').textContent = '请在 WPF WebView2 宿主中运行此场景。';
  }
} catch (error) {
  window.reportSceneError(error?.stack || error?.message || error);
  throw error;
}
