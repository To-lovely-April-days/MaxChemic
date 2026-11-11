import * as THREE from 'three';

function canvasTexture(canvas, color = true) {
  const texture = new THREE.CanvasTexture(canvas);
  if (color) texture.colorSpace = THREE.SRGBColorSpace;
  texture.anisotropy = 8;
  return texture;
}

function seededRandom(seed) {
  let value = seed >>> 0;
  return () => {
    value = (value * 1664525 + 1013904223) >>> 0;
    return value / 4294967296;
  };
}

export function createFloorTextures() {
  const size = 1024;
  const colorCanvas = document.createElement('canvas');
  const roughCanvas = document.createElement('canvas');
  colorCanvas.width = colorCanvas.height = roughCanvas.width = roughCanvas.height = size;
  const ctx = colorCanvas.getContext('2d');
  const rough = roughCanvas.getContext('2d');
  const random = seededRandom(7107);
  const base = ctx.createLinearGradient(0, 0, size, size);
  base.addColorStop(0, '#697477');
  base.addColorStop(.45, '#505b5f');
  base.addColorStop(1, '#778184');
  ctx.fillStyle = base;
  ctx.fillRect(0, 0, size, size);
  rough.fillStyle = '#525252';
  rough.fillRect(0, 0, size, size);
  for (let i = 0; i < 26000; i++) {
    const shade = Math.floor(70 + random() * 80);
    ctx.fillStyle = `rgba(${shade},${shade + 5},${shade + 7},${random() * .08})`;
    ctx.fillRect(random() * size, random() * size, random() * 3 + .5, random() * 3 + .5);
  }
  for (let i = 0; i < 34; i++) {
    const x = random() * size;
    const y = random() * size;
    const drift = (random() - .5) * 700;
    ctx.beginPath();
    ctx.moveTo(x - 350, y - 200);
    ctx.bezierCurveTo(x - 120, y + drift * .2, x + 180, y - drift * .35, x + 420, y + 280);
    ctx.strokeStyle = `rgba(218,226,225,${.025 + random() * .1})`;
    ctx.lineWidth = .6 + random() * 3.2;
    ctx.stroke();
    ctx.strokeStyle = `rgba(27,38,42,${.03 + random() * .08})`;
    ctx.lineWidth = .4 + random() * 1.2;
    ctx.stroke();
  }
  ctx.strokeStyle = 'rgba(26,34,37,.68)';
  ctx.lineWidth = 5;
  ctx.strokeRect(1, 1, size - 2, size - 2);
  ctx.strokeStyle = 'rgba(219,227,226,.2)';
  ctx.lineWidth = 1;
  ctx.strokeRect(5, 5, size - 10, size - 10);
  const color = canvasTexture(colorCanvas);
  const roughness = canvasTexture(roughCanvas, false);
  color.wrapS = color.wrapT = roughness.wrapS = roughness.wrapT = THREE.RepeatWrapping;
  color.repeat.set(5, 7);
  roughness.repeat.copy(color.repeat);
  return { color, roughness };
}

export function createBrushedTexture() {
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = 256;
  const ctx = canvas.getContext('2d');
  const random = seededRandom(93);
  ctx.fillStyle = '#aeb9bd';
  ctx.fillRect(0, 0, 256, 256);
  for (let y = 0; y < 256; y++) {
    const shade = Math.floor(130 + random() * 80);
    ctx.fillStyle = `rgba(${shade},${shade + 5},${shade + 8},.2)`;
    ctx.fillRect(0, y, 256, 1);
  }
  const texture = canvasTexture(canvas);
  texture.wrapS = texture.wrapT = THREE.RepeatWrapping;
  texture.repeat.set(2, 3);
  return texture;
}

export function createSignTexture(title, subtitle = '', options = {}) {
  const canvas = document.createElement('canvas');
  canvas.width = 1024;
  canvas.height = 256;
  const ctx = canvas.getContext('2d');
  ctx.fillStyle = options.background || '#13262e';
  ctx.fillRect(0, 0, 1024, 256);
  ctx.fillStyle = options.accent || '#55d9c7';
  ctx.fillRect(45, 42, 7, 167);
  ctx.font = `600 ${options.fontSize || 61}px "Microsoft YaHei UI", sans-serif`;
  ctx.fillStyle = options.color || '#e0edef';
  ctx.textAlign = 'left';
  ctx.fillText(title, 88, subtitle ? 123 : 151);
  if (subtitle) {
    ctx.font = '27px Consolas, monospace';
    ctx.fillStyle = '#7797a1';
    ctx.fillText(subtitle, 90, 181);
  }
  return canvasTexture(canvas);
}

export function createDeviceScreen(device) {
  const canvas = document.createElement('canvas');
  canvas.width = 512;
  canvas.height = 256;
  const ctx = canvas.getContext('2d');
  const texture = canvasTexture(canvas);
  let current = device;

  function draw() {
    const gradient = ctx.createLinearGradient(0, 0, 512, 256);
    gradient.addColorStop(0, '#071b22');
    gradient.addColorStop(1, '#0b2c33');
    ctx.fillStyle = gradient;
    ctx.fillRect(0, 0, 512, 256);
    ctx.strokeStyle = '#1b4d54';
    ctx.lineWidth = 2;
    for (let x = 22; x < 512; x += 44) {
      ctx.beginPath(); ctx.moveTo(x, 82); ctx.lineTo(x, 205); ctx.stroke();
    }
    ctx.fillStyle = current.online ? '#50e0ca' : '#f4a06b';
    ctx.font = '600 22px Consolas, monospace';
    ctx.fillText(`NEXUS  /  ${current.id}`, 24, 38);
    ctx.font = '17px Consolas, monospace';
    ctx.fillStyle = '#6c9ba4';
    ctx.fillText(current.online ? '●  SYSTEM READY' : '○  STANDBY', 308, 38);
    ctx.fillStyle = '#d7fcf5';
    ctx.font = '300 72px Consolas, monospace';
    ctx.fillText(current.primaryValue ?? '--', 24, 143);
    ctx.font = '24px Consolas, monospace';
    ctx.fillStyle = '#65bdb4';
    ctx.fillText(current.primaryUnit || '', 360, 140);
    ctx.strokeStyle = '#49b9ab';
    ctx.lineWidth = 3;
    ctx.beginPath();
    for (let i = 0; i < 22; i++) {
      const x = 25 + i * 21;
      const y = 211 - Math.sin(i * .62 + device.id.charCodeAt(2)) * 15 - Math.sin(i * 1.71) * 7;
      if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
    }
    ctx.stroke();
    texture.needsUpdate = true;
  }

  texture.userData.update = data => { current = { ...current, ...data }; draw(); };
  draw();
  return texture;
}

export function createVentTexture() {
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = 512;
  const ctx = canvas.getContext('2d');
  ctx.fillStyle = '#b5bec0';
  ctx.fillRect(0, 0, 512, 512);
  for (let row = 0; row < 15; row++) {
    for (let col = 0; col < 9; col++) {
      const x = 27 + col * 52 + (row % 2) * 13;
      const y = 18 + row * 33;
      ctx.fillStyle = '#263238';
      ctx.beginPath(); ctx.roundRect(x, y, 12, 24, 6); ctx.fill();
      ctx.fillStyle = 'rgba(255,255,255,.38)';
      ctx.fillRect(x + 13, y + 3, 2, 18);
    }
  }
  return canvasTexture(canvas);
}

export function createGalaxyTexture() {
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = 1024;
  const ctx = canvas.getContext('2d');
  const random = seededRandom(2026);
  const base = ctx.createLinearGradient(0, 0, 1024, 1024);
  base.addColorStop(0, '#071a4d');
  base.addColorStop(.48, '#104c96');
  base.addColorStop(1, '#250c55');
  ctx.fillStyle = base;
  ctx.fillRect(0, 0, 1024, 1024);
  for (let i = 0; i < 70; i++) {
    const x = random() * 1024;
    const y = random() * 1024;
    const radius = 60 + random() * 210;
    const cloud = ctx.createRadialGradient(x, y, 0, x, y, radius);
    const blue = random() > .3;
    cloud.addColorStop(0, blue ? 'rgba(45,157,255,.18)' : 'rgba(213,79,199,.12)');
    cloud.addColorStop(1, 'rgba(0,0,0,0)');
    ctx.fillStyle = cloud;
    ctx.fillRect(x - radius, y - radius, radius * 2, radius * 2);
  }
  for (let i = 0; i < 1900; i++) {
    const x = random() * 1024;
    const y = random() * 1024;
    const radius = random() < .96 ? random() * 1.1 + .2 : random() * 2.2 + 1;
    ctx.fillStyle = `rgba(225,244,255,${.25 + random() * .75})`;
    ctx.beginPath(); ctx.arc(x, y, radius, 0, Math.PI * 2); ctx.fill();
    if (radius > 1.8) {
      ctx.strokeStyle = 'rgba(165,220,255,.45)';
      ctx.lineWidth = .7;
      ctx.beginPath(); ctx.moveTo(x - 6, y); ctx.lineTo(x + 6, y); ctx.moveTo(x, y - 6); ctx.lineTo(x, y + 6); ctx.stroke();
    }
  }
  return canvasTexture(canvas);
}

export function createTimelineTexture(entries) {
  const canvas = document.createElement('canvas');
  canvas.width = 512;
  canvas.height = 1024;
  const ctx = canvas.getContext('2d');
  const texture = canvasTexture(canvas);
  ctx.fillStyle = '#72767a';
  ctx.fillRect(0, 0, 512, 1024);
  ctx.fillStyle = 'rgba(255,255,255,.045)';
  for (let y = 0; y < 1024; y += 8) ctx.fillRect(0, y, 512, 1);

  entries.forEach((entry, index) => {
    const y = 95 + index * 475;
    ctx.textAlign = 'left';
    ctx.fillStyle = '#f2f1ec';
    ctx.font = '600 68px "Segoe UI", sans-serif';
    ctx.fillText(entry.year, 42, y + 55);
    ctx.font = '600 25px "Microsoft YaHei UI", sans-serif';
    ctx.fillText(entry.title, 45, y + 106);
    ctx.font = '15px Consolas, monospace';
    ctx.fillStyle = '#d3d7d7';
    ctx.fillText(entry.subtitle, 46, y + 139);
    ctx.strokeStyle = 'rgba(237,240,238,.55)';
    ctx.lineWidth = 1;
    ctx.beginPath(); ctx.moveTo(46, y + 172); ctx.lineTo(270, y + 172); ctx.stroke();
    ctx.fillStyle = '#c4cccd';
    ctx.font = '13px "Microsoft YaHei UI", sans-serif';
    ctx.fillText('创新驱动 · 智造未来', 46, y + 204);
    ctx.strokeStyle = 'rgba(232,238,237,.65)';
    ctx.lineWidth = 3;
    ctx.beginPath(); ctx.arc(374, y + 83, 72, 0, Math.PI * 2); ctx.stroke();
    ctx.fillStyle = '#545e63';
    ctx.beginPath(); ctx.arc(374, y + 83, 68, 0, Math.PI * 2); ctx.fill();
    if (entry.image) {
      const image = new Image();
      image.onload = () => {
        ctx.save();
        ctx.beginPath(); ctx.arc(374, y + 83, 67, 0, Math.PI * 2); ctx.clip();
        ctx.filter = 'grayscale(.45) contrast(.94)';
        const scale = Math.max(136 / image.width, 136 / image.height);
        const width = image.width * scale;
        const height = image.height * scale;
        ctx.drawImage(image, 374 - width / 2, y + 83 - height / 2, width, height);
        ctx.restore();
        texture.needsUpdate = true;
      };
      image.src = entry.image;
    }
  });
  return texture;
}

export function createDeviceInfoTexture(device) {
  const canvas = document.createElement('canvas');
  canvas.width = 640;
  canvas.height = 840;
  const ctx = canvas.getContext('2d');
  const texture = canvasTexture(canvas);
  let current = device;
  let image = null;
  let imageLoaded = false;

  function draw() {
    ctx.fillStyle = '#e9efef';
    ctx.fillRect(0, 0, 640, 840);
    ctx.fillStyle = '#b52238';
    ctx.fillRect(0, 0, 640, 16);
    ctx.fillStyle = '#263c46';
    ctx.font = '600 37px "Microsoft YaHei UI", sans-serif';
    ctx.fillText(current.name, 38, 78);
    ctx.fillStyle = '#66818b';
    ctx.font = '19px Consolas, monospace';
    ctx.fillText(current.englishName, 40, 114);
    ctx.fillStyle = '#b52238';
    ctx.font = '600 21px Consolas, monospace';
    ctx.fillText(`${current.id} / SMART EQUIPMENT`, 40, 154);
    ctx.fillStyle = '#f7f8f5';
    ctx.fillRect(32, 180, 576, 388);
    ctx.strokeStyle = '#c7d3d4';
    ctx.strokeRect(32, 180, 576, 388);
    ctx.fillStyle = '#2f4852';
    ctx.font = '22px "Microsoft YaHei UI", sans-serif';
    ctx.fillText('设备数字化展示', 40, 626);
    ctx.fillStyle = '#708890';
    ctx.font = '18px "Microsoft YaHei UI", sans-serif';
    ctx.fillText('实时状态监测  ·  远程数据联动', 40, 669);
    ctx.fillText('PRECISION  /  INTELLIGENCE  /  CONNECTIVITY', 40, 710);
    ctx.fillStyle = current.online ? '#169a88' : '#d17640';
    ctx.fillRect(40, 752, 8, 34);
    ctx.font = '600 21px "Microsoft YaHei UI", sans-serif';
    ctx.fillText(current.online ? '智能设备 · 已连接' : '智能设备 · 未连接', 64, 778);
    if (imageLoaded) {
      const scale = Math.min(520 / image.width, 340 / image.height);
      const width = image.width * scale;
      const height = image.height * scale;
      ctx.drawImage(image, 320 - width / 2, 374 - height / 2, width, height);
    }
    texture.needsUpdate = true;
  }

  texture.userData.update = data => {
    current = { ...current, ...data };
    draw();
  };
  draw();

  if (current.image) {
    image = new Image();
    image.onload = () => {
      imageLoaded = true;
      draw();
    };
    image.src = current.image;
  }
  return texture;
}

export function createLogoTexture() {
  const canvas = document.createElement('canvas');
  canvas.width = 1200;
  canvas.height = 320;
  const ctx = canvas.getContext('2d');
  ctx.clearRect(0, 0, 1200, 320);
  ctx.textAlign = 'center';
  ctx.fillStyle = '#c5223c';
  ctx.font = '800 156px "Arial", sans-serif';
  ctx.fillText('HOTREE', 600, 185);
  ctx.font = '500 29px Consolas, monospace';
  ctx.letterSpacing = '8px';
  ctx.fillText('INTELLIGENT TECHNOLOGY', 600, 252);
  return canvasTexture(canvas);
}

export function createMicroSurfaceTexture(kind = 'metal') {
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = 512;
  const ctx = canvas.getContext('2d');
  const seed = kind === 'metal' ? 93 : kind === 'paint' ? 381 : 727;
  const random = seededRandom(seed);
  const base = kind === 'metal' ? 154 : kind === 'paint' ? 188 : 166;
  ctx.fillStyle = `rgb(${base},${base},${base})`;
  ctx.fillRect(0, 0, 512, 512);
  for (let i = 0; i < 19000; i++) {
    const shade = Math.floor(base + (random() - .5) * (kind === 'metal' ? 42 : 24));
    const alpha = .08 + random() * .16;
    ctx.fillStyle = `rgba(${shade},${shade},${shade},${alpha})`;
    const x = random() * 512;
    const y = random() * 512;
    if (kind === 'metal') ctx.fillRect(x, y, 8 + random() * 45, .35 + random() * .8);
    else ctx.fillRect(x, y, .5 + random() * 1.4, .5 + random() * 1.4);
  }
  if (kind === 'metal') {
    for (let y = 0; y < 512; y += 3) {
      ctx.fillStyle = `rgba(230,230,230,${.015 + random() * .035})`;
      ctx.fillRect(0, y, 512, 1);
    }
  }
  const texture = canvasTexture(canvas, false);
  texture.wrapS = texture.wrapT = THREE.RepeatWrapping;
  texture.repeat.set(4, 4);
  return texture;
}

export function createContactShadowTexture() {
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = 256;
  const ctx = canvas.getContext('2d');
  const gradient = ctx.createRadialGradient(128, 128, 8, 128, 128, 126);
  gradient.addColorStop(0, 'rgba(0,0,0,0.62)');
  gradient.addColorStop(.42, 'rgba(0,0,0,0.3)');
  gradient.addColorStop(.74, 'rgba(0,0,0,0.09)');
  gradient.addColorStop(1, 'rgba(0,0,0,0)');
  ctx.fillStyle = gradient;
  ctx.fillRect(0, 0, 256, 256);
  return canvasTexture(canvas, false);
}
