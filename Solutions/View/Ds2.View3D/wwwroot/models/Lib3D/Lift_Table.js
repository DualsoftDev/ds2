/**
 * Lift_Table - 리프트 테이블 (Cartoon Style)
 * 명령어: RAISE, LOWER, HOLD
 * 용도: 높이 조절, 작업 위치 변경
 * @version 1.0 - Cartoon/Toon rendering with vertical lift animation
 */

class Lift_Table {
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
      side: THREE.BackSide,
      depthWrite: false
    });
    const outline = new THREE.Mesh(geometry, outlineMaterial);
    outline.scale.multiplyScalar(thickness);
    outline.userData.isOutline = true;
    outline.renderOrder = -1;
    return outline;
  }

  static create(THREE, options = {}) {
    const {
      baseColor = 0xf97316,  // Orange
      targetHeight = 2.5
    } = options;

    const gradientMap = this.createToonGradient(THREE);
    const lift = new THREE.Group();
    lift.userData.deviceType = 'Lift_Table';

    // ===== BASE =====
    const baseGeometry = new THREE.BoxGeometry(2.0, 0.2, 1.5);
    const base = new THREE.Mesh(
      baseGeometry,
      new THREE.MeshToonMaterial({
        color: 0x64748b,
        gradientMap: gradientMap
      })
    );
    base.position.y = 0.1;
    base.castShadow = true;
    lift.add(base);
    const baseOutline = this.createOutline(baseGeometry.clone(), 0x000000, 1.12);
    baseOutline.position.y = 0.1;
    lift.add(baseOutline);

    // ===== SCISSOR MECHANISM =====
    const scissorGroup = new THREE.Group();
    scissorGroup.position.y = 0.2;
    scissorGroup.userData.isScissor = true;
    lift.add(scissorGroup);

    // Scissor arms
    const armGeometry = new THREE.BoxGeometry(1.4, 0.08, 0.08);
    const armMaterial = new THREE.MeshToonMaterial({
      color: baseColor,
      gradientMap: gradientMap,
      emissive: baseColor,
      emissiveIntensity: 0.3
    });

    // Left side scissor
    for (let i = 0; i < 2; i++) {
      const leftArm1 = new THREE.Mesh(armGeometry.clone(), armMaterial);
      leftArm1.position.set(-0.3, 0.3 + i * 0.15, -0.5);
      leftArm1.rotation.z = Math.PI / 6;
      leftArm1.castShadow = true;
      scissorGroup.add(leftArm1);

      const leftArm2 = new THREE.Mesh(armGeometry.clone(), armMaterial);
      leftArm2.position.set(-0.3, 0.3 + i * 0.15, -0.5);
      leftArm2.rotation.z = -Math.PI / 6;
      leftArm2.castShadow = true;
      scissorGroup.add(leftArm2);
    }

    // Right side scissor
    for (let i = 0; i < 2; i++) {
      const rightArm1 = new THREE.Mesh(armGeometry.clone(), armMaterial);
      rightArm1.position.set(-0.3, 0.3 + i * 0.15, 0.5);
      rightArm1.rotation.z = Math.PI / 6;
      rightArm1.castShadow = true;
      scissorGroup.add(rightArm1);

      const rightArm2 = new THREE.Mesh(armGeometry.clone(), armMaterial);
      rightArm2.position.set(-0.3, 0.3 + i * 0.15, 0.5);
      rightArm2.rotation.z = -Math.PI / 6;
      rightArm2.castShadow = true;
      scissorGroup.add(rightArm2);
    }

    // ===== PLATFORM =====
    const platformGroup = new THREE.Group();
    platformGroup.position.y = 1.2;  // 0.8 → 1.2 (상판 더 위로)
    platformGroup.userData.isPlatform = true;
    lift.add(platformGroup);

    const platformGeometry = new THREE.BoxGeometry(1.8, 0.15, 1.4);
    const platform = new THREE.Mesh(
      platformGeometry,
      new THREE.MeshToonMaterial({
        color: 0xfbbf24,
        gradientMap: gradientMap,
        emissive: 0xf59e0b,
        emissiveIntensity: 0.3
      })
    );
    platform.castShadow = true;
    platformGroup.add(platform);
    const platformOutline = this.createOutline(platformGeometry.clone(), 0x000000, 1.12);
    platformGroup.add(platformOutline);

    // Safety rails
    const railGeometry = new THREE.BoxGeometry(1.9, 0.08, 0.08);
    const railMaterial = new THREE.MeshToonMaterial({
      color: 0xfde047,
      gradientMap: gradientMap
    });

    [-0.7, 0.7].forEach(z => {
      const rail = new THREE.Mesh(railGeometry.clone(), railMaterial);
      rail.position.set(0, 0.15, z);
      rail.castShadow = true;
      platformGroup.add(rail);
    });

    // ===== HYDRAULIC CYLINDERS =====
    const cylinderGeometry = new THREE.CylinderGeometry(0.08, 0.08, 0.6, 12);
    const cylinderMaterial = new THREE.MeshToonMaterial({
      color: 0x60a5fa,
      gradientMap: gradientMap,
      emissive: 0x3b82f6,
      emissiveIntensity: 0.3
    });

    [-0.6, 0.6].forEach(x => {
      const cylinder = new THREE.Mesh(cylinderGeometry.clone(), cylinderMaterial);
      cylinder.position.set(x, 0.5, 0);
      cylinder.castShadow = true;
      lift.add(cylinder);
      const cylinderOutline = this.createOutline(cylinderGeometry.clone(), 0x000000, 1.15);
      cylinderOutline.position.set(x, 0.5, 0);
      lift.add(cylinderOutline);
    });

    // ===== LABEL =====
    const canvas = document.createElement('canvas');
    canvas.width = 256;
    canvas.height = 128;
    const ctx = canvas.getContext('2d');

    ctx.fillStyle = '#334155';
    ctx.fillRect(0, 0, 256, 128);

    ctx.strokeStyle = '#f97316';
    ctx.lineWidth = 6;
    ctx.strokeRect(6, 6, 244, 116);

    ctx.font = 'bold 36px Arial';
    ctx.textAlign = 'center';

    ctx.strokeStyle = '#000000';
    ctx.lineWidth = 8;
    ctx.strokeText('LIFT', 128, 50);
    ctx.fillStyle = '#22d3ee';
    ctx.fillText('LIFT', 128, 50);

    ctx.font = 'bold 32px Arial';
    ctx.strokeText('TABLE', 128, 95);
    ctx.fillStyle = '#f97316';
    ctx.fillText('TABLE', 128, 95);

    const texture = new THREE.CanvasTexture(canvas);
    const label = new THREE.Mesh(
      new THREE.PlaneGeometry(0.8, 0.4),
      new THREE.MeshBasicMaterial({ map: texture, transparent: true })
    );
    label.position.set(0, 1.3, 0);
    lift.add(label);

    // ===== STATE INDICATOR =====
    const indicator = new THREE.Mesh(
      new THREE.BoxGeometry(2.2, 1.8, 1.7),
      new THREE.MeshToonMaterial({
        color: 0x22d3ee,
        transparent: true,
        opacity: 0.2,
        emissive: 0x22d3ee,
        emissiveIntensity: 0.4,
        gradientMap: gradientMap
      })
    );
    indicator.position.set(0, 0.7, 0);
    indicator.userData.isStateIndicator = true;
    lift.add(indicator);

    const bbox = new THREE.Box3().setFromObject(lift);
    const scale = targetHeight / (bbox.max.y - bbox.min.y);
    lift.scale.set(scale, scale, scale);

    return lift;
  }

  static updateState(lift, state) {
    const colors = {
      'R': 0x22d3ee,
      'G': 0xfde047,
      'F': 0x60a5fa,
      'H': 0xa78bfa
    };
    const color = colors[state] || 0x22d3ee;
    lift.traverse(child => {
      if (child.userData.isStateIndicator && child.material) {
        child.material.color.setHex(color);
        child.material.emissive.setHex(color);
      }
    });
  }

  static animate(lift, direction, speed) {
    speed = speed || 0.03;

    let platform = null;
    let scissor = null;

    lift.traverse(child => {
      if (child.userData.isPlatform) platform = child;
      if (child.userData.isScissor) scissor = child;
    });

    if (!platform) return false;

    const minY = 0.6;   // 0.4 → 0.6 (최소 높이 상향)
    const maxY = 1.8;   // 1.5 → 1.8 (최대 높이 상향)
    const currentY = platform.position.y;

    if (direction === 'RAISE') {
      // Raise platform
      const targetY = maxY;
      const newY = currentY + (targetY - currentY) * speed;
      platform.position.y = newY;

      if (scissor) {
        const angle = (newY - minY) / (maxY - minY) * (Math.PI / 3);
        scissor.scale.y = 1 + (newY - minY) / (maxY - minY);
      }

      return Math.abs(newY - targetY) < 0.01;
    } else if (direction === 'LOWER') {
      // Lower platform
      const targetY = minY;
      const newY = currentY + (targetY - currentY) * speed;
      platform.position.y = newY;

      if (scissor) {
        const angle = (newY - minY) / (maxY - minY) * (Math.PI / 3);
        scissor.scale.y = 1 + (newY - minY) / (maxY - minY);
      }

      return Math.abs(newY - targetY) < 0.01;
    } else if (direction === 'HOLD') {
      // Hold position
      return false;
    }

    return false;
  }
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = Lift_Table;
}

if (typeof window !== 'undefined') {
  window.Lift_Table = Lift_Table;
}
