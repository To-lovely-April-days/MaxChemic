import * as THREE from 'three';

function createPhotoShape(image, width, height) {
  const scale = Math.min(1, 768 / image.width);
  const scanWidth = Math.max(1, Math.round(image.width * scale));
  const scanHeight = Math.max(1, Math.round(image.height * scale));
  const canvas = document.createElement('canvas');
  canvas.width = scanWidth;
  canvas.height = scanHeight;
  const context = canvas.getContext('2d', { willReadFrequently: true });
  context.drawImage(image, 0, 0, scanWidth, scanHeight);
  const pixels = context.getImageData(0, 0, scanWidth, scanHeight).data;
  let minY = scanHeight;
  let maxY = -1;
  for (let y = 0; y < scanHeight; y++) {
    for (let x = 0; x < scanWidth; x++) {
      if (pixels[(y * scanWidth + x) * 4 + 3] > 32) {
        minY = Math.min(minY, y);
        maxY = y;
        break;
      }
    }
  }
  if (maxY < 0) throw new Error('图片中没有可识别的设备轮廓，请使用透明背景的设备图片。');

  const rowCount = Math.min(220, maxY - minY + 1);
  const rows = [];
  for (let i = 0; i < rowCount; i++) {
    const y = rowCount === 1 ? minY : Math.round(minY + (maxY - minY) * i / (rowCount - 1));
    let left = 0;
    let right = scanWidth - 1;
    while (left < scanWidth && pixels[(y * scanWidth + left) * 4 + 3] <= 32) left++;
    while (right > left && pixels[(y * scanWidth + right) * 4 + 3] <= 32) right--;
    const modelY = height * (1 - y / scanHeight);
    if (right <= left) {
      if (rows.length > 0) rows.push({ ...rows[rows.length - 1], y: modelY });
      continue;
    }
    rows.push({
      left: (left / scanWidth - 0.5) * width,
      right: (right / scanWidth - 0.5) * width,
      y: modelY
    });
  }
  if (rows.length < 2) throw new Error('设备图片的有效轮廓不足，无法生成实体模型。');

  const shape = new THREE.Shape();
  shape.moveTo(rows[0].right, rows[0].y);
  for (const row of rows) shape.lineTo(row.right, row.y);
  for (let i = rows.length - 1; i >= 0; i--) shape.lineTo(rows[i].left, rows[i].y);
  shape.closePath();
  return shape;
}

function createUvGenerator(width, height) {
  const point = (vertices, index) => new THREE.Vector2(
    vertices[index * 3] / width + 0.5,
    vertices[index * 3 + 1] / height
  );
  return {
    generateTopUV: (geometry, vertices, a, b, c) => [point(vertices, a), point(vertices, b), point(vertices, c)],
    generateSideWallUV: () => [
      new THREE.Vector2(0, 0), new THREE.Vector2(1, 0),
      new THREE.Vector2(1, 1), new THREE.Vector2(0, 1)
    ]
  };
}

export function buildPhotoDevice(group, device, materials, onReady) {
  const aspect = Math.max(0.1, device.imageAspect || 1);
  let height = THREE.MathUtils.clamp(device.modelHeight || 2, 0.35, 4);
  let width = height * aspect;
  if (width > 2.8) {
    width = 2.8;
    height = width / aspect;
  }
  width = Math.max(0.4, width);
  const depth = THREE.MathUtils.clamp(width * 0.22, 0.08, 0.42);
  group.userData.height = height;
  group.userData.plinthWidth = Math.max(1.1, width + 0.4);
  group.userData.plinthDepth = Math.max(1.2, depth + 0.7);

  new THREE.TextureLoader().load(device.image, texture => {
    try {
      texture.colorSpace = THREE.SRGBColorSpace;
      texture.anisotropy = 16;
      const shape = createPhotoShape(texture.image, width, height);
      const edge = Math.min(0.018, width * 0.02);
      const geometry = new THREE.ExtrudeGeometry(shape, {
        depth,
        steps: 1,
        bevelEnabled: true,
        bevelThickness: edge,
        bevelSize: edge,
        bevelSegments: 2,
        curveSegments: 4,
        UVGenerator: createUvGenerator(width, height)
      });
      geometry.translate(0, 0, -depth / 2);
      const photoMaterial = new THREE.MeshPhysicalMaterial({
        map: texture,
        roughness: 0.38,
        metalness: 0.03,
        clearcoat: 0.16,
        clearcoatRoughness: 0.32,
        envMapIntensity: 0.65,
        alphaTest: 0.2,
        alphaToCoverage: true,
        transparent: false
      });
      const model = new THREE.Mesh(geometry, [photoMaterial, materials.steel]);
      model.castShadow = true;
      model.receiveShadow = true;
      model.userData.deviceId = device.id;
      group.add(model);
      onReady?.();
    } catch (error) {
      onReady?.(false);
      window.reportSceneError(`设备 ${device.id} 的模型生成失败：${error?.message || error}`);
    }
  }, undefined, () => {
    onReady?.(false);
    window.reportSceneError(`设备 ${device.id} 的图片纹理加载失败，请检查设备库目录和图片文件。`);
  });
}
