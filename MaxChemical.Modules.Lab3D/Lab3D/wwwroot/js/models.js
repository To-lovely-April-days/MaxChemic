import * as THREE from 'three';
import { RoundedBoxGeometry } from 'three/addons/geometries/RoundedBoxGeometry.js';
import { createBrushedTexture, createContactShadowTexture, createDeviceScreen, createMicroSurfaceTexture, createSignTexture, createVentTexture } from './textures.js';
import { buildPhotoDevice } from './photo-model.js';

const contactShadowTexture = createContactShadowTexture();

export function createMaterials() {
  const brushed = createBrushedTexture();
  const vent = createVentTexture();
  const metalMicro = createMicroSurfaceTexture('metal');
  const paintMicro = createMicroSurfaceTexture('paint');
  const blackMicro = createMicroSurfaceTexture('black');
  return {
    white: new THREE.MeshPhysicalMaterial({ color: 0xdde3e3, metalness: 0.12, roughness: 0.34, clearcoat: 0.22, clearcoatRoughness: 0.32, roughnessMap: paintMicro, bumpMap: paintMicro, bumpScale: 0.003 }),
    porcelain: new THREE.MeshPhysicalMaterial({ color: 0xf1f4f2, metalness: 0.02, roughness: 0.2, clearcoat: 0.5 }),
    steel: new THREE.MeshPhysicalMaterial({ color: 0xb9c4c7, map: brushed, metalness: 0.88, roughness: 0.29, anisotropy: 0.65, anisotropyRotation: Math.PI / 2, roughnessMap: metalMicro, bumpMap: metalMicro, bumpScale: 0.004, envMapIntensity: 1.15 }),
    darkSteel: new THREE.MeshStandardMaterial({ color: 0x35444a, metalness: 0.82, roughness: 0.3, roughnessMap: metalMicro, bumpMap: metalMicro, bumpScale: 0.003 }),
    black: new THREE.MeshPhysicalMaterial({ color: 0x111a1f, metalness: 0.3, roughness: 0.28, clearcoat: 0.28, roughnessMap: blackMicro, bumpMap: blackMicro, bumpScale: 0.003 }),
    rubber: new THREE.MeshStandardMaterial({ color: 0x151b1e, roughness: 0.85 }),
    red: new THREE.MeshPhysicalMaterial({ color: 0x8e1730, metalness: 0.32, roughness: 0.26, clearcoat: 0.52, clearcoatRoughness: 0.25, roughnessMap: paintMicro, bumpMap: paintMicro, bumpScale: 0.002 }),
    blue: new THREE.MeshPhysicalMaterial({ color: 0x135a77, metalness: 0.15, roughness: 0.24, clearcoat: 0.5 }),
    glass: new THREE.MeshPhysicalMaterial({ color: 0xe8fbff, metalness: 0, roughness: 0.045, transmission: 0.97, thickness: 0.08, ior: 1.46, attenuationColor: 0xd6f2f2, attenuationDistance: 3, envMapIntensity: 1.1, specularIntensity: 1, transparent: true, opacity: 1 }),
    darkGlass: new THREE.MeshPhysicalMaterial({ color: 0x14262b, metalness: 0.2, roughness: 0.1, transmission: 0.25, thickness: 0.12, envMapIntensity: 1.05, transparent: true, opacity: 0.94 }),
    liquid: new THREE.MeshPhysicalMaterial({ color: 0x28b9d1, roughness: 0.06, transmission: 0.62, thickness: 0.5, attenuationColor: 0x65d8e8, attenuationDistance: 1.8, envMapIntensity: 1.05, transparent: true, opacity: 0.82, ior: 1.33 }),
    vent: new THREE.MeshStandardMaterial({ color: 0xc2c9ca, map: vent, metalness: 0.55, roughness: 0.42, roughnessMap: metalMicro, bumpMap: metalMicro, bumpScale: 0.004 }),
    bench: new THREE.MeshPhysicalMaterial({ color: 0x34464d, metalness: 0.34, roughness: 0.3, clearcoat: 0.18, roughnessMap: paintMicro, bumpMap: paintMicro, bumpScale: 0.003 }),
    wall: new THREE.MeshStandardMaterial({ color: 0xb7c2c3, roughness: 0.88 }),
    wallDark: new THREE.MeshStandardMaterial({ color: 0x52636a, roughness: 0.72 }),
    cyan: new THREE.MeshStandardMaterial({ color: 0x44d9c5, emissive: 0x2abcab, emissiveIntensity: 1.5, roughness: 0.3 }),
    amber: new THREE.MeshStandardMaterial({ color: 0xf19a5c, emissive: 0xd36c31, emissiveIntensity: 1.2, roughness: 0.35 }),
    light: new THREE.MeshStandardMaterial({ color: 0xe7f5f4, emissive: 0xc8f0ef, emissiveIntensity: 2.8, roughness: 0.4 }),
    yellow: new THREE.MeshStandardMaterial({ color: 0xd4ad3d, roughness: 0.55 })
  };
}

function mesh(parent, geometry, material, position = [0, 0, 0], rotation = [0, 0, 0]) {
  const object = new THREE.Mesh(geometry, material);
  object.position.set(...position);
  object.rotation.set(...rotation);
  object.castShadow = true;
  object.receiveShadow = true;
  parent.add(object);
  return object;
}

export function createContactShadow(parent, width, depth, opacity = 0.2, y = 0.006) {
  const material = new THREE.MeshBasicMaterial({ map: contactShadowTexture, transparent: true, opacity, depthWrite: false });
  const shadow = mesh(parent, new THREE.PlaneGeometry(width, depth), material, [0, y, 0], [-Math.PI / 2, 0, 0]);
  shadow.castShadow = false;
  shadow.receiveShadow = false;
  shadow.renderOrder = 1;
  return shadow;
}

function box(parent, size, position, material, radius = 0) {
  const safeRadius = Math.min(radius, Math.min(...size) * 0.45);
  const geometry = radius > 0
    ? new RoundedBoxGeometry(size[0], size[1], size[2], 3, safeRadius)
    : new THREE.BoxGeometry(...size);
  return mesh(parent, geometry, material, position);
}

function cylinder(parent, radius, height, position, material, segments = 32, rotation = [0, 0, 0], radiusTop = radius) {
  return mesh(parent, new THREE.CylinderGeometry(radiusTop, radius, height, segments), material, position, rotation);
}

function sphere(parent, radius, position, material) {
  return mesh(parent, new THREE.SphereGeometry(radius, 20, 12), material, position);
}

function torus(parent, radius, tube, position, material, rotation = [Math.PI / 2, 0, 0]) {
  return mesh(parent, new THREE.TorusGeometry(radius, tube, 10, 40), material, position, rotation);
}

function rod(parent, start, end, radius, material) {
  const a = new THREE.Vector3(...start);
  const b = new THREE.Vector3(...end);
  const direction = b.clone().sub(a);
  const object = cylinder(parent, radius, direction.length(), a.clone().add(b).multiplyScalar(0.5).toArray(), material, 16);
  object.quaternion.setFromUnitVectors(new THREE.Vector3(0, 1, 0), direction.normalize());
  return object;
}

function tube(parent, points, radius, material) {
  const curve = new THREE.CatmullRomCurve3(points.map(point => new THREE.Vector3(...point)));
  return mesh(parent, new THREE.TubeGeometry(curve, 40, radius, 10, false), material);
}

function sign(parent, title, subtitle, width, height, position, rotation = [0, 0, 0], options = {}) {
  const texture = createSignTexture(title, subtitle, options);
  const material = new THREE.MeshStandardMaterial({ map: texture, emissiveMap: texture, emissive: 0xffffff, emissiveIntensity: 0.22, roughness: 0.5 });
  const object = mesh(parent, new THREE.PlaneGeometry(width, height), material, position, rotation);
  object.castShadow = false;
  return object;
}

function addFeet(group, width, depth, materials) {
  for (const x of [-width * 0.38, width * 0.38]) {
    for (const z of [-depth * 0.34, depth * 0.34]) {
      cylinder(group, 0.095, 0.13, [x, 0.085, z], materials.rubber, 20);
      cylinder(group, 0.065, 0.08, [x, 0.15, z], materials.steel, 20);
    }
  }
}

function addScrews(group, width, height, z, centerY, materials) {
  for (const x of [-width / 2, width / 2]) {
    for (const y of [centerY - height / 2, centerY + height / 2]) {
      cylinder(group, 0.022, 0.018, [x, y, z], materials.steel, 12, [Math.PI / 2, 0, 0]);
    }
  }
}

function addScreen(group, device, width, height, position, materials) {
  box(group, [width + 0.12, height + 0.12, 0.07], [position[0], position[1], position[2] - 0.035], materials.black, 0.045);
  const texture = createDeviceScreen(device);
  const material = new THREE.MeshStandardMaterial({ map: texture, emissiveMap: texture, emissive: 0xffffff, emissiveIntensity: 0.65, roughness: 0.26 });
  const screen = mesh(group, new THREE.PlaneGeometry(width, height), material, position);
  screen.castShadow = false;
  group.userData.screens.push(texture);
  return screen;
}

function addStatusLed(group, position, materials, online) {
  const material = (online ? materials.cyan : materials.amber).clone();
  const led = sphere(group, 0.035, position, material);
  led.castShadow = false;
  group.userData.statusMaterials.push(material);
}

function baseDevice(device) {
  const group = new THREE.Group();
  group.name = `${device.id}-${device.kind}`;
  group.userData.deviceId = device.id;
  group.userData.screens = [];
  group.userData.statusMaterials = [];
  group.userData.spinners = [];
  return group;
}

function buildThermal(group, device, materials, tall) {
  const width = tall ? 1.72 : 1.62;
  const height = tall ? 2.95 : 2.68;
  const depth = 1.38;
  addFeet(group, width, depth, materials);
  box(group, [width, height, depth], [0, height / 2 + 0.18, 0], materials.white, 0.075);
  box(group, [width - 0.04, 0.14, depth - 0.03], [0, height + 0.19, 0], materials.red, 0.045);
  box(group, [width - 0.16, 0.035, 0.035], [0, height * 0.48, depth / 2 + 0.012], materials.darkSteel);
  box(group, [width - 0.2, height * 0.39, 0.045], [0, height * 0.285, depth / 2 + 0.028], materials.vent, 0.025);
  box(group, [width - 0.23, height * 0.43, 0.055], [0, height * 0.72, depth / 2 + 0.035], materials.porcelain, 0.025);
  box(group, [1.05, 0.78, 0.07], [0.1, height * 0.77, depth / 2 + 0.08], materials.red, 0.085);
  addScreen(group, device, 0.55, 0.29, [0.12, height * 0.79, depth / 2 + 0.121], materials);
  box(group, [0.075, 0.56, 0.09], [-width * 0.39, height * 0.73, depth / 2 + 0.105], materials.steel, 0.03);
  for (const y of [height * 0.61, height * 0.88]) {
    box(group, [0.07, 0.13, 0.04], [-width * 0.47, y, depth / 2 + 0.08], materials.darkSteel, 0.012);
  }
  cylinder(group, 0.055, 0.035, [-0.23, height * 0.88, depth / 2 + 0.135], materials.red, 20, [Math.PI / 2, 0, 0]);
  addStatusLed(group, [-0.23, height * 0.68, depth / 2 + 0.14], materials, device.online);
  addScrews(group, width - 0.3, height * 0.34, depth / 2 + 0.065, height * 0.285, materials);
  sign(group, 'NEXUS', tall ? 'TEMPERATURE CONTROL' : 'PRECISION COOLING', 0.82, 0.2, [0.24, height * 0.96, depth / 2 + 0.075], [0, 0, 0], { background: '#e4e8e7', color: '#263d47', accent: '#a01d35', fontSize: 66 });
  const pipeCount = tall ? 3 : 2;
  for (let i = 0; i < pipeCount; i++) {
    const y = 1.3 + i * 0.38;
    rod(group, [width / 2, y, -0.25], [width / 2 + 0.33, y, -0.25], 0.055, materials.steel);
    cylinder(group, 0.1, 0.11, [width / 2 + 0.24, y, -0.25], materials.darkSteel, 20, [0, 0, Math.PI / 2]);
    torus(group, 0.12, 0.018, [width / 2 + 0.24, y + 0.13, -0.25], materials.red, [0, Math.PI / 2, 0]);
  }
  group.userData.height = height + 0.5;
}

function buildReactor(group, device, materials) {
  const width = 3.15;
  box(group, [width, 0.16, 1.25], [0, 0.24, 0], materials.darkSteel, 0.04);
  box(group, [width + 0.12, 0.1, 1.34], [0, 1.78, 0], materials.steel, 0.025);
  for (const x of [-1.48, 1.48]) {
    for (const z of [-0.53, 0.53]) rod(group, [x, 0.22, z], [x, 1.8, z], 0.055, materials.steel);
  }
  for (let i = 0; i < 5; i++) {
    const x = -1.12 + i * 0.56;
    box(group, [0.25, 1.18, 0.42], [x, 0.98, 0], materials.black, 0.025);
    box(group, [0.28, 0.06, 0.45], [x, 1.58, 0], materials.steel, 0.015);
    rod(group, [x, 1.62, 0], [x, 1.86, 0], 0.033, materials.steel);
    cylinder(group, 0.11, 0.12, [x, 1.22, 0.34], materials.steel, 24, [Math.PI / 2, 0, 0]);
    cylinder(group, 0.075, 0.14, [x, 1.22, 0.42], materials.black, 20, [Math.PI / 2, 0, 0]);
    tube(group, [[x, 1.86, 0], [x, 2.02, 0], [x + 0.22, 2.08, -0.2], [x + 0.22, 1.9, -0.47]], 0.022, materials.darkSteel);
    if (i < 4) rod(group, [x + 0.14, 0.48, 0.32], [x + 0.43, 0.48, 0.32], 0.025, materials.steel);
  }
  addScreen(group, device, 0.76, 0.3, [0, 0.37, 0.67], materials);
  addStatusLed(group, [0.62, 0.37, 0.69], materials, device.online);
  sign(group, 'MICRO REACTOR', '5 CHANNEL CONTINUOUS FLOW', 1.4, 0.26, [0, 1.77, 0.69], [0, 0, 0], { fontSize: 54 });
  group.userData.height = 2.35;
}

function buildStirrer(group, device, materials) {
  box(group, [1.72, 0.38, 1.52], [0, 0.3, 0], materials.black, 0.13);
  box(group, [1.6, 0.07, 1.4], [0, 0.51, 0], materials.steel, 0.04);
  cylinder(group, 0.61, 0.1, [-0.13, 0.6, 0], materials.porcelain, 48);
  cylinder(group, 0.48, 0.025, [-0.13, 0.66, 0], materials.black, 48);
  addScreen(group, device, 0.65, 0.2, [0.27, 0.31, 0.77], materials);
  for (const x of [-0.58, 0.59]) {
    cylinder(group, 0.13, 0.11, [x, 0.28, 0.78], materials.steel, 28, [Math.PI / 2, 0, 0]);
    cylinder(group, 0.09, 0.12, [x, 0.28, 0.82], materials.darkSteel, 28, [Math.PI / 2, 0, 0]);
  }
  rod(group, [0.62, 0.48, -0.48], [0.62, 2.65, -0.48], 0.045, materials.steel);
  cylinder(group, 0.11, 0.15, [0.62, 0.56, -0.48], materials.black, 24);
  rod(group, [0.62, 2.15, -0.48], [-0.2, 2.15, -0.15], 0.038, materials.steel);
  box(group, [0.22, 0.19, 0.25], [0.55, 2.15, -0.46], materials.black, 0.035);
  const points = [[0, 0], [.29, 0], [.34, .05], [.34, .25], [.27, .55], [.14, .9], [.12, 1.16], [.16, 1.2], [.16, 1.25], [.09, 1.27]].map(p => new THREE.Vector2(...p));
  const flask = mesh(group, new THREE.LatheGeometry(points, 48), materials.glass, [-0.13, 0.67, 0]);
  flask.castShadow = false;
  const liquidPoints = [[0, .02], [.27, .02], [.3, .1], [.3, .28], [.25, .48], [0, .48]].map(p => new THREE.Vector2(...p));
  const liquid = mesh(group, new THREE.LatheGeometry(liquidPoints, 40), materials.liquid, [-0.13, 0.67, 0]);
  liquid.castShadow = false;
  rod(group, [-0.13, 0.85, 0], [-0.13, 2.25, 0], 0.018, materials.steel);
  const stirBar = box(group, [0.22, 0.045, 0.055], [-0.13, 0.77, 0], materials.porcelain, 0.02);
  group.userData.spinners.push(stirBar);
  tube(group, [[0.6, 2.28, -0.48], [0.25, 2.55, -0.5], [-0.3, 2.4, -0.25], [-0.16, 1.75, 0]], 0.022, materials.rubber);
  addStatusLed(group, [0.69, 0.43, 0.74], materials, device.online);
  group.userData.height = 2.85;
}

function buildClimate(group, device, materials) {
  addFeet(group, 2.55, 1.85, materials);
  box(group, [2.62, 2.25, 1.9], [0, 1.3, 0], materials.white, 0.075);
  box(group, [2.45, 1.65, 0.08], [0, 1.51, 0.97], materials.porcelain, 0.045);
  box(group, [1.76, 1.03, 0.09], [0.04, 1.63, 1.035], materials.black, 0.075);
  box(group, [1.57, 0.85, 0.035], [0.04, 1.64, 1.086], materials.darkGlass, 0.055);
  for (const y of [1.45, 1.66, 1.87]) rod(group, [-0.62, y, 1.11], [0.69, y, 1.11], 0.012, materials.steel);
  box(group, [0.07, 0.63, 0.09], [-1.02, 1.57, 1.08], materials.steel, 0.025);
  for (const y of [0.98, 2.03]) box(group, [0.07, 0.2, 0.07], [-1.2, y, 1.02], materials.darkSteel, 0.015);
  addScreen(group, device, 0.7, 0.3, [0.22, 0.48, 0.985], materials);
  box(group, [1.95, 0.22, 0.04], [0, 0.22, 0.965], materials.vent, 0.02);
  sign(group, 'NEXUS', 'CLIMATE CONTROL', 0.73, 0.19, [0.53, 2.25, 0.974], [0, 0, 0], { background: '#e4e8e7', color: '#28444e', accent: '#278aaf', fontSize: 64 });
  addStatusLed(group, [0.87, 0.52, 1.0], materials, device.online);
  group.userData.height = 2.75;
}

function buildMeter(group, device, materials) {
  box(group, [1.1, 0.18, 0.95], [0, 0.18, 0], materials.darkSteel, 0.07);
  box(group, [0.68, 0.24, 0.58], [0, 0.35, 0], materials.black, 0.055);
  rod(group, [0, 0.3, -0.19], [0, 2.45, -0.19], 0.035, materials.steel);
  box(group, [0.55, 1.62, 0.4], [0, 1.27, 0.03], materials.white, 0.12);
  cylinder(group, 0.245, 0.67, [0, 2.22, 0.03], materials.glass, 40);
  cylinder(group, 0.25, 0.12, [0, 1.87, 0.03], materials.steel, 40);
  box(group, [0.34, 0.52, 0.035], [0, 0.96, 0.245], materials.black, 0.035);
  addScreen(group, device, 0.28, 0.43, [0, 0.97, 0.268], materials);
  for (let i = 0; i < 2; i++) cylinder(group, 0.07, 0.025, [0, 1.42 + i * 0.2, 0.248], materials.porcelain, 24, [Math.PI / 2, 0, 0]);
  sign(group, 'NEXUS', '', 0.37, 0.13, [0, 1.74, 0.245], [0, 0, 0], { background: '#e1e6e5', color: '#2479a4', accent: '#2479a4', fontSize: 64 });
  for (const x of [-0.12, 0.12]) cylinder(group, 0.025, 0.025, [x, 0.44, 0.31], materials.steel, 16, [Math.PI / 2, 0, 0]);
  addStatusLed(group, [0.36, 0.28, 0.46], materials, device.online);
  group.userData.height = 2.75;
}

function buildCustomDevice(group, device, materials, onReady) {
  buildPhotoDevice(group, device, materials, onReady);
}

const DEVICE_PROFILES = {
  Temperature: { scale: 0.62, plinthWidth: 2.5, plinthDepth: 2.0 },
  Chiller: { scale: 0.52, plinthWidth: 2.3, plinthDepth: 1.9 },
  Reactor: { scale: 0.42, plinthWidth: 2.4, plinthDepth: 1.7 },
  Stirrer: { scale: 0.34, plinthWidth: 1.7, plinthDepth: 1.4 },
  Climate: { scale: 0.38, plinthWidth: 1.9, plinthDepth: 1.6 },
  Meter: { scale: 0.12, plinthWidth: 1.1, plinthDepth: 1.0 },
  Custom: { scale: 1, plinthWidth: 2.2, plinthDepth: 1.8 }
};

function addDisplayAccessories(group, device, materials) {
  const height = device.position?.[1] ?? 0.88;
  const kiosk = new THREE.Group();
  kiosk.position.y = -height;
  group.userData.kiosk = kiosk;
  group.add(kiosk);
  box(kiosk, [0.82, 0.08, 0.7], [0, 0.055, 0], materials.steel, 0.055);
  const stand = box(kiosk, [0.24, 1.08, 0.38], [0, 0.61, -0.04], materials.steel, 0.045);
  stand.rotation.x = -0.18;
  box(kiosk, [0.32, 0.06, 0.42], [0, 0.24, 0.02], materials.darkSteel, 0.025);
  const screenGroup = new THREE.Group();
  screenGroup.position.set(0, 1.22, 0.06);
  screenGroup.rotation.x = -0.62;
  kiosk.add(screenGroup);
  box(screenGroup, [1.12, 0.7, 0.075], [0, 0, 0], materials.darkSteel, 0.045);
  box(screenGroup, [1.06, 0.64, 0.025], [0, 0, 0.046], materials.black, 0.025);
  const texture = createDeviceScreen(device);
  const screenMaterial = new THREE.MeshStandardMaterial({ map: texture, emissiveMap: texture, emissive: 0xffffff, emissiveIntensity: 0.72, roughness: 0.2 });
  const screen = mesh(screenGroup, new THREE.PlaneGeometry(0.98, 0.5), screenMaterial, [0, 0.04, 0.061]);
  screen.castShadow = false;
  group.userData.screens.push(texture);
  sphere(screenGroup, 0.018, [0, -0.29, 0.066], materials.cyan);
}

export function buildDevice(device, materials, onReady) {
  const group = baseDevice(device);
  switch (device.kind) {
    case 'Temperature': buildThermal(group, device, materials, true); break;
    case 'Chiller': buildThermal(group, device, materials, false); break;
    case 'Reactor': buildReactor(group, device, materials); break;
    case 'Stirrer': buildStirrer(group, device, materials); break;
    case 'Climate': buildClimate(group, device, materials); break;
    case 'Meter': buildMeter(group, device, materials); break;
    case 'Custom': buildCustomDevice(group, device, materials, onReady); break;
  }
  const profile = { ...DEVICE_PROFILES[device.kind] };
  if (device.kind === 'Custom') {
    profile.plinthWidth = group.userData.plinthWidth;
    profile.plinthDepth = group.userData.plinthDepth;
  } else if (device.libraryId && device.modelHeight > 0) {
    const sizeFactor = device.modelHeight / (group.userData.height * profile.scale);
    profile.scale *= sizeFactor;
    profile.plinthWidth *= sizeFactor;
    profile.plinthDepth *= sizeFactor;
    group.userData.plinthWidth = profile.plinthWidth;
    group.userData.plinthDepth = profile.plinthDepth;
  }
  const equipment = new THREE.Group();
  for (const child of [...group.children]) equipment.add(child);
  const shadowSize = device.kind === 'Custom' || device.libraryId ? [profile.plinthWidth, profile.plinthDepth] : device.kind === 'Reactor' ? [3.5, 1.7] : device.kind === 'Climate' ? [2.9, 2.1] : device.kind === 'Meter' ? [1.1, 1] : [2.2, 1.8];
  createContactShadow(equipment, ...shadowSize, 0.2);
  equipment.scale.setScalar(profile.scale);
  group.add(equipment);
  group.userData.height *= profile.scale;
  group.userData.ringScale = profile.plinthWidth / 3.2;
  addDisplayAccessories(group, device, materials);
  group.traverse(object => {
    if (object.isMesh) object.userData.deviceId = device.id;
  });
  return group;
}


