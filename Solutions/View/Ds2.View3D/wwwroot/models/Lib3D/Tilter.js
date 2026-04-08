/**
 * Tilter - 틸터 (Cartoon Style)
 * 명령어: TILT_FORWARD, TILT_BACK, LEVEL
 * 용도: 각도 조절, 경사 제어
 * @version 1.0 - Cartoon/Toon rendering with tilting motion
 */

class Tilter {
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
      baseColor = 0xf59e0b,  // Amber
      targetHeight = 2.0
    } = options;

    const gradientMap = this.createToonGradient(THREE);
    const tilter = new THREE.Group();
    tilter.userData.deviceType = 'Tilter';

    // ===== BASE FRAME =====
    const baseGeometry = new THREE.BoxGeometry(2.0, 0.3, 1.5);
    const base = new THREE.Mesh(
      baseGeometry,
      new THREE.MeshToonMaterial({
        color: 0x64748b,
        gradientMap: gradientMap
      })
    );
    base.position.y = 0.15;
    base.castShadow = true;
    tilter.add(base);
    const baseOutline = this.createOutline(baseGeometry.clone(), 0x000000, 1.12);
    baseOutline.position.y = 0.15;
    tilter.add(baseOutline);

    // ===== PIVOT SUPPORTS =====
    const supportGeometry = new THREE.BoxGeometry(0.3, 0.8, 0.3);
    const supportMaterial = new THREE.MeshToonMaterial({
      color: 0x475569,
      gradientMap: gradientMap
    });

    [-0.7, 0.7].forEach(z => {
      const support = new THREE.Mesh(supportGeometry.clone(), supportMaterial);
      support.position.set(0, 0.7, z);
      support.castShadow = true;
      tilter.add(support);
      const supportOutline = this.createOutline(supportGeometry.clone(), 0x000000, 1.12);
      supportOutline.position.set(0, 0.7, z);
      tilter.add(supportOutline);
    });

    // ===== TILTING PLATFORM =====
    const platformGroup = new THREE.Group();
    platformGroup.position.y = 1.1;
    platformGroup.userData.isPlatform = true;
    tilter.add(platformGroup);

    const platformGeometry = new THREE.BoxGeometry(1.8, 0.2, 1.4);
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

    // Safety railings
    const railingGeometry = new THREE.BoxGeometry(1.9, 0.08, 0.08);
    const railingMaterial = new THREE.MeshToonMaterial({
      color: 0xfde047,
      gradientMap: gradientMap,
      emissive: 0xfbbf24,
      emissiveIntensity: 0.4
    });

    [-0.7, 0.7].forEach(z => {
      const railing = new THREE.Mesh(railingGeometry.clone(), railingMaterial);
      railing.position.set(0, 0.15, z);
      railing.castShadow = true;
      platformGroup.add(railing);
    });

    // Platform surface texture
    const surfaceGeometry = new THREE.BoxGeometry(1.7, 0.21, 1.3);
    const surface = new THREE.Mesh(
      surfaceGeometry,
      new THREE.MeshToonMaterial({
        color: 0x94a3b8,
        gradientMap: gradientMap
      })
    );
    surface.castShadow = true;
    platformGroup.add(surface);

    // ===== HYDRAULIC ACTUATORS =====
    const actuatorGeometry = new THREE.CylinderGeometry(0.1, 0.1, 0.8, 12);
    const actuatorMaterial = new THREE.MeshToonMaterial({
      color: 0x60a5fa,
      gradientMap: gradientMap,
      emissive: 0x3b82f6,
      emissiveIntensity: 0.3
    });

    [-0.6, 0.6].forEach(x => {
      const actuator = new THREE.Mesh(actuatorGeometry.clone(), actuatorMaterial);
      actuator.position.set(x, 0.7, -0.5);
      actuator.rotation.x = Math.PI / 6;
      actuator.castShadow = true;
      actuator.userData.isActuator = true;
      tilter.add(actuator);
      const actuatorOutline = this.createOutline(actuatorGeometry.clone(), 0x000000, 1.15);
      actuatorOutline.position.set(x, 0.7, -0.5);
      actuatorOutline.rotation.x = Math.PI / 6;
      tilter.add(actuatorOutline);
    });

    // ===== PIVOT AXIS =====
    const axisGeometry = new THREE.CylinderGeometry(0.12, 0.12, 1.5, 16);
    const axis = new THREE.Mesh(
      axisGeometry,
      new THREE.MeshToonMaterial({
        color: 0x1e293b,
        gradientMap: gradientMap
      })
    );
    axis.position.set(0, 1.1, 0);
    axis.rotation.z = Math.PI / 2;
    axis.castShadow = true;
    tilter.add(axis);

    // ===== ANGLE INDICATOR =====
    const indicatorGeometry = new THREE.ConeGeometry(0.15, 0.3, 8);
    const indicator = new THREE.Mesh(
      indicatorGeometry,
      new THREE.MeshToonMaterial({
        color: 0xef4444,
        gradientMap: gradientMap,
        emissive: 0xdc2626,
        emissiveIntensity: 0.5
      })
    );
    indicator.position.set(0.95, 1.1, 0);
    indicator.rotation.z = -Math.PI / 2;
    indicator.castShadow = true;
    platformGroup.add(indicator);

    // ===== LABEL =====
    const canvas = document.createElement('canvas');
    canvas.width = 256;
    canvas.height = 128;
    const ctx = canvas.getContext('2d');

    ctx.fillStyle = '#334155';
    ctx.fillRect(0, 0, 256, 128);

    ctx.strokeStyle = '#f59e0b';
    ctx.lineWidth = 6;
    ctx.strokeRect(6, 6, 244, 116);

    ctx.font = 'bold 48px Arial';
    ctx.textAlign = 'center';

    ctx.strokeStyle = '#000000';
    ctx.lineWidth = 8;
    ctx.strokeText('TILTER', 128, 80);
    ctx.fillStyle = '#22d3ee';
    ctx.fillText('TILTER', 128, 80);

    const texture = new THREE.CanvasTexture(canvas);
    const label = new THREE.Mesh(
      new THREE.PlaneGeometry(0.6, 0.3),
      new THREE.MeshBasicMaterial({ map: texture, transparent: true })
    );
    label.position.set(0, 0.5, 0);
    tilter.add(label);

    // ===== STATE INDICATOR =====
    const stateIndicator = new THREE.Mesh(
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
    stateIndicator.position.set(0, 0.9, 0);
    stateIndicator.userData.isStateIndicator = true;
    tilter.add(stateIndicator);

    const bbox = new THREE.Box3().setFromObject(tilter);
    const scale = targetHeight / (bbox.max.y - bbox.min.y);
    tilter.scale.set(scale, scale, scale);

    return tilter;
  }

  static updateState(tilter, state) {
    const colors = {
      'R': 0x22d3ee,
      'G': 0xfde047,
      'F': 0x60a5fa,
      'H': 0xa78bfa
    };
    const color = colors[state] || 0x22d3ee;
    tilter.traverse(child => {
      if (child.userData.isStateIndicator && child.material) {
        child.material.color.setHex(color);
        child.material.emissive.setHex(color);
      }
    });
  }

  static animate(tilter, direction, speed) {
    speed = speed || 0.04;

    let platform = null;

    tilter.traverse(child => {
      if (child.userData.isPlatform) platform = child;
    });

    if (!platform) return false;

    const maxTilt = Math.PI / 6; // 30 degrees

    if (direction === 'TILT_FORWARD') {
      // Tilt forward
      const targetAngle = maxTilt;
      const currentAngle = platform.rotation.x;
      const newAngle = currentAngle + (targetAngle - currentAngle) * speed;
      platform.rotation.x = newAngle;
      return Math.abs(newAngle - targetAngle) < 0.01;
    } else if (direction === 'TILT_BACK') {
      // Tilt backward
      const targetAngle = -maxTilt;
      const currentAngle = platform.rotation.x;
      const newAngle = currentAngle + (targetAngle - currentAngle) * speed;
      platform.rotation.x = newAngle;
      return Math.abs(newAngle - targetAngle) < 0.01;
    } else if (direction === 'LEVEL') {
      // Return to level
      const targetAngle = 0;
      const currentAngle = platform.rotation.x;
      const newAngle = currentAngle + (targetAngle - currentAngle) * speed;
      platform.rotation.x = newAngle;
      return Math.abs(newAngle - targetAngle) < 0.01;
    }

    return false;
  }
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = Tilter;
}

if (typeof window !== 'undefined') {
  window.Tilter = Tilter;
}
