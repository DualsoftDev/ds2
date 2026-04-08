/**
 * Barrier_Arm - 차단기 암 (Cartoon Style)
 * 명령어: RAISE, LOWER, STOP
 * - RAISE: 암을 위로 올림 (+90도)
 * - LOWER: 암을 수평으로 내림 (0도)
 * - STOP: 현재 위치 유지
 * 용도: 진입 차단, 주차장 게이트
 * @version 2.0 - Fixed rotation direction (upward)
 */

class Barrier_Arm {
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
      baseColor = 0xef4444,  // Red
      targetHeight = 2.0
    } = options;

    const gradientMap = this.createToonGradient(THREE);
    const barrier = new THREE.Group();
    barrier.userData.deviceType = 'Barrier_Arm';

    // ===== BASE HOUSING =====
    const baseGeometry = new THREE.CylinderGeometry(0.4, 0.5, 1.0, 20);
    const base = new THREE.Mesh(
      baseGeometry,
      new THREE.MeshToonMaterial({
        color: 0x64748b,
        gradientMap: gradientMap
      })
    );
    base.position.y = 0.5;
    base.castShadow = true;
    barrier.add(base);
    const baseOutline = this.createOutline(baseGeometry.clone(), 0x000000, 1.12);
    baseOutline.position.y = 0.5;
    barrier.add(baseOutline);

    // ===== ROTATING ARM =====
    const armGroup = new THREE.Group();
    armGroup.position.y = 1.0;
    armGroup.userData.isArm = true;
    barrier.add(armGroup);

    // Pivot housing
    const pivotGeometry = new THREE.CylinderGeometry(0.25, 0.25, 0.3, 16);
    const pivot = new THREE.Mesh(
      pivotGeometry,
      new THREE.MeshToonMaterial({
        color: 0xfbbf24,
        gradientMap: gradientMap,
        emissive: 0xf59e0b,
        emissiveIntensity: 0.4
      })
    );
    pivot.castShadow = true;
    armGroup.add(pivot);
    const pivotOutline = this.createOutline(pivotGeometry.clone(), 0x000000, 1.15);
    armGroup.add(pivotOutline);

    // Barrier arm
    const armGeometry = new THREE.BoxGeometry(3.0, 0.15, 0.15);
    const arm = new THREE.Mesh(
      armGeometry,
      new THREE.MeshToonMaterial({
        color: baseColor,
        gradientMap: gradientMap,
        emissive: baseColor,
        emissiveIntensity: 0.3
      })
    );
    arm.position.set(1.5, 0, 0);
    arm.castShadow = true;
    armGroup.add(arm);
    const armOutline = this.createOutline(armGeometry.clone(), 0x000000, 1.15);
    armOutline.position.set(1.5, 0, 0);
    armGroup.add(armOutline);

    // Warning stripes
    const stripeGeometry = new THREE.BoxGeometry(0.3, 0.16, 0.16);
    const stripeMaterial = new THREE.MeshToonMaterial({
      color: 0xfde047,
      gradientMap: gradientMap,
      emissive: 0xfbbf24,
      emissiveIntensity: 0.5
    });

    for (let i = 0; i < 5; i++) {
      const stripe = new THREE.Mesh(stripeGeometry.clone(), stripeMaterial);
      stripe.position.set(0.5 + i * 0.6, 0, 0);
      stripe.castShadow = true;
      armGroup.add(stripe);
    }

    // End cap
    const capGeometry = new THREE.BoxGeometry(0.2, 0.25, 0.25);
    const cap = new THREE.Mesh(
      capGeometry,
      new THREE.MeshToonMaterial({
        color: 0xef4444,
        gradientMap: gradientMap,
        emissive: 0xdc2626,
        emissiveIntensity: 0.5
      })
    );
    cap.position.set(3.1, 0, 0);
    cap.castShadow = true;
    armGroup.add(cap);

    // ===== WARNING LIGHT =====
    const lightGeometry = new THREE.SphereGeometry(0.12, 12, 12);
    const light = new THREE.Mesh(
      lightGeometry,
      new THREE.MeshStandardMaterial({
        color: 0xef4444,
        emissive: 0xef4444,
        emissiveIntensity: 1.0
      })
    );
    light.position.set(0, 1.3, 0);
    light.castShadow = true;
    light.userData.isWarningLight = true;
    barrier.add(light);

    // ===== CONTROL BOX =====
    const controlGeometry = new THREE.BoxGeometry(0.35, 0.5, 0.3);
    const control = new THREE.Mesh(
      controlGeometry,
      new THREE.MeshToonMaterial({
        color: 0x60a5fa,
        gradientMap: gradientMap,
        emissive: 0x3b82f6,
        emissiveIntensity: 0.3
      })
    );
    control.position.set(0, 0.3, 0.4);
    control.castShadow = true;
    barrier.add(control);
    const controlOutline = this.createOutline(controlGeometry.clone(), 0x000000, 1.15);
    controlOutline.position.set(0, 0.3, 0.4);
    barrier.add(controlOutline);

    // ===== LABEL =====
    const canvas = document.createElement('canvas');
    canvas.width = 256;
    canvas.height = 128;
    const ctx = canvas.getContext('2d');

    ctx.fillStyle = '#334155';
    ctx.fillRect(0, 0, 256, 128);

    ctx.strokeStyle = '#ef4444';
    ctx.lineWidth = 6;
    ctx.strokeRect(6, 6, 244, 116);

    ctx.font = 'bold 36px Arial';
    ctx.textAlign = 'center';

    ctx.strokeStyle = '#000000';
    ctx.lineWidth = 8;
    ctx.strokeText('BARRIER', 128, 50);
    ctx.fillStyle = '#22d3ee';
    ctx.fillText('BARRIER', 128, 50);

    ctx.font = 'bold 32px Arial';
    ctx.strokeText('ARM', 128, 95);
    ctx.fillStyle = '#ef4444';
    ctx.fillText('ARM', 128, 95);

    const texture = new THREE.CanvasTexture(canvas);
    const label = new THREE.Mesh(
      new THREE.PlaneGeometry(0.6, 0.3),
      new THREE.MeshBasicMaterial({ map: texture, transparent: true })
    );
    label.position.set(0, 0.7, 0.3);
    barrier.add(label);

    // ===== STATE INDICATOR =====
    const indicator = new THREE.Mesh(
      new THREE.BoxGeometry(3.5, 1.8, 1.0),
      new THREE.MeshToonMaterial({
        color: 0x22d3ee,
        transparent: true,
        opacity: 0.2,
        emissive: 0x22d3ee,
        emissiveIntensity: 0.4,
        gradientMap: gradientMap
      })
    );
    indicator.position.set(1.2, 0.9, 0);
    indicator.userData.isStateIndicator = true;
    barrier.add(indicator);

    const bbox = new THREE.Box3().setFromObject(barrier);
    const scale = targetHeight / (bbox.max.y - bbox.min.y);
    barrier.scale.set(scale, scale, scale);

    return barrier;
  }

  static updateState(barrier, state) {
    const colors = {
      'R': 0x22d3ee,
      'G': 0xfde047,
      'F': 0x60a5fa,
      'H': 0xa78bfa
    };
    const color = colors[state] || 0x22d3ee;
    barrier.traverse(child => {
      if (child.userData.isStateIndicator && child.material) {
        child.material.color.setHex(color);
        child.material.emissive.setHex(color);
      }
    });
  }

  /**
   * Animate barrier arm
   * - RAISE: 위로 올림 (+90도)
   * - LOWER: 아래로 내림 (0도)
   * - STOP: 현재 위치 유지
   */
  static animate(barrier, direction, speed) {
    speed = speed || 0.04;

    let arm = null;

    barrier.traverse(child => {
      if (child.userData.isArm) arm = child;
      if (child.userData.isWarningLight && child.material) {
        // Blink warning light
        const time = Date.now() * 0.005;
        child.material.opacity = 0.5 + Math.sin(time) * 0.5;
      }
    });

    if (!arm) return false;

    if (direction === 'RAISE') {
      // Raise arm (rotate up) - 위로 올림
      const targetAngle = Math.PI / 2;  // -Math.PI/2 → +Math.PI/2 (위 방향)
      const currentAngle = arm.rotation.z;
      const newAngle = currentAngle + (targetAngle - currentAngle) * speed;
      arm.rotation.z = newAngle;
      return Math.abs(newAngle - targetAngle) < 0.01;
    } else if (direction === 'LOWER') {
      // Lower arm (rotate down) - 수평으로 내림
      const targetAngle = 0;
      const currentAngle = arm.rotation.z;
      const newAngle = currentAngle + (targetAngle - currentAngle) * speed;
      arm.rotation.z = newAngle;
      return Math.abs(newAngle - targetAngle) < 0.01;
    } else if (direction === 'STOP') {
      // Hold position
      return false;
    }

    return false;
  }
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = Barrier_Arm;
}

if (typeof window !== 'undefined') {
  window.Barrier_Arm = Barrier_Arm;
}
