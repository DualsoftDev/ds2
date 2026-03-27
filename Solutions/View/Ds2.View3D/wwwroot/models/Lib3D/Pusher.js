/**
 * Pusher - 푸셔 (Cartoon Style)
 * 명령어: FWD (Forward), BWD (Backward)
 * 용도: 제품을 밀어내거나 당기는 동작
 * @version 2.0 - Cartoon/Toon rendering with animation
 */

class Pusher {
  static createToonGradient(THREE) {
    const colors = new Uint8Array(3);
    colors[0] = 50;
    colors[1] = 180;
    colors[2] = 255;
    const gradientMap = new THREE.DataTexture(colors, colors.length, 1, THREE.LuminanceFormat);
    gradientMap.needsUpdate = true;
    return gradientMap;
  }

  static createOutline(geometry, color = 0x000000, thickness = 1.10) {
    const outlineMaterial = new THREE.MeshBasicMaterial({
      color: color,
      side: THREE.BackSide
    });
    const outline = new THREE.Mesh(geometry, outlineMaterial);
    outline.scale.multiplyScalar(thickness);
    outline.userData.isOutline = true;
    return outline;
  }

  static create(THREE, options = {}) {
    const {
      baseColor = 0xa78bfa, // Bright purple
      targetHeight = 1.8
    } = options;

    const gradientMap = this.createToonGradient(THREE);
    const pusher = new THREE.Group();
    pusher.userData.deviceType = 'Pusher';

    // Base mount with cartoon style
    const baseGeometry = new THREE.BoxGeometry(0.8, 0.25, 0.8);
    const base = new THREE.Mesh(
      baseGeometry,
      new THREE.MeshToonMaterial({
        color: 0x64748b,
        gradientMap: gradientMap
      })
    );
    base.position.y = 0.125;
    base.castShadow = true;
    pusher.add(base);
    const baseOutline = this.createOutline(baseGeometry.clone(), 0x000000, 1.15);
    baseOutline.position.y = 0.125;
    pusher.add(baseOutline);

    // Vertical support with bright color
    const supportGeometry = new THREE.BoxGeometry(0.18, 1.2, 0.18);
    const support = new THREE.Mesh(
      supportGeometry,
      new THREE.MeshToonMaterial({
        color: baseColor,
        gradientMap: gradientMap,
        emissive: baseColor,
        emissiveIntensity: 0.3
      })
    );
    support.position.y = 0.8;
    support.castShadow = true;
    pusher.add(support);
    const supportOutline = this.createOutline(supportGeometry.clone(), 0x000000, 1.20);
    supportOutline.position.y = 0.8;
    pusher.add(supportOutline);

    // Pusher arm mount
    const mountGeometry = new THREE.BoxGeometry(0.35, 0.3, 0.35);
    const armMount = new THREE.Mesh(
      mountGeometry,
      new THREE.MeshToonMaterial({
        color: 0x94a3b8,
        gradientMap: gradientMap
      })
    );
    armMount.position.y = 1.4;
    armMount.castShadow = true;
    pusher.add(armMount);
    const mountOutline = this.createOutline(mountGeometry.clone(), 0x000000, 1.15);
    mountOutline.position.y = 1.4;
    pusher.add(mountOutline);

    // Pusher arm (extends forward/backward) - 초기값 더욱 안쪽으로
    const armGeometry = new THREE.BoxGeometry(1.2, 0.15, 0.15);
    const arm = new THREE.Mesh(
      armGeometry,
      new THREE.MeshToonMaterial({
        color: 0xfbbf24,
        gradientMap: gradientMap,
        emissive: 0xf59e0b,
        emissiveIntensity: 0.3
      })
    );
    arm.position.set(0.2, 1.4, 0);  // 0.4 → 0.2 (더 안쪽으로)
    arm.castShadow = true;
    arm.userData.isPusherArm = true;
    pusher.add(arm);
    const armOutline = this.createOutline(armGeometry.clone(), 0x000000, 1.20);
    armOutline.position.set(0.2, 1.4, 0);  // 0.4 → 0.2
    armOutline.userData.isPusherArmOutline = true;
    pusher.add(armOutline);

    // Pusher head (contact plate)
    const headGeometry = new THREE.BoxGeometry(0.1, 0.45, 0.55);
    const head = new THREE.Mesh(
      headGeometry,
      new THREE.MeshToonMaterial({
        color: 0xfb7185,
        gradientMap: gradientMap,
        emissive: 0xef4444,
        emissiveIntensity: 0.4
      })
    );
    head.position.set(0.76, 1.4, 0);  // 0.96 → 0.76 (0.2 + 0.56)
    head.castShadow = true;
    head.userData.isPusherHead = true;
    pusher.add(head);
    const headOutline = this.createOutline(headGeometry.clone(), 0x000000, 1.25);
    headOutline.position.set(0.76, 1.4, 0);  // 0.96 → 0.76
    headOutline.userData.isPusherHeadOutline = true;
    pusher.add(headOutline);

    // Pneumatic cylinder - 안쪽으로 이동
    const cylinderGeometry = new THREE.CylinderGeometry(0.1, 0.1, 0.6, 16);
    const cylinder = new THREE.Mesh(
      cylinderGeometry,
      new THREE.MeshToonMaterial({
        color: 0x475569,
        gradientMap: gradientMap
      })
    );
    cylinder.rotation.z = Math.PI / 2;
    cylinder.position.set(0.1, 1.5, 0);  // 0.3 → 0.1 (실린더 안쪽으로)
    cylinder.castShadow = true;
    pusher.add(cylinder);
    const cylinderOutline = this.createOutline(cylinderGeometry.clone(), 0x000000, 1.15);
    cylinderOutline.rotation.z = Math.PI / 2;
    cylinderOutline.position.set(0.1, 1.5, 0);  // 0.3 → 0.1
    pusher.add(cylinderOutline);

    // Air ports with bright glow - 실린더와 함께 안쪽으로
    const portGeometry = new THREE.CylinderGeometry(0.04, 0.04, 0.06, 8);
    [-0.1, 0.3].forEach(x => {  // [0.1, 0.5] → [-0.1, 0.3] (안쪽으로)
      const port = new THREE.Mesh(
        portGeometry.clone(),
        new THREE.MeshToonMaterial({
          color: 0x60a5fa,
          emissive: 0x60a5fa,
          emissiveIntensity: 0.7,
          gradientMap: gradientMap
        })
      );
      port.position.set(x, 1.5, 0.12);
      pusher.add(port);
      const portOutline = this.createOutline(portGeometry.clone(), 0x000000, 1.25);
      portOutline.position.set(x, 1.5, 0.12);
      pusher.add(portOutline);
    });

    // FWD/BWD label with bold cartoon style
    const canvas = document.createElement('canvas');
    canvas.width = 256;
    canvas.height = 128;
    const ctx = canvas.getContext('2d');

    ctx.fillStyle = '#334155';
    ctx.fillRect(0, 0, 256, 128);

    ctx.strokeStyle = '#a78bfa';
    ctx.lineWidth = 6;
    ctx.strokeRect(6, 6, 244, 116);

    ctx.font = 'bold 44px Arial';
    ctx.textAlign = 'center';

    ctx.strokeStyle = '#000000';
    ctx.lineWidth = 8;
    ctx.strokeText('FWD', 128, 50);
    ctx.fillStyle = '#22d3ee';
    ctx.fillText('FWD', 128, 50);

    ctx.strokeStyle = '#000000';
    ctx.lineWidth = 8;
    ctx.strokeText('BWD', 128, 100);
    ctx.fillStyle = '#fb7185';
    ctx.fillText('BWD', 128, 100);

    const texture = new THREE.CanvasTexture(canvas);
    const label = new THREE.Mesh(
      new THREE.PlaneGeometry(0.4, 0.2),
      new THREE.MeshBasicMaterial({ map: texture, transparent: true })
    );
    label.position.set(0, 0.8, 0.45);
    pusher.add(label);

    // State indicator with cartoon glow
    const indicator = new THREE.Mesh(
      new THREE.BoxGeometry(1.0, 1.6, 0.9),
      new THREE.MeshToonMaterial({
        color: 0x22d3ee,
        transparent: true,
        opacity: 0.25,
        emissive: 0x22d3ee,
        emissiveIntensity: 0.5,
        gradientMap: gradientMap
      })
    );
    indicator.position.set(0.3, 0.8, 0);  // 0.4 → 0.3 (더 안쪽으로)
    indicator.userData.isStateIndicator = true;
    pusher.add(indicator);

    // Scale to target height
    const bbox = new THREE.Box3().setFromObject(pusher);
    const scale = targetHeight / (bbox.max.y - bbox.min.y);
    pusher.scale.set(scale, scale, scale);

    return pusher;
  }

  static updateState(pusher, state) {
    const colors = {
      'R': 0x22d3ee,
      'G': 0xfde047,
      'F': 0x60a5fa,
      'H': 0xa78bfa
    };
    const color = colors[state] || 0x22d3ee;
    pusher.traverse(child => {
      if (child.userData.isStateIndicator && child.material) {
        child.material.color.setHex(color);
        child.material.emissive.setHex(color);
      }
    });
  }

  /**
   * Animate pusher arm movement
   * @param {THREE.Group} pusher - Pusher object
   * @param {string} direction - 'FWD' or 'BWD'
   * @param {number} speed - Animation speed
   */
  static animate(pusher, direction, speed) {
    speed = speed || 0.1;

    let arm = null;
    let armOutline = null;
    let head = null;
    let headOutline = null;

    pusher.traverse(child => {
      if (child.userData.isPusherArm) arm = child;
      if (child.userData.isPusherArmOutline) armOutline = child;
      if (child.userData.isPusherHead) head = child;
      if (child.userData.isPusherHeadOutline) headOutline = child;
    });

    if (!arm) return false;

    const scale = pusher.scale.x;
    const minX = 0.2;  // Retracted (더 안쪽)
    const maxX = 0.8;  // Extended

    const currentX = arm.position.x / scale;
    let targetX = currentX;

    if (direction === 'FWD') {
      targetX = maxX;
    } else if (direction === 'BWD') {
      targetX = minX;
    }

    const newX = currentX + (targetX - currentX) * speed;
    arm.position.x = newX * scale;
    if (armOutline) armOutline.position.x = newX * scale;
    if (head) head.position.x = (newX + 0.56) * scale;
    if (headOutline) headOutline.position.x = (newX + 0.56) * scale;

    return Math.abs(newX - targetX) < 0.01;
  }

  static setPosition(pusher, position) {
    position = Math.max(0, Math.min(1, position));

    let arm = null;
    let armOutline = null;
    let head = null;
    let headOutline = null;

    pusher.traverse(child => {
      if (child.userData.isPusherArm) arm = child;
      if (child.userData.isPusherArmOutline) armOutline = child;
      if (child.userData.isPusherHead) head = child;
      if (child.userData.isPusherHeadOutline) headOutline = child;
    });

    if (!arm) return;

    const scale = pusher.scale.x;
    const minX = 0.2;  // Retracted (더 안쪽)
    const maxX = 0.8;  // Extended
    const targetX = minX + (maxX - minX) * position;

    arm.position.x = targetX * scale;
    if (armOutline) armOutline.position.x = targetX * scale;
    if (head) head.position.x = (targetX + 0.56) * scale;
    if (headOutline) headOutline.position.x = (targetX + 0.56) * scale;
  }
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = Pusher;
}

if (typeof window !== 'undefined') {
  window.Pusher = Pusher;
}
