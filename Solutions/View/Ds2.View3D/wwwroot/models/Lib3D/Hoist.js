/**
 * Hoist - 호이스트 (Cartoon Style)
 * 명령어: LIFT, LOWER, HOLD
 * 용도: 수직 승강, 중량물 리프팅
 * @version 1.0 - Cartoon/Toon rendering with vertical hoist motion
 */

class Hoist {
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
      baseColor = 0xdc2626,  // Red
      targetHeight = 3.0
    } = options;

    const gradientMap = this.createToonGradient(THREE);
    const hoist = new THREE.Group();
    hoist.userData.deviceType = 'Hoist';

    // ===== MOUNTING FRAME =====
    const frameGeometry = new THREE.BoxGeometry(1.2, 0.3, 1.2);
    const frame = new THREE.Mesh(
      frameGeometry,
      new THREE.MeshToonMaterial({
        color: 0x64748b,
        gradientMap: gradientMap
      })
    );
    frame.position.y = 2.85;
    frame.castShadow = true;
    hoist.add(frame);
    const frameOutline = this.createOutline(frameGeometry.clone(), 0x000000, 1.12);
    frameOutline.position.y = 2.85;
    hoist.add(frameOutline);

    // ===== MOTOR HOUSING =====
    const motorGeometry = new THREE.BoxGeometry(0.8, 0.6, 0.8);
    const motor = new THREE.Mesh(
      motorGeometry,
      new THREE.MeshToonMaterial({
        color: baseColor,
        gradientMap: gradientMap,
        emissive: baseColor,
        emissiveIntensity: 0.3
      })
    );
    motor.position.y = 2.4;
    motor.castShadow = true;
    hoist.add(motor);
    const motorOutline = this.createOutline(motorGeometry.clone(), 0x000000, 1.15);
    motorOutline.position.y = 2.4;
    hoist.add(motorOutline);

    // ===== DRUM =====
    const drumGeometry = new THREE.CylinderGeometry(0.3, 0.3, 0.7, 20);
    const drum = new THREE.Mesh(
      drumGeometry,
      new THREE.MeshToonMaterial({
        color: 0xfbbf24,
        gradientMap: gradientMap,
        emissive: 0xf59e0b,
        emissiveIntensity: 0.3
      })
    );
    drum.position.y = 2.1;
    drum.rotation.z = Math.PI / 2;
    drum.castShadow = true;
    drum.userData.isDrum = true;
    hoist.add(drum);
    const drumOutline = this.createOutline(drumGeometry.clone(), 0x000000, 1.15);
    drumOutline.position.y = 2.1;
    drumOutline.rotation.z = Math.PI / 2;
    hoist.add(drumOutline);

    // ===== CABLE =====
    const cableGroup = new THREE.Group();
    cableGroup.userData.isCable = true;
    hoist.add(cableGroup);

    const cableGeometry = new THREE.CylinderGeometry(0.05, 0.05, 2.0, 8);
    const cable = new THREE.Mesh(
      cableGeometry,
      new THREE.MeshToonMaterial({
        color: 0x94a3b8,
        gradientMap: gradientMap
      })
    );
    cable.position.y = 1.0;
    cable.castShadow = true;
    cableGroup.add(cable);
    const cableOutline = this.createOutline(cableGeometry.clone(), 0x000000, 1.15);
    cableOutline.position.y = 1.0;
    cableGroup.add(cableOutline);

    // ===== HOOK =====
    const hookGroup = new THREE.Group();
    hookGroup.position.y = 0.5;
    hookGroup.userData.isHook = true;
    cableGroup.add(hookGroup);

    // Hook body
    const hookBodyGeometry = new THREE.CylinderGeometry(0.2, 0.15, 0.5, 12);
    const hookBody = new THREE.Mesh(
      hookBodyGeometry,
      new THREE.MeshToonMaterial({
        color: 0xfbbf24,
        gradientMap: gradientMap,
        emissive: 0xf59e0b,
        emissiveIntensity: 0.5
      })
    );
    hookBody.castShadow = true;
    hookGroup.add(hookBody);
    const hookBodyOutline = this.createOutline(hookBodyGeometry.clone(), 0x000000, 1.20);
    hookGroup.add(hookBodyOutline);

    // Hook curve
    const hookCurveGeometry = new THREE.TorusGeometry(0.2, 0.06, 10, 16, Math.PI);
    const hookCurve = new THREE.Mesh(
      hookCurveGeometry,
      new THREE.MeshToonMaterial({
        color: 0xfbbf24,
        gradientMap: gradientMap,
        emissive: 0xf59e0b,
        emissiveIntensity: 0.5
      })
    );
    hookCurve.position.y = -0.25;
    hookCurve.rotation.x = Math.PI / 2;
    hookCurve.castShadow = true;
    hookGroup.add(hookCurve);

    // Safety latch
    const latchGeometry = new THREE.BoxGeometry(0.08, 0.15, 0.08);
    const latch = new THREE.Mesh(
      latchGeometry,
      new THREE.MeshToonMaterial({
        color: 0xef4444,
        gradientMap: gradientMap
      })
    );
    latch.position.set(0.2, -0.25, 0);
    latch.castShadow = true;
    hookGroup.add(latch);

    // ===== LABEL =====
    const canvas = document.createElement('canvas');
    canvas.width = 256;
    canvas.height = 128;
    const ctx = canvas.getContext('2d');

    ctx.fillStyle = '#334155';
    ctx.fillRect(0, 0, 256, 128);

    ctx.strokeStyle = '#dc2626';
    ctx.lineWidth = 6;
    ctx.strokeRect(6, 6, 244, 116);

    ctx.font = 'bold 48px Arial';
    ctx.textAlign = 'center';

    ctx.strokeStyle = '#000000';
    ctx.lineWidth = 8;
    ctx.strokeText('HOIST', 128, 80);
    ctx.fillStyle = '#22d3ee';
    ctx.fillText('HOIST', 128, 80);

    const texture = new THREE.CanvasTexture(canvas);
    const label = new THREE.Mesh(
      new THREE.PlaneGeometry(0.8, 0.4),
      new THREE.MeshBasicMaterial({ map: texture, transparent: true })
    );
    label.position.set(0, 1.8, 0);
    hoist.add(label);

    // ===== STATE INDICATOR =====
    const indicator = new THREE.Mesh(
      new THREE.BoxGeometry(1.5, 3.2, 1.5),
      new THREE.MeshToonMaterial({
        color: 0x22d3ee,
        transparent: true,
        opacity: 0.2,
        emissive: 0x22d3ee,
        emissiveIntensity: 0.4,
        gradientMap: gradientMap
      })
    );
    indicator.position.set(0, 1.5, 0);
    indicator.userData.isStateIndicator = true;
    hoist.add(indicator);

    const bbox = new THREE.Box3().setFromObject(hoist);
    const scale = targetHeight / (bbox.max.y - bbox.min.y);
    hoist.scale.set(scale, scale, scale);

    return hoist;
  }

  static updateState(hoist, state) {
    const colors = {
      'R': 0x22d3ee,
      'G': 0xfde047,
      'F': 0x60a5fa,
      'H': 0xa78bfa
    };
    const color = colors[state] || 0x22d3ee;
    hoist.traverse(child => {
      if (child.userData.isStateIndicator && child.material) {
        child.material.color.setHex(color);
        child.material.emissive.setHex(color);
      }
    });
  }

  static animate(hoist, direction, speed) {
    speed = speed || 0.03;

    let hook = null;
    let cable = null;
    let drum = null;

    hoist.traverse(child => {
      if (child.userData.isHook) hook = child;
      if (child.userData.isCable) cable = child;
      if (child.userData.isDrum) drum = child;
    });

    if (!hook || !cable) return false;

    const minY = 0.3;
    const maxY = 1.8;
    const currentY = hook.position.y;

    if (direction === 'LIFT') {
      // Lift hook up
      const targetY = maxY;
      const newY = currentY + (targetY - currentY) * speed;
      hook.position.y = newY;

      // Rotate drum
      if (drum) {
        drum.rotation.x += speed * 0.5;
      }

      return Math.abs(newY - targetY) < 0.01;
    } else if (direction === 'LOWER') {
      // Lower hook down
      const targetY = minY;
      const newY = currentY + (targetY - currentY) * speed;
      hook.position.y = newY;

      // Rotate drum
      if (drum) {
        drum.rotation.x -= speed * 0.5;
      }

      return Math.abs(newY - targetY) < 0.01;
    } else if (direction === 'HOLD') {
      // Hold position, slight oscillation
      hook.rotation.z = Math.sin(Date.now() * 0.002) * 0.05;
      return false;
    }

    return false;
  }
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = Hoist;
}

if (typeof window !== 'undefined') {
  window.Hoist = Hoist;
}
