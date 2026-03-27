/**
 * Conveyor - 컨베이어 (Cartoon Style)
 * 명령어: MOVE, STOP
 * 용도: 제품 이송
 * @version 2.0 - Cartoon/Toon rendering with animation
 */

class Conveyor {
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

  /**
   * Create arrow texture for belt movement visualization
   */
  static createArrowTexture() {
    const canvas = document.createElement('canvas');
    canvas.width = 512;
    canvas.height = 256;
    const ctx = canvas.getContext('2d');

    // Dark belt background
    ctx.fillStyle = '#18181b';
    ctx.fillRect(0, 0, 512, 256);

    // Draw repeating <<< arrows
    ctx.fillStyle = '#fbbf24'; // Bright amber arrows
    ctx.strokeStyle = '#000000';
    ctx.lineWidth = 4;
    ctx.font = 'bold 100px Arial';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';

    // Draw multiple arrow sets for seamless scrolling
    for (let i = 0; i < 4; i++) {
      const x = i * 128 + 64;

      // Black outline
      ctx.strokeText('<<<', x, 128);
      // Bright fill
      ctx.fillText('<<<', x, 128);
    }

    const texture = new THREE.CanvasTexture(canvas);
    texture.wrapS = THREE.RepeatWrapping;
    texture.wrapT = THREE.RepeatWrapping;
    texture.repeat.set(3, 1);

    return texture;
  }

  static create(THREE, options = {}) {
    const {
      baseColor = 0x22c55e, // Bright green
      targetHeight = 2.0
    } = options;

    const gradientMap = this.createToonGradient(THREE);
    const conveyor = new THREE.Group();
    conveyor.userData.deviceType = 'Conveyor';
    conveyor.userData.beltOffset = 0;

    // ===== LEGS (4개 지지대) =====
    const legGeometry = new THREE.BoxGeometry(0.15, 1.0, 0.15);
    const legMaterial = new THREE.MeshToonMaterial({
      color: 0x475569,
      gradientMap: gradientMap
    });

    const legPositions = [
      [-1.5, 0.5, -0.4],
      [-1.5, 0.5, 0.4],
      [1.5, 0.5, -0.4],
      [1.5, 0.5, 0.4]
    ];

    legPositions.forEach(pos => {
      const leg = new THREE.Mesh(legGeometry.clone(), legMaterial);
      leg.position.set(...pos);
      leg.castShadow = true;
      conveyor.add(leg);
      const legOutline = this.createOutline(legGeometry.clone(), 0x000000, 1.18);
      legOutline.position.set(...pos);
      conveyor.add(legOutline);
    });

    // ===== MAIN FRAME =====
    const frameGeometry = new THREE.BoxGeometry(3.5, 0.25, 1.0);
    const frame = new THREE.Mesh(
      frameGeometry,
      new THREE.MeshToonMaterial({
        color: 0x64748b,
        gradientMap: gradientMap
      })
    );
    frame.position.y = 1.125;
    frame.castShadow = true;
    conveyor.add(frame);
    const frameOutline = this.createOutline(frameGeometry.clone(), 0x000000, 1.15);
    frameOutline.position.y = 1.125;
    conveyor.add(frameOutline);

    // ===== SIDE RAILS (좌우 가이드) =====
    const railGeometry = new THREE.BoxGeometry(3.3, 0.2, 0.08);
    [-0.5, 0.5].forEach(z => {
      const rail = new THREE.Mesh(
        railGeometry.clone(),
        new THREE.MeshToonMaterial({
          color: baseColor,
          gradientMap: gradientMap,
          emissive: baseColor,
          emissiveIntensity: 0.3
        })
      );
      rail.position.set(0, 1.35, z);
      rail.castShadow = true;
      conveyor.add(rail);
      const railOutline = this.createOutline(railGeometry.clone(), 0x000000, 1.20);
      railOutline.position.set(0, 1.35, z);
      conveyor.add(railOutline);
    });

    // ===== ROLLERS (여러 개의 롤러) =====
    const rollerGeometry = new THREE.CylinderGeometry(0.06, 0.06, 1.0, 16);
    const rollerMaterial = new THREE.MeshToonMaterial({
      color: 0x94a3b8,
      gradientMap: gradientMap
    });

    for (let x = -1.5; x <= 1.5; x += 0.5) {
      const roller = new THREE.Mesh(rollerGeometry.clone(), rollerMaterial);
      roller.rotation.z = Math.PI / 2;
      roller.position.set(x, 1.28, 0);
      roller.castShadow = true;
      conveyor.add(roller);
      const rollerOutline = this.createOutline(rollerGeometry.clone(), 0x000000, 1.15);
      rollerOutline.rotation.z = Math.PI / 2;
      rollerOutline.position.set(x, 1.28, 0);
      conveyor.add(rollerOutline);
    }

    // ===== BELT (화살표 텍스처가 있는 벨트) =====
    const arrowTexture = this.createArrowTexture();
    const beltGeometry = new THREE.BoxGeometry(3.4, 0.06, 0.85);
    const belt = new THREE.Mesh(
      beltGeometry,
      new THREE.MeshToonMaterial({
        color: 0xffffff,
        gradientMap: gradientMap,
        map: arrowTexture
      })
    );
    belt.position.y = 1.38;
    belt.castShadow = true;
    belt.userData.isBelt = true;
    conveyor.add(belt);
    const beltOutline = this.createOutline(beltGeometry.clone(), 0x000000, 1.12);
    beltOutline.position.y = 1.38;
    conveyor.add(beltOutline);

    // ===== MOTOR HOUSING =====
    const motorGeometry = new THREE.BoxGeometry(0.5, 0.5, 0.5);
    const motor = new THREE.Mesh(
      motorGeometry,
      new THREE.MeshToonMaterial({
        color: 0x60a5fa,
        gradientMap: gradientMap,
        emissive: 0x3b82f6,
        emissiveIntensity: 0.4
      })
    );
    motor.position.set(-1.8, 0.8, 0.6);
    motor.castShadow = true;
    conveyor.add(motor);
    const motorOutline = this.createOutline(motorGeometry.clone(), 0x000000, 1.18);
    motorOutline.position.set(-1.8, 0.8, 0.6);
    conveyor.add(motorOutline);

    // Motor label "M"
    const motorCanvas = document.createElement('canvas');
    motorCanvas.width = 128;
    motorCanvas.height = 128;
    const motorCtx = motorCanvas.getContext('2d');

    motorCtx.fillStyle = '#60a5fa';
    motorCtx.fillRect(0, 0, 128, 128);

    motorCtx.strokeStyle = '#000000';
    motorCtx.lineWidth = 6;
    motorCtx.font = 'bold 90px Arial';
    motorCtx.textAlign = 'center';
    motorCtx.textBaseline = 'middle';
    motorCtx.strokeText('M', 64, 64);
    motorCtx.fillStyle = '#ffffff';
    motorCtx.fillText('M', 64, 64);

    const motorTexture = new THREE.CanvasTexture(motorCanvas);
    const motorLabel = new THREE.Mesh(
      new THREE.PlaneGeometry(0.4, 0.4),
      new THREE.MeshBasicMaterial({ map: motorTexture, transparent: true })
    );
    motorLabel.position.set(-1.8, 0.8, 0.85);
    conveyor.add(motorLabel);

    // ===== MOVE/STOP LABEL =====
    const canvas = document.createElement('canvas');
    canvas.width = 256;
    canvas.height = 128;
    const ctx = canvas.getContext('2d');

    ctx.fillStyle = '#334155';
    ctx.fillRect(0, 0, 256, 128);

    ctx.strokeStyle = '#22c55e';
    ctx.lineWidth = 6;
    ctx.strokeRect(6, 6, 244, 116);

    ctx.font = 'bold 44px Arial';
    ctx.textAlign = 'center';

    ctx.strokeStyle = '#000000';
    ctx.lineWidth = 8;
    ctx.strokeText('MOVE', 128, 50);
    ctx.fillStyle = '#22d3ee';
    ctx.fillText('MOVE', 128, 50);

    ctx.strokeStyle = '#000000';
    ctx.lineWidth = 8;
    ctx.strokeText('STOP', 128, 100);
    ctx.fillStyle = '#fb7185';
    ctx.fillText('STOP', 128, 100);

    const texture = new THREE.CanvasTexture(canvas);
    const label = new THREE.Mesh(
      new THREE.PlaneGeometry(0.6, 0.3),
      new THREE.MeshBasicMaterial({ map: texture, transparent: true })
    );
    label.position.set(0, 1.7, 0.6);
    conveyor.add(label);

    // ===== STATE INDICATOR =====
    const indicator = new THREE.Mesh(
      new THREE.BoxGeometry(3.6, 1.5, 1.2),
      new THREE.MeshToonMaterial({
        color: 0x22d3ee,
        transparent: true,
        opacity: 0.25,
        emissive: 0x22d3ee,
        emissiveIntensity: 0.5,
        gradientMap: gradientMap
      })
    );
    indicator.position.y = 1.0;
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

  /**
   * Animate conveyor movement (scroll belt texture)
   */
  static animate(conveyor, direction, speed) {
    if (direction === 'MOVE') {
      // Find the belt mesh
      let belt = null;
      conveyor.traverse(child => {
        if (child.userData.isBelt) {
          belt = child;
        }
      });

      if (belt && belt.material.map) {
        // Scroll texture to the left (arrow direction)
        belt.material.map.offset.x -= 0.015;

        // Keep offset in range to prevent precision issues
        if (belt.material.map.offset.x < -1) {
          belt.material.map.offset.x += 1;
        }
      }

      conveyor.userData.beltOffset = (conveyor.userData.beltOffset || 0) + 0.015;
      return false; // Continuous animation
    }
    return true; // Stop
  }
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = Conveyor;
}

if (typeof window !== 'undefined') {
  window.Conveyor = Conveyor;
}
