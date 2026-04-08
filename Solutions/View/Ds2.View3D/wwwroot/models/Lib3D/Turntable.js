/**
 * Turntable - 턴테이블 (Cartoon Style)
 * 명령어: TURN_90, TURN_180, RESET
 * - TURN_90: 0° → 90° → 180° → 270° → 0° 순환
 * - TURN_180: 180도 회전
 * - RESET: 0도로 복귀
 * 용도: 90도 회전, 방향 전환
 * @version 2.0 - Indexed rotation with 0/90/180/270 positions
 */

class Turntable {
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
      baseColor = 0xa855f7,  // Purple
      targetHeight = 1.2
    } = options;

    const gradientMap = this.createToonGradient(THREE);
    const turntable = new THREE.Group();
    turntable.userData.deviceType = 'Turntable';
    turntable.userData.targetAngle = 0;
    turntable.userData.currentAngle = 0;
    turntable.userData.positionIndex = 0; // 0, 1, 2, 3 (0°, 90°, 180°, 270°)

    // ===== BASE =====
    const baseGeometry = new THREE.CylinderGeometry(1.3, 1.4, 0.25, 32);
    const base = new THREE.Mesh(
      baseGeometry,
      new THREE.MeshToonMaterial({
        color: 0x64748b,
        gradientMap: gradientMap
      })
    );
    base.position.y = 0.125;
    base.castShadow = true;
    turntable.add(base);
    const baseOutline = this.createOutline(baseGeometry.clone(), 0x000000, 1.12);
    baseOutline.position.y = 0.125;
    turntable.add(baseOutline);

    // ===== ROTATING PLATFORM =====
    const platformGroup = new THREE.Group();
    platformGroup.position.y = 0.35;
    platformGroup.userData.isRotatingPlatform = true;
    turntable.add(platformGroup);

    // Platform disk
    const platformGeometry = new THREE.CylinderGeometry(1.2, 1.2, 0.15, 32);
    const platform = new THREE.Mesh(
      platformGeometry,
      new THREE.MeshToonMaterial({
        color: baseColor,
        gradientMap: gradientMap,
        emissive: baseColor,
        emissiveIntensity: 0.3
      })
    );
    platform.castShadow = true;
    platformGroup.add(platform);
    const platformOutline = this.createOutline(platformGeometry.clone(), 0x000000, 1.12);
    platformGroup.add(platformOutline);

    // Direction arrows (4 directions)
    const arrowGeometry = new THREE.ConeGeometry(0.15, 0.4, 8);
    const arrowMaterial = new THREE.MeshToonMaterial({
      color: 0xfbbf24,
      gradientMap: gradientMap,
      emissive: 0xf59e0b,
      emissiveIntensity: 0.5
    });

    for (let i = 0; i < 4; i++) {
      const angle = (i / 4) * Math.PI * 2;
      const arrow = new THREE.Mesh(arrowGeometry.clone(), arrowMaterial);
      arrow.position.set(
        Math.cos(angle) * 0.8,
        0.2,
        Math.sin(angle) * 0.8
      );
      arrow.rotation.x = Math.PI / 2;
      arrow.rotation.z = -angle;
      arrow.castShadow = true;
      platformGroup.add(arrow);
      const arrowOutline = this.createOutline(arrowGeometry.clone(), 0x000000, 1.20);
      arrowOutline.position.set(
        Math.cos(angle) * 0.8,
        0.2,
        Math.sin(angle) * 0.8
      );
      arrowOutline.rotation.x = Math.PI / 2;
      arrowOutline.rotation.z = -angle;
      platformGroup.add(arrowOutline);
    }

    // Center rollers
    const rollerGeometry = new THREE.CylinderGeometry(0.08, 0.08, 0.2, 12);
    const rollerMaterial = new THREE.MeshToonMaterial({
      color: 0x22d3ee,
      gradientMap: gradientMap,
      emissive: 0x06b6d4,
      emissiveIntensity: 0.4
    });

    for (let i = 0; i < 8; i++) {
      const angle = (i / 8) * Math.PI * 2;
      const roller = new THREE.Mesh(rollerGeometry.clone(), rollerMaterial);
      roller.position.set(
        Math.cos(angle) * 0.4,
        0.15,
        Math.sin(angle) * 0.4
      );
      roller.castShadow = true;
      platformGroup.add(roller);
    }

    // ===== DRIVE MECHANISM =====
    const driveGeometry = new THREE.BoxGeometry(0.35, 0.3, 0.35);
    const drive = new THREE.Mesh(
      driveGeometry,
      new THREE.MeshToonMaterial({
        color: 0x60a5fa,
        gradientMap: gradientMap,
        emissive: 0x3b82f6,
        emissiveIntensity: 0.3
      })
    );
    drive.position.set(0, 0.15, 1.5);
    drive.castShadow = true;
    turntable.add(drive);
    const driveOutline = this.createOutline(driveGeometry.clone(), 0x000000, 1.15);
    driveOutline.position.set(0, 0.15, 1.5);
    turntable.add(driveOutline);

    // ===== LABEL =====
    const canvas = document.createElement('canvas');
    canvas.width = 256;
    canvas.height = 128;
    const ctx = canvas.getContext('2d');

    ctx.fillStyle = '#334155';
    ctx.fillRect(0, 0, 256, 128);

    ctx.strokeStyle = '#a855f7';
    ctx.lineWidth = 6;
    ctx.strokeRect(6, 6, 244, 116);

    ctx.font = 'bold 36px Arial';
    ctx.textAlign = 'center';

    ctx.strokeStyle = '#000000';
    ctx.lineWidth = 8;
    ctx.strokeText('TURN', 128, 50);
    ctx.fillStyle = '#22d3ee';
    ctx.fillText('TURN', 128, 50);

    ctx.font = 'bold 32px Arial';
    ctx.strokeText('TABLE', 128, 95);
    ctx.fillStyle = '#a855f7';
    ctx.fillText('TABLE', 128, 95);

    const texture = new THREE.CanvasTexture(canvas);
    const label = new THREE.Mesh(
      new THREE.PlaneGeometry(0.8, 0.4),
      new THREE.MeshBasicMaterial({ map: texture, transparent: true })
    );
    label.position.set(0, 0.7, 0);
    turntable.add(label);

    // ===== STATE INDICATOR =====
    const indicator = new THREE.Mesh(
      new THREE.CylinderGeometry(1.4, 1.4, 0.8, 32),
      new THREE.MeshToonMaterial({
        color: 0x22d3ee,
        transparent: true,
        opacity: 0.2,
        emissive: 0x22d3ee,
        emissiveIntensity: 0.4,
        gradientMap: gradientMap
      })
    );
    indicator.position.set(0, 0.35, 0);
    indicator.userData.isStateIndicator = true;
    turntable.add(indicator);

    const bbox = new THREE.Box3().setFromObject(turntable);
    const scale = targetHeight / (bbox.max.y - bbox.min.y);
    turntable.scale.set(scale, scale, scale);

    return turntable;
  }

  static updateState(turntable, state) {
    const colors = {
      'R': 0x22d3ee,
      'G': 0xfde047,
      'F': 0x60a5fa,
      'H': 0xa78bfa
    };
    const color = colors[state] || 0x22d3ee;
    turntable.traverse(child => {
      if (child.userData.isStateIndicator && child.material) {
        child.material.color.setHex(color);
        child.material.emissive.setHex(color);
      }
    });
  }

  /**
   * Animate turntable rotation
   * - TURN_90: 0° → 90° → 180° → 270° → 0° (cycle)
   * - TURN_180: 180도 회전
   * - RESET: 0도로 복귀
   */
  static animate(turntable, direction, speed) {
    speed = speed || 0.06;

    let platform = null;

    turntable.traverse(child => {
      if (child.userData.isRotatingPlatform) platform = child;
    });

    if (!platform) return false;

    // Initialize position index if not set
    if (turntable.userData.positionIndex === undefined) {
      turntable.userData.positionIndex = 0;
    }

    // Define the 4 positions: 0°, 90°, 180°, 270°
    const positions = [0, Math.PI / 2, Math.PI, Math.PI * 1.5];

    if (direction === 'TURN_90') {
      // Move to next position (0 → 1 → 2 → 3 → 0)
      turntable.userData.positionIndex = (turntable.userData.positionIndex + 1) % 4;
      turntable.userData.targetAngle = positions[turntable.userData.positionIndex];
    } else if (direction === 'TURN_180') {
      // Rotate 180 degrees from current position
      turntable.userData.positionIndex = (turntable.userData.positionIndex + 2) % 4;
      turntable.userData.targetAngle = positions[turntable.userData.positionIndex];
    } else if (direction === 'RESET') {
      // Return to position 0 (0°)
      turntable.userData.positionIndex = 0;
      turntable.userData.targetAngle = 0;
    }

    // Smooth rotation to target
    const diff = turntable.userData.targetAngle - turntable.userData.currentAngle;

    if (Math.abs(diff) > 0.01) {
      turntable.userData.currentAngle += diff * speed;
      platform.rotation.y = turntable.userData.currentAngle;
      return false;
    } else {
      // Snap to exact target angle
      turntable.userData.currentAngle = turntable.userData.targetAngle;
      platform.rotation.y = turntable.userData.currentAngle;
      return true;
    }
  }
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = Turntable;
}

if (typeof window !== 'undefined') {
  window.Turntable = Turntable;
}
