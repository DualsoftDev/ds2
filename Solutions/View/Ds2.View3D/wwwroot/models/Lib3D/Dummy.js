/**
 * Dummy - 미등록 장치 (Cartoon Style)
 * 용도: DeviceType을 찾을 수 없을 때 표시
 * @version 2.0 - Cartoon/Toon rendering
 */

class Dummy {
  static createToonGradient(THREE) {
    const colors = new Uint8Array(3);
    colors[0] = 50;
    colors[1] = 180;
    colors[2] = 255;
    const gradientMap = new THREE.DataTexture(colors, colors.length, 1, THREE.LuminanceFormat);
    gradientMap.needsUpdate = true;
    return gradientMap;
  }

  static create(THREE, options = {}) {
    const {
      targetHeight = 2.0,
      deviceType = 'Unknown'
    } = options;

    const gradientMap = this.createToonGradient(THREE);
    const dummy = new THREE.Group();
    dummy.userData.deviceType = deviceType;
    dummy.userData.isDummy = true;

    // Main box with cartoon style
    const box = new THREE.Mesh(
      new THREE.BoxGeometry(1.0, 1.5, 1.0),
      new THREE.MeshToonMaterial({
        color: 0x94a3b8,
        transparent: true,
        opacity: 0.8,
        gradientMap: gradientMap
      })
    );
    box.position.y = 0.75;
    box.castShadow = true;
    dummy.add(box);

    // Bold outline
    const outline = new THREE.Mesh(
      new THREE.BoxGeometry(1.0, 1.5, 1.0),
      new THREE.MeshBasicMaterial({
        color: 0x000000,
        side: THREE.BackSide
      })
    );
    outline.scale.multiplyScalar(1.15);
    outline.position.y = 0.75;
    dummy.add(outline);

    // "?" label with cartoon style
    const canvas = document.createElement('canvas');
    canvas.width = 256;
    canvas.height = 256;
    const ctx = canvas.getContext('2d');

    ctx.fillStyle = '#f1f5f9';
    ctx.fillRect(0, 0, 256, 256);

    ctx.strokeStyle = '#000000';
    ctx.lineWidth = 12;
    ctx.font = 'bold 180px Arial';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    ctx.strokeText('?', 128, 128);

    ctx.fillStyle = '#ef4444';
    ctx.fillText('?', 128, 128);

    const texture = new THREE.CanvasTexture(canvas);
    const label = new THREE.Mesh(
      new THREE.PlaneGeometry(0.8, 0.8),
      new THREE.MeshBasicMaterial({ map: texture, transparent: true })
    );
    label.position.set(0, 0.75, 0.51);
    dummy.add(label);

    // State indicator with bright colors
    const indicator = new THREE.Mesh(
      new THREE.BoxGeometry(1.2, 1.7, 1.2),
      new THREE.MeshToonMaterial({
        color: 0xfb7185,
        transparent: true,
        opacity: 0.25,
        emissive: 0xfb7185,
        emissiveIntensity: 0.6,
        gradientMap: gradientMap
      })
    );
    indicator.position.y = 0.75;
    indicator.userData.isStateIndicator = true;
    dummy.add(indicator);

    const bbox = new THREE.Box3().setFromObject(dummy);
    const scale = targetHeight / (bbox.max.y - bbox.min.y);
    dummy.scale.set(scale, scale, scale);

    return dummy;
  }

  static updateState(dummy, state) {
    const colors = {
      'R': 0xfb7185,
      'G': 0xfde047,
      'F': 0x60a5fa,
      'H': 0xa78bfa
    };
    const color = colors[state] || 0xfb7185;
    dummy.traverse(child => {
      if (child.userData.isStateIndicator && child.material) {
        child.material.color.setHex(color);
        child.material.emissive.setHex(color);
      }
    });
  }
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = Dummy;
}

if (typeof window !== 'undefined') {
  window.Dummy = Dummy;
}
