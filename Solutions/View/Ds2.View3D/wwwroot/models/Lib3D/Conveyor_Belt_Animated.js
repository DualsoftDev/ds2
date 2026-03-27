/**
 * Conveyor_Belt_Animated - 애니메이션 컨베이어 벨트 (Cartoon Style)
 * 명령어: RUN, STOP, REVERSE
 * 용도: 자동 이송
 * @version 1.0 - Cartoon/Toon rendering with belt animation
 */

class Conveyor_Belt_Animated {
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
      baseColor = 0x78716c,  // Stone
      targetHeight = 1.5
    } = options;

    const gradientMap = this.createToonGradient(THREE);
    const conveyor = new THREE.Group();
    conveyor.userData.deviceType = 'Conveyor_Belt_Animated';
    conveyor.userData.beltOffset = 0;
    conveyor.userData.direction = 1;

    // ===== FRAME =====
    const frameGeometry = new THREE.BoxGeometry(4.0, 0.2, 0.8);
    const frame = new THREE.Mesh(
      frameGeometry,
      new THREE.MeshToonMaterial({
        color: 0x64748b,
        gradientMap: gradientMap
      })
    );
    frame.position.y = 0.5;
    frame.castShadow = true;
    conveyor.add(frame);
    const frameOutline = this.createOutline(frameGeometry.clone(), 0x000000, 1.12);
    frameOutline.position.y = 0.5;
    conveyor.add(frameOutline);

    // ===== ROLLERS (여러 개) =====
    const rollerGeometry = new THREE.CylinderGeometry(0.08, 0.08, 0.75, 16);
    const rollerMaterial = new THREE.MeshToonMaterial({
      color: 0x94a3b8,
      gradientMap: gradientMap
    });

    for (let i = -1.8; i <= 1.8; i += 0.4) {
      const roller = new THREE.Mesh(rollerGeometry.clone(), rollerMaterial);
      roller.position.set(i, 0.64, 0);
      roller.rotation.z = Math.PI / 2;
      roller.castShadow = true;
      roller.userData.isRoller = true;
      conveyor.add(roller);
      const rollerOutline = this.createOutline(rollerGeometry.clone(), 0x000000, 1.15);
      rollerOutline.position.set(i, 0.64, 0);
      rollerOutline.rotation.z = Math.PI / 2;
      conveyor.add(rollerOutline);
    }

    // ===== BELT SURFACE (moving texture) =====
    // Create striped belt texture
    const beltCanvas = document.createElement('canvas');
    beltCanvas.width = 256;
    beltCanvas.height = 64;
    const beltCtx = beltCanvas.getContext('2d');

    // Draw alternating stripes
    for (let i = 0; i < 10; i++) {
      beltCtx.fillStyle = i % 2 === 0 ? '#475569' : '#334155';
      beltCtx.fillRect(i * 25.6, 0, 25.6, 64);
    }

    const beltTexture = new THREE.CanvasTexture(beltCanvas);
    beltTexture.wrapS = THREE.RepeatWrapping;
    beltTexture.wrapT = THREE.RepeatWrapping;
    beltTexture.repeat.set(4, 1);

    const beltGeometry = new THREE.BoxGeometry(4.0, 0.05, 0.7);
    const belt = new THREE.Mesh(
      beltGeometry,
      new THREE.MeshToonMaterial({
        map: beltTexture,
        gradientMap: gradientMap,
        color: baseColor,
        emissive: baseColor,
        emissiveIntensity: 0.1
      })
    );
    belt.position.y = 0.73;
    belt.castShadow = true;
    belt.userData.isBelt = true;
    conveyor.add(belt);
    const beltOutline = this.createOutline(beltGeometry.clone(), 0x000000, 1.10);
    beltOutline.position.y = 0.73;
    conveyor.add(beltOutline);

    // ===== DRIVE MOTOR =====
    const motorGeometry = new THREE.BoxGeometry(0.3, 0.25, 0.3);
    const motor = new THREE.Mesh(
      motorGeometry,
      new THREE.MeshToonMaterial({
        color: 0xfbbf24,
        gradientMap: gradientMap,
        emissive: 0xf59e0b,
        emissiveIntensity: 0.3
      })
    );
    motor.position.set(1.8, 0.35, 0.6);
    motor.castShadow = true;
    conveyor.add(motor);
    const motorOutline = this.createOutline(motorGeometry.clone(), 0x000000, 1.15);
    motorOutline.position.set(1.8, 0.35, 0.6);
    conveyor.add(motorOutline);

    // ===== LABEL =====
    const canvas = document.createElement('canvas');
    canvas.width = 256;
    canvas.height = 128;
    const ctx = canvas.getContext('2d');

    ctx.fillStyle = '#334155';
    ctx.fillRect(0, 0, 256, 128);

    ctx.strokeStyle = '#78716c';
    ctx.lineWidth = 6;
    ctx.strokeRect(6, 6, 244, 116);

    ctx.font = 'bold 32px Arial';
    ctx.textAlign = 'center';

    ctx.strokeStyle = '#000000';
    ctx.lineWidth = 6;
    ctx.strokeText('CONVEYOR', 128, 50);
    ctx.fillStyle = '#22d3ee';
    ctx.fillText('CONVEYOR', 128, 50);

    ctx.font = 'bold 28px Arial';
    ctx.strokeText('BELT', 128, 95);
    ctx.fillStyle = '#78716c';
    ctx.fillText('BELT', 128, 95);

    const texture = new THREE.CanvasTexture(canvas);
    const label = new THREE.Mesh(
      new THREE.PlaneGeometry(0.8, 0.4),
      new THREE.MeshBasicMaterial({ map: texture, transparent: true })
    );
    label.position.set(0, 1.0, 0);
    conveyor.add(label);

    // ===== STATE INDICATOR =====
    const indicator = new THREE.Mesh(
      new THREE.BoxGeometry(4.2, 1.0, 1.0),
      new THREE.MeshToonMaterial({
        color: 0x22d3ee,
        transparent: true,
        opacity: 0.2,
        emissive: 0x22d3ee,
        emissiveIntensity: 0.4,
        gradientMap: gradientMap
      })
    );
    indicator.position.set(0, 0.6, 0);
    indicator.userData.isStateIndicator = true;
    conveyor.add(indicator);

    const bbox = new THREE.Box3().setFromObject(conveyor);
    const scale = targetHeight / (bbox.max.y - bbox.min.y);
    conveyor.scale.set(scale, scale, scale);

    return conveyor;
  }

  static updateState(conveyor, state) {
    const colors = {
      'R': 0x22d3ee,
      'G': 0xfde047,
      'F': 0x60a5fa,
      'H': 0xa78bfa
    };
    const color = colors[state] || 0x22d3ee;
    conveyor.traverse(child => {
      if (child.userData.isStateIndicator && child.material) {
        child.material.color.setHex(color);
        child.material.emissive.setHex(color);
      }
    });
  }

  static animate(conveyor, direction, speed) {
    speed = speed || 0.02;

    if (direction === 'RUN') {
      // Move belt forward
      conveyor.userData.direction = 1;
      conveyor.traverse(child => {
        if (child.userData.isBelt && child.material.map) {
          conveyor.userData.beltOffset += speed * conveyor.userData.direction;
          child.material.map.offset.x = conveyor.userData.beltOffset;
        }
        if (child.userData.isRoller) {
          child.rotation.x += speed * 2 * conveyor.userData.direction;
        }
      });
    } else if (direction === 'REVERSE') {
      // Move belt backward
      conveyor.userData.direction = -1;
      conveyor.traverse(child => {
        if (child.userData.isBelt && child.material.map) {
          conveyor.userData.beltOffset += speed * conveyor.userData.direction;
          child.material.map.offset.x = conveyor.userData.beltOffset;
        }
        if (child.userData.isRoller) {
          child.rotation.x += speed * 2 * conveyor.userData.direction;
        }
      });
    } else if (direction === 'STOP') {
      // Gradually stop
      conveyor.userData.direction *= 0.95;
      if (Math.abs(conveyor.userData.direction) < 0.01) {
        conveyor.userData.direction = 0;
        return true;
      }
      conveyor.traverse(child => {
        if (child.userData.isBelt && child.material.map) {
          conveyor.userData.beltOffset += speed * conveyor.userData.direction;
          child.material.map.offset.x = conveyor.userData.beltOffset;
        }
        if (child.userData.isRoller) {
          child.rotation.x += speed * 2 * conveyor.userData.direction;
        }
      });
    }

    return false;
  }
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = Conveyor_Belt_Animated;
}

if (typeof window !== 'undefined') {
  window.Conveyor_Belt_Animated = Conveyor_Belt_Animated;
}
