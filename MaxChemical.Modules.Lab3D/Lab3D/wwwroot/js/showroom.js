import * as THREE from 'three';
import { RoundedBoxGeometry } from 'three/addons/geometries/RoundedBoxGeometry.js';
import { Reflector } from 'three/addons/objects/Reflector.js';
import { createDeviceInfoTexture, createFloorTextures, createGalaxyTexture, createLogoTexture, createSignTexture, createTimelineTexture } from './textures.js';
import { SHOWROOM_SCALE } from './layout.js';
import { createContactShadow } from './models.js';

function mesh(parent, geometry, material, position = [0, 0, 0], rotation = [0, 0, 0]) {
  const object = new THREE.Mesh(geometry, material);
  object.position.set(...position);
  object.rotation.set(...rotation);
  object.castShadow = true;
  object.receiveShadow = true;
  parent.add(object);
  return object;
}

function box(parent, size, position, material, radius = 0) {
  const safeRadius = Math.min(radius, Math.min(...size) * 0.45);
  const geometry = radius > 0 ? new RoundedBoxGeometry(size[0], size[1], size[2], 3, safeRadius) : new THREE.BoxGeometry(...size);
  return mesh(parent, geometry, material, position);
}

function cylinder(parent, radius, height, position, material, segments = 24, rotation = [0, 0, 0]) {
  return mesh(parent, new THREE.CylinderGeometry(radius, radius, height, segments), material, position, rotation);
}

function rod(parent, start, end, radius, material) {
  const a = new THREE.Vector3(...start);
  const b = new THREE.Vector3(...end);
  const direction = b.clone().sub(a);
  const object = cylinder(parent, radius, direction.length(), a.clone().add(b).multiplyScalar(0.5).toArray(), material, 16);
  object.quaternion.setFromUnitVectors(new THREE.Vector3(0, 1, 0), direction.normalize());
  return object;
}

function sign(parent, title, subtitle, width, height, position, rotation, options) {
  const texture = createSignTexture(title, subtitle, options);
  const material = new THREE.MeshStandardMaterial({ map: texture, emissiveMap: texture, emissive: 0xffffff, emissiveIntensity: 0.16, roughness: 0.62 });
  const object = mesh(parent, new THREE.PlaneGeometry(width, height), material, position, rotation);
  object.castShadow = false;
  return object;
}

export function buildDeviceInfoPanel(scene, device, materials) {
  const warmGlow = new THREE.MeshStandardMaterial({ color: 0xffdb94, emissive: 0xffb641, emissiveIntensity: 2.2, roughness: 0.32 });
  const group = new THREE.Group();
  group.scale.set(SHOWROOM_SCALE, 1, SHOWROOM_SCALE);
  group.userData.fadeWall = 'right';
  scene.add(group);
  box(group, [0.12, 3.22, 3.02], [8.34, 3.05, 0], materials.darkSteel, 0.035);
  box(group, [0.055, 0.055, 2.96], [8.24, 4.64, 0], warmGlow);
  box(group, [0.055, 0.055, 2.96], [8.24, 1.46, 0], warmGlow);
  box(group, [0.055, 3.16, 0.055], [8.24, 3.05, -1.48], warmGlow);
  box(group, [0.055, 3.16, 0.055], [8.24, 3.05, 1.48], warmGlow);
  const texture = createDeviceInfoTexture(device);
  group.userData.infoTexture = texture;
  const material = new THREE.MeshStandardMaterial({ map: texture, emissiveMap: texture, emissive: 0xffffff, emissiveIntensity: 0.16, roughness: 0.45 });
  const panel = mesh(group, new THREE.PlaneGeometry(2.72, 2.92), material, [8.25, 3.05, 0], [0, -Math.PI / 2, 0]);
  panel.castShadow = false;
  box(group, [1.18, 0.12, 3.0], [7.83, 1.05, 0], materials.porcelain, 0.035);
  box(group, [1.05, 0.92, 2.75], [7.9, 0.55, 0], materials.white, 0.025);
  box(group, [0.04, 0.055, 2.7], [7.35, 0.2, 0], warmGlow);
  return group;
}

function buildTimelineWall(environment, materials) {
  const milestones = [
    ['2015', '梦想启航', 'COMPANY FOUNDED'], ['2016', '技术积淀', 'TECHNOLOGY'],
    ['2017', '产品体系', 'PRODUCT SYSTEM'], ['2018', '创新突破', 'INNOVATION'],
    ['2019', '品质升级', 'QUALITY UPGRADE'], ['2020', '智能互联', 'CONNECTIVITY'],
    ['2021', '规模发展', 'DEVELOPMENT'], ['2022', '数字转型', 'DIGITALIZATION'],
    ['2023', '智造未来', 'INTELLIGENCE'], ['2024', '全球视野', 'GLOBAL VISION'],
    ['2025', '生态协同', 'ECOSYSTEM'], ['2026', '再启新程', 'NEW JOURNEY']
  ];
  const images = ['highLowtemperature.png', 'MicroReactor.png', 'magneticstirrer.png', 'ChilLer.png', 'ClimateChamber.png', 'PenMeter_BLE.png'];
  const warmGlow = new THREE.MeshStandardMaterial({ color: 0xffe4b4, emissive: 0xffc56c, emissiveIntensity: 2.4, roughness: 0.3 });
  for (let i = 0; i < 6; i++) {
    const z = -8.5 + i * 3.3;
    const entries = [0, 1].map(offset => {
      const item = milestones[i * 2 + offset];
      return { year: item[0], title: item[1], subtitle: item[2], image: `assets/devices/${images[i]}` };
    });
    box(environment, [0.09, 4.25, 3.08], [-8.34, 2.75, z], materials.wallDark, 0.025);
    const texture = createTimelineTexture(entries);
    const material = new THREE.MeshStandardMaterial({ map: texture, emissiveMap: texture, emissive: 0xffffff, emissiveIntensity: 0.08, roughness: 0.82 });
    const panel = mesh(environment, new THREE.PlaneGeometry(2.96, 4.12), material, [-8.275, 2.75, z], [0, Math.PI / 2, 0]);
    panel.castShadow = false;
    box(environment, [0.055, 4.4, 0.055], [-8.18, 2.8, z - 1.58], warmGlow);
    box(environment, [0.055, 4.4, 0.055], [-8.18, 2.8, z + 1.58], warmGlow);
  }
  sign(environment, '发展历程', 'DEVELOPMENT HISTORY', 3.1, 0.65, [-8.15, 5.2, 7.95], [0, Math.PI / 2, 0], { background: '#74787b', color: '#c51f39', accent: '#c51f39', fontSize: 66 });
}

function buildBackWall(environment, materials) {
  const warmGlow = new THREE.MeshStandardMaterial({ color: 0xffd98c, emissive: 0xffb536, emissiveIntensity: 2.2, roughness: 0.35 });
  const shelfMaterial = new THREE.MeshPhysicalMaterial({ color: 0xd8d9d4, roughness: 0.36, clearcoat: 0.25 });
  for (let level = 0; level < 3; level++) {
    const y = 1.15 + level * 1.08;
    const width = 13.8 - level * 1.2;
    box(environment, [width, 0.16, 0.82], [0, y, -11.25], shelfMaterial, 0.025);
    box(environment, [width - 0.12, 0.055, 0.055], [0, y - 0.11, -10.81], warmGlow);
    box(environment, [0.14, 0.86, 0.65], [-width / 2 + 0.1, y + 0.47, -11.25], shelfMaterial, 0.025);
    box(environment, [0.14, 0.86, 0.65], [width / 2 - 0.1, y + 0.47, -11.25], shelfMaterial, 0.025);
    for (const x of [-5.4, -3.7, 3.7, 5.4]) {
      if (Math.abs(x) > width / 2 - 0.6) continue;
      if ((level + Math.round(x)) % 2 === 0) {
        cylinder(environment, 0.19, 0.48, [x, y + 0.32, -11.1], materials.steel);
        cylinder(environment, 0.23, 0.06, [x, y + 0.08, -11.1], materials.darkSteel);
      } else {
        box(environment, [0.48, 0.55, 0.35], [x, y + 0.35, -11.1], materials.white, 0.04);
        box(environment, [0.3, 0.12, 0.025], [x, y + 0.38, -10.91], materials.black, 0.015);
      }
    }
  }
  const logo = createLogoTexture();
  const logoMaterial = new THREE.MeshStandardMaterial({ map: logo, transparent: true, emissiveMap: logo, emissive: 0xffffff, emissiveIntensity: 0.18, roughness: 0.5 });
  const brand = mesh(environment, new THREE.PlaneGeometry(5.5, 1.5), logoMaterial, [3.1, 4.7, -11.16]);
  brand.castShadow = false;
  box(environment, [15.2, 0.07, 0.07], [0, 5.85, -11.12], warmGlow);
}

function buildReceptionDesk(environment, materials) {
  const group = new THREE.Group();
  group.position.set(0.4, 0, -8.9);
  environment.add(group);
  createContactShadow(group, 5.6, 2.6, 0.18, 0.008);
  const warmGlow = new THREE.MeshStandardMaterial({ color: 0xffd88a, emissive: 0xffb946, emissiveIntensity: 2.2, roughness: 0.32 });
  box(group, [4.9, 1.05, 1.25], [0, 0.58, 0], materials.white, 0.06);
  box(group, [4.72, 0.13, 1.18], [0, 0.1, 0], materials.darkSteel, 0.03);
  box(group, [5.05, 0.14, 1.45], [0, 1.16, 0], materials.bench, 0.045);
  box(group, [4.72, 0.045, 0.045], [0, 0.88, 0.645], warmGlow);
  box(group, [0.045, 0.045, 1.18], [2.44, 0.88, 0], warmGlow);
  sign(group, 'HOTREE', 'WELCOME', 1.9, 0.43, [0, 0.57, 0.638], [0, 0, 0], { background: '#e1e5e4', color: '#bd2339', accent: '#bd2339', fontSize: 82 });
  box(group, [1.02, 0.96, 2.05], [2.05, 0.55, -0.83], materials.white, 0.05);
  box(group, [1.16, 0.14, 2.15], [2.05, 1.16, -0.83], materials.bench, 0.04);
  box(group, [0.04, 0.045, 1.92], [2.58, 0.86, -0.83], warmGlow);

  cylinder(group, 0.18, 0.045, [-0.95, 1.25, -0.12], materials.steel);
  rod(group, [-0.95, 1.25, -0.12], [-0.95, 1.48, -0.12], 0.035, materials.steel);
  box(group, [0.98, 0.62, 0.07], [-0.95, 1.69, -0.1], materials.black, 0.035);
  sign(group, 'HOTREE', 'DIGITAL SHOWROOM', 0.86, 0.49, [-0.95, 1.69, -0.055], [0, 0, 0], { background: '#10252d', color: '#e7c77e', accent: '#b5223c', fontSize: 62 });
  box(group, [0.74, 0.035, 0.25], [-0.95, 1.255, 0.38], materials.darkSteel, 0.018);
  box(group, [0.12, 0.045, 0.19], [-0.38, 1.255, 0.36], materials.black, 0.025);
}

function buildCeiling(environment, materials) {
  const ceilingMaterial = new THREE.MeshStandardMaterial({ color: 0xbec4c5, roughness: 0.82 });
  box(environment, [17, 0.16, 24], [0, 6.24, 0], ceilingMaterial);
  const blueGlow = new THREE.MeshStandardMaterial({ color: 0x70d9ff, emissive: 0x239dff, emissiveIntensity: 3.1, roughness: 0.25 });
  const warmGlow = new THREE.MeshStandardMaterial({ color: 0xffe0a1, emissive: 0xffc565, emissiveIntensity: 2.1, roughness: 0.3 });
  const slatMaterial = new THREE.MeshStandardMaterial({ color: 0x4e5a60, metalness: 0.68, roughness: 0.36 });
  for (let z = -11.5; z <= 11.5; z += 0.43) {
    if (z > -7 && z < 5) {
      box(environment, [4.15, 0.13, 0.11], [-6.42, 6.03, z], slatMaterial);
      box(environment, [4.15, 0.13, 0.11], [6.42, 6.03, z], slatMaterial);
    } else {
      box(environment, [17, 0.13, 0.11], [0, 6.03, z], slatMaterial);
    }
  }
  const galaxy = createGalaxyTexture();
  const skyMaterial = new THREE.MeshStandardMaterial({ map: galaxy, emissiveMap: galaxy, emissive: 0xffffff, emissiveIntensity: 1.7, roughness: 0.65 });
  const sky = mesh(environment, new THREE.PlaneGeometry(8.55, 12), skyMaterial, [0, 5.96, -1], [Math.PI / 2, 0, 0]);
  sky.castShadow = false;
  box(environment, [0.28, 0.28, 12.4], [-4.4, 5.94, -1], materials.white, 0.035);
  box(environment, [0.28, 0.28, 12.4], [4.4, 5.94, -1], materials.white, 0.035);
  box(environment, [8.8, 0.28, 0.28], [0, 5.94, -7.1], materials.white, 0.035);
  box(environment, [8.8, 0.28, 0.28], [0, 5.94, 5.1], materials.white, 0.035);
  box(environment, [0.055, 0.055, 12.05], [-4.18, 5.82, -1], blueGlow);
  box(environment, [0.055, 0.055, 12.05], [4.18, 5.82, -1], blueGlow);
  box(environment, [8.4, 0.055, 0.055], [0, 5.82, -6.94], blueGlow);
  box(environment, [8.4, 0.055, 0.055], [0, 5.82, 4.94], blueGlow);
  for (const x of [-8.18, 8.18]) box(environment, [0.055, 0.065, 23.4], [x, 5.72, 0], x < 0 ? blueGlow : warmGlow);
  box(environment, [16.4, 0.065, 0.055], [0, 5.72, -11.45], warmGlow);
}

function buildTrackLights(environment, materials, wallGroups) {
  for (const side of [-1, 1]) {
    const parent = side < 0 ? wallGroups.left : wallGroups.right;
    const x = side * 8.03;
    rod(parent, [x, 5.35, -10.4], [x, 5.35, 10.2], 0.035, materials.black);
    for (let i = 0; i < 6; i++) {
      const z = -8.5 + i * 3.3;
      rod(parent, [x, 5.35, z], [x - side * 0.18, 5.08, z + 0.08], 0.085, materials.black);
      cylinder(parent, 0.095, 0.22, [x - side * 0.2, 5.02, z + 0.08], materials.black, 20, [0, 0, side * 0.55]);
      if (i % 2 === 0) {
        const light = new THREE.SpotLight(0xffd5a0, 24, 7.5, 0.48, 0.82, 1.6);
        light.position.set(x - side * 0.35, 5.02, z);
        light.target.position.set(side * 8.3, 2.7, z);
        parent.add(light, light.target);
      }
    }
  }
}

export function buildContinuousCounters(scene, materials, ranges) {
  const groups = [];
  const warmGlow = new THREE.MeshStandardMaterial({ color: 0xffd98c, emissive: 0xffb83e, emissiveIntensity: 2.1, roughness: 0.35 });
  for (const side of [-1, 1]) {
    const range = side < 0 ? ranges?.left : ranges?.right;
    if (!range) continue;
    const length = Math.max(1, range.maxZ - range.minZ);
    const center = (range.minZ + range.maxZ) / 2;
    const doorCount = Math.max(1, Math.round(length / 2.3));
    const doorDepth = Math.min(2.1, length / doorCount - 0.12);
    const group = new THREE.Group();
    group.position.set(side * 3.4, 0, center);
    scene.add(group);
    createContactShadow(group, 2.8, length + 0.5, 0.16, 0.008);
    box(group, [2.35, 0.7, length], [0, 0.43, 0], materials.white, 0.045);
    box(group, [2.25, 0.08, length - 0.15], [0, 0.04, 0], materials.darkSteel, 0.025);
    box(group, [2.5, 0.12, length + 0.15], [0, 0.82, 0], materials.bench, 0.04);
    box(group, [0.035, 0.035, length], [-1.19, 0.74, 0], warmGlow);
    box(group, [0.035, 0.035, length], [1.19, 0.74, 0], warmGlow);
    box(group, [2.35, 0.035, 0.035], [0, 0.74, -length / 2], warmGlow);
    box(group, [2.35, 0.035, 0.035], [0, 0.74, length / 2], warmGlow);
    sign(group, 'HOTREE', 'INTELLIGENT EQUIPMENT', 1.75, 0.32, [0, 0.42, length / 2 + 0.015], [0, 0, 0], { background: '#e1e5e4', color: '#bd2339', accent: '#bd2339', fontSize: 76 });
    for (let i = 0; i < doorCount; i++) {
      const z = -length / 2 + (i + 0.5) * length / doorCount;
      box(group, [0.025, 0.52, doorDepth], [-side * 1.185, 0.42, z], materials.porcelain, 0.015);
      box(group, [0.045, 0.04, 0.48], [-side * 1.215, 0.56, z], materials.steel, 0.015);
    }
    groups.push(group);
  }
  return groups;
}

export function buildEnvironment(scene, materials) {
  const environment = new THREE.Group();
  environment.name = 'HOTREE-Showroom';
  environment.scale.set(SHOWROOM_SCALE, 1, SHOWROOM_SCALE);
  scene.add(environment);
  const wallGroups = { left: new THREE.Group(), right: new THREE.Group(), back: new THREE.Group() };
  for (const [side, group] of Object.entries(wallGroups)) {
    group.name = `${side}-wall`;
    group.userData.fadeWall = side;
    environment.add(group);
  }
  const wallMaterial = new THREE.MeshStandardMaterial({ color: 0x85898c, roughness: 0.78 });
  const backMaterial = new THREE.MeshStandardMaterial({ color: 0x999b9b, roughness: 0.74 });
  const floorTextures = createFloorTextures();
  const reflector = new Reflector(new THREE.PlaneGeometry(17, 24), { clipBias: 0.003, textureWidth: 768, textureHeight: 768, color: 0x697276 });
  reflector.rotation.x = -Math.PI / 2;
  reflector.position.y = -0.018;
  reflector.userData.isFloorReflection = true;
  environment.add(reflector);
  const floorMaterial = new THREE.MeshPhysicalMaterial({ map: floorTextures.color, roughnessMap: floorTextures.roughness, color: 0xa1a8aa, metalness: 0.22, roughness: 0.2, clearcoat: 0.85, clearcoatRoughness: 0.16, transparent: true, opacity: 0.79, depthWrite: false });
  const floor = mesh(environment, new THREE.PlaneGeometry(17, 24), floorMaterial, [0, 0, 0], [-Math.PI / 2, 0, 0]);
  floor.castShadow = false;
  floor.renderOrder = 1;
  box(environment, [0.07, 0.025, 23.6], [2.92, 0.048, 0], materials.steel);
  box(wallGroups.back, [17, 6.3, 0.26], [0, 3.05, -11.8], backMaterial);
  box(wallGroups.left, [0.26, 6.3, 24], [-8.5, 3.05, 0], wallMaterial);
  box(wallGroups.right, [0.26, 6.3, 24], [8.5, 3.05, 0], wallMaterial);
  box(environment, [17, 6.3, 0.26], [0, 3.05, 11.8], wallMaterial);
  const entranceMaterial = new THREE.MeshPhysicalMaterial({ color: 0x718187, metalness: 0.32, roughness: 0.3, clearcoat: 0.35 });
  box(environment, [3.5, 4.25, 0.08], [0, 2.2, 11.61], entranceMaterial, 0.035);
  box(environment, [0.12, 4.45, 0.14], [-1.82, 2.3, 11.55], materials.steel, 0.025);
  box(environment, [0.12, 4.45, 0.14], [1.82, 2.3, 11.55], materials.steel, 0.025);
  box(environment, [3.75, 0.12, 0.14], [0, 4.5, 11.55], materials.steel, 0.025);
  box(environment, [0.055, 4.1, 0.06], [0, 2.2, 11.53], materials.steel);
  box(environment, [16.8, 0.2, 0.09], [0, 0.13, 11.61], materials.black);
  sign(environment, 'HOTREE', 'INTELLIGENT TECHNOLOGY', 3.6, 0.72, [0, 5.15, 11.62], [0, Math.PI, 0], { background: '#85898c', color: '#c51f39', accent: '#c51f39', fontSize: 68 });
  box(wallGroups.back, [16.8, 0.2, 0.09], [0, 0.13, -11.61], materials.black);
  box(wallGroups.left, [0.09, 0.2, 23.8], [-8.31, 0.13, 0], materials.black);
  box(wallGroups.right, [0.09, 0.2, 23.8], [8.31, 0.13, 0], materials.black);
  buildTimelineWall(wallGroups.left, materials);
  buildBackWall(wallGroups.back, materials);
  buildReceptionDesk(environment, materials);
  buildCeiling(environment, materials);
  buildTrackLights(environment, materials, wallGroups);
  return environment;
}
