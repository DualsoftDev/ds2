/**
 * Rotary_Table - 회전 테이블 (Cartoon Style)
 * 명령어: ROTATE_CW, ROTATE_CCW, STOP
 * 용도: 작업물 회전, 인덱싱
 * @version 1.0 - Cartoon/Toon rendering with rotary motion
 */

class Rotary_Table {
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
      baseColor = 0x14b8a6,  // Teal
      targetHeight = 1.5
    } = options;

    const gradientMap = this.createToonGradient(THREE);
    const table = new THREE.Group();
    table.userData.deviceType = 'Rotary_Table';
    table.userData.rotationSpeed = 0;

    // ===== BASE =====
    const baseGeometry = new THREE.CylinderGeometry(0.9, 1.0, 0.3, 32);
    const base = new THREE.Mesh(
      baseGeometry,
      new THREE.MeshToonMaterial({
        color: 0x64748b,
        gradientMap: gradientMap
      })
    );
    base.position.y = 0.15;
    base.castShadow = true;
    table.add(base);
    const baseOutline = this.createOutline(baseGeometry.clone(), 0x000000, 1.12);
    baseOutline.position.y = 0.15;
    table.add(baseOutline);

    // ===== ROTATING PLATFORM =====
    const platformGroup = new THREE.Group();
    platformGroup.position.y = 0.4;
    platformGroup.userData.isRotatingPlatform = true;
    table.add(platformGroup);

    const platformGeometry = new THREE.CylinderGeometry(1.2, 1.2, 0.2, 32);
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

    // Index markers (12 positions)
    for (let i = 0; i < 12; i++) {
      const angle = (i / 12) * Math.PI * 2;
      const markerGeometry = new THREE.BoxGeometry(0.15, 0.25, 0.08);
      const marker = new THREE.Mesh(
        markerGeometry,
        new THREE.MeshToonMaterial({
          color: 0xfbbf24,
          gradientMap: gradientMap,
          emissive: 0xf59e0b,
          emissiveIntensity: 0.4
        })
      );
      marker.position.set(
        Math.cos(angle) * 1.0,
        0.13,
        Math.sin(angle) * 1.0
      );
      marker.rotation.y = -angle;
      marker.castShadow = true;
      platformGroup.add(marker);
      const markerOutline = this.createOutline(markerGeometry.clone(), 0x000000, 1.18);
      markerOutline.position.set(
        Math.cos(angle) * 1.0,
        0.13,
        Math.sin(angle) * 1.0
      );
      markerOutline.rotation.y = -angle;
      platformGroup.add(markerOutline);
    }

    // Center fixture
    const fixtureGeometry = new THREE.CylinderGeometry(0.3, 0.3, 0.4, 16);
    const fixture = new THREE.Mesh(
      fixtureGeometry,
      new THREE.MeshToonMaterial({
        color: 0x22d3ee,
        gradientMap: gradientMap,
        emissive: 0x06b6d4,
        emissiveIntensity: 0.4
      })
    );
    fixture.position.y = 0.3;
    fixture.castShadow = true;
    platformGroup.add(fixture);
    const fixtureOutline = this.createOutline(fixtureGeometry.clone(), 0x000000, 1.15);
    fixtureOutline.position.y = 0.3;
    platformGroup.add(fixtureOutline);

    // ===== DRIVE MOTOR =====
    const motorGeometry = new THREE.BoxGeometry(0.4, 0.3, 0.4);
    const motor = new THREE.Mesh(
      motorGeometry,
      new THREE.MeshToonMaterial({
        color: 0x60a5fa,
        gradientMap: gradientMap,
        emissive: 0x3b82f6,
        emissiveIntensity: 0.3
      })
    );
    motor.position.set(0, 0.15, 1.3);
    motor.castShadow = true;
    table.add(motor);
    const motorOutline = this.createOutline(motorGeometry.clone(), 0x000000, 1.15);
    motorOutline.position.set(0, 0.15, 1.3);
    table.add(motorOutline);

    // ===== LABEL =====
    const canvas = document.createElement('canvas');
    canvas.width = 256;
    canvas.height = 128;
    const ctx = canvas.getContext('2d');

    ctx.fillStyle = '#334155';
    ctx.fillRect(0, 0, 256, 128);

    ctx.strokeStyle = '#14b8a6';
    ctx.lineWidth = 6;
    ctx.strokeRect(6, 6, 244, 116);

    ctx.font = 'bold 32px Arial';
    ctx.textAlign = 'center';

    ctx.strokeStyle = '#000000';
    ctx.lineWidth = 6;
    ctx.strokeText('ROTARY', 128, 50);
    ctx.fillStyle = '#22d3ee';
    ctx.fillText('ROTARY', 128, 50);

    ctx.strokeText('TABLE', 128, 95);
    ctx.fillStyle = '#14b8a6';
    ctx.fillText('TABLE', 128, 95);

    const texture = new THREE.CanvasTexture(canvas);
    const label = new THREE.Mesh(
      new THREE.PlaneGeometry(0.8, 0.4),
      new THREE.MeshBasicMaterial({ map: texture, transparent: true })
    );
    label.position.set(0, 0.9, 0);
    table.add(label);

    // ===== STATE INDICATOR =====
    const indicator = new THREE.Mesh(
      new THREE.CylinderGeometry(1.4, 1.4, 1.0, 32),
      new THREE.MeshToonMaterial({
        color: 0x22d3ee,
        transparent: true,
        opacity: 0.2,
        emissive: 0x22d3ee,
        emissiveIntensity: 0.4,
        gradientMap: gradientMap
      })
    );
    indicator.position.set(0, 0.4, 0);
    indicator.userData.isStateIndicator = true;
    table.add(indicator);

    const bbox = new THREE.Box3().setFromObject(table);
    const scale = targetHeight / (bbox.max.y - bbox.min.y);
    table.scale.set(scale, scale, scale);

    return table;
  }

  static updateState(table, state) {
    const colors = {
      'R': 0x22d3ee,
      'G': 0xfde047,
      'F': 0x60a5fa,
      'H': 0xa78bfa
    };
    const color = colors[state] || 0x22d3ee;
    table.traverse(child => {
      if (child.userData.isStateIndicator && child.material) {
        child.material.color.setHex(color);
        child.material.emissive.setHex(color);
      }
    });
  }

  static animate(table, direction, speed) {
    speed = speed || 0.02;

    let platform = null;

    table.traverse(child => {
      if (child.userData.isRotatingPlatform) platform = child;
    });

    if (!platform) return false;

    if (direction === 'ROTATE_CW') {
      // Rotate clockwise
      table.userData.rotationSpeed = speed;
      platform.rotation.y += table.userData.rotationSpeed;
    } else if (direction === 'ROTATE_CCW') {
      // Rotate counter-clockwise
      table.userData.rotationSpeed = -speed;
      platform.rotation.y += table.userData.rotationSpeed;
    } else if (direction === 'STOP') {
      // Gradually stop
      table.userData.rotationSpeed *= 0.95;
      platform.rotation.y += table.userData.rotationSpeed;

      if (Math.abs(table.userData.rotationSpeed) < 0.001) {
        table.userData.rotationSpeed = 0;
        return true;
      }
    }

    return false;
  }
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = Rotary_Table;
}

if (typeof window !== 'undefined') {
  window.Rotary_Table = Rotary_Table;
}
