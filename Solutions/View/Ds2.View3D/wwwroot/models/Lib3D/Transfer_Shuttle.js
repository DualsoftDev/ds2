/**
 * Transfer_Shuttle - 셔틀 이송기 (Cartoon Style)
 * 명령어: SHUTTLE_LEFT, SHUTTLE_RIGHT, CENTER
 * 용도: 좌우 이송, 버퍼링
 * @version 1.0 - Cartoon/Toon rendering with shuttle motion
 */

class Transfer_Shuttle {
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
      baseColor = 0xec4899,  // Pink
      targetHeight = 1.5
    } = options;

    const gradientMap = this.createToonGradient(THREE);
    const shuttle = new THREE.Group();
    shuttle.userData.deviceType = 'Transfer_Shuttle';

    // ===== RAIL TRACK =====
    const railGeometry = new THREE.BoxGeometry(4.0, 0.15, 0.6);
    const rail = new THREE.Mesh(
      railGeometry,
      new THREE.MeshToonMaterial({
        color: 0x64748b,
        gradientMap: gradientMap
      })
    );
    rail.position.y = 0.4;
    rail.castShadow = true;
    shuttle.add(rail);
    const railOutline = this.createOutline(railGeometry.clone(), 0x000000, 1.12);
    railOutline.position.y = 0.4;
    shuttle.add(railOutline);

    // Rail supports
    const supportGeometry = new THREE.BoxGeometry(0.2, 0.4, 0.7);
    [-1.5, 0, 1.5].forEach(x => {
      const support = new THREE.Mesh(
        supportGeometry.clone(),
        new THREE.MeshToonMaterial({
          color: 0x475569,
          gradientMap: gradientMap
        })
      );
      support.position.set(x, 0.2, 0);
      support.castShadow = true;
      shuttle.add(support);
      const supportOutline = this.createOutline(supportGeometry.clone(), 0x000000, 1.12);
      supportOutline.position.set(x, 0.2, 0);
      shuttle.add(supportOutline);
    });

    // ===== SHUTTLE CARRIAGE =====
    const carriageGroup = new THREE.Group();
    carriageGroup.position.y = 0.55;
    carriageGroup.userData.isCarriage = true;
    shuttle.add(carriageGroup);

    const carriageGeometry = new THREE.BoxGeometry(1.0, 0.25, 0.8);
    const carriage = new THREE.Mesh(
      carriageGeometry,
      new THREE.MeshToonMaterial({
        color: baseColor,
        gradientMap: gradientMap,
        emissive: baseColor,
        emissiveIntensity: 0.3
      })
    );
    carriage.castShadow = true;
    carriageGroup.add(carriage);
    const carriageOutline = this.createOutline(carriageGeometry.clone(), 0x000000, 1.15);
    carriageGroup.add(carriageOutline);

    // Platform
    const platformGeometry = new THREE.BoxGeometry(0.9, 0.1, 0.7);
    const platform = new THREE.Mesh(
      platformGeometry,
      new THREE.MeshToonMaterial({
        color: 0xfbbf24,
        gradientMap: gradientMap,
        emissive: 0xf59e0b,
        emissiveIntensity: 0.3
      })
    );
    platform.position.y = 0.18;
    platform.castShadow = true;
    carriageGroup.add(platform);
    const platformOutline = this.createOutline(platformGeometry.clone(), 0x000000, 1.12);
    platformOutline.position.y = 0.18;
    carriageGroup.add(platformOutline);

    // Guide wheels
    const wheelGeometry = new THREE.CylinderGeometry(0.08, 0.08, 0.12, 12);
    const wheelMaterial = new THREE.MeshToonMaterial({
      color: 0x1e293b,
      gradientMap: gradientMap
    });

    [-0.4, 0.4].forEach(x => {
      [-0.35, 0.35].forEach(z => {
        const wheel = new THREE.Mesh(wheelGeometry.clone(), wheelMaterial);
        wheel.position.set(x, -0.2, z);
        wheel.rotation.x = Math.PI / 2;
        wheel.castShadow = true;
        wheel.userData.isWheel = true;
        carriageGroup.add(wheel);
      });
    });

    // ===== DRIVE MOTOR =====
    const motorGeometry = new THREE.BoxGeometry(0.3, 0.25, 0.3);
    const motor = new THREE.Mesh(
      motorGeometry,
      new THREE.MeshToonMaterial({
        color: 0x60a5fa,
        gradientMap: gradientMap,
        emissive: 0x3b82f6,
        emissiveIntensity: 0.3
      })
    );
    motor.position.set(0, -0.08, -0.5);
    motor.castShadow = true;
    carriageGroup.add(motor);
    const motorOutline = this.createOutline(motorGeometry.clone(), 0x000000, 1.15);
    motorOutline.position.set(0, -0.08, -0.5);
    carriageGroup.add(motorOutline);

    // ===== LABEL =====
    const canvas = document.createElement('canvas');
    canvas.width = 256;
    canvas.height = 128;
    const ctx = canvas.getContext('2d');

    ctx.fillStyle = '#334155';
    ctx.fillRect(0, 0, 256, 128);

    ctx.strokeStyle = '#ec4899';
    ctx.lineWidth = 6;
    ctx.strokeRect(6, 6, 244, 116);

    ctx.font = 'bold 32px Arial';
    ctx.textAlign = 'center';

    ctx.strokeStyle = '#000000';
    ctx.lineWidth = 6;
    ctx.strokeText('TRANSFER', 128, 50);
    ctx.fillStyle = '#22d3ee';
    ctx.fillText('TRANSFER', 128, 50);

    ctx.strokeText('SHUTTLE', 128, 95);
    ctx.fillStyle = '#ec4899';
    ctx.fillText('SHUTTLE', 128, 95);

    const texture = new THREE.CanvasTexture(canvas);
    const label = new THREE.Mesh(
      new THREE.PlaneGeometry(0.8, 0.4),
      new THREE.MeshBasicMaterial({ map: texture, transparent: true })
    );
    label.position.set(0, 1.0, 0);
    shuttle.add(label);

    // ===== STATE INDICATOR =====
    const indicator = new THREE.Mesh(
      new THREE.BoxGeometry(4.2, 1.2, 1.0),
      new THREE.MeshToonMaterial({
        color: 0x22d3ee,
        transparent: true,
        opacity: 0.2,
        emissive: 0x22d3ee,
        emissiveIntensity: 0.4,
        gradientMap: gradientMap
      })
    );
    indicator.position.set(0, 0.5, 0);
    indicator.userData.isStateIndicator = true;
    shuttle.add(indicator);

    const bbox = new THREE.Box3().setFromObject(shuttle);
    const scale = targetHeight / (bbox.max.y - bbox.min.y);
    shuttle.scale.set(scale, scale, scale);

    return shuttle;
  }

  static updateState(shuttle, state) {
    const colors = {
      'R': 0x22d3ee,
      'G': 0xfde047,
      'F': 0x60a5fa,
      'H': 0xa78bfa
    };
    const color = colors[state] || 0x22d3ee;
    shuttle.traverse(child => {
      if (child.userData.isStateIndicator && child.material) {
        child.material.color.setHex(color);
        child.material.emissive.setHex(color);
      }
    });
  }

  static animate(shuttle, direction, speed) {
    speed = speed || 0.03;

    let carriage = null;

    shuttle.traverse(child => {
      if (child.userData.isCarriage) carriage = child;
    });

    if (!carriage) return false;

    const minX = -1.5;
    const maxX = 1.5;
    const currentX = carriage.position.x;

    if (direction === 'SHUTTLE_LEFT') {
      // Move left
      const targetX = minX;
      const newX = currentX + (targetX - currentX) * speed;
      carriage.position.x = newX;

      // Rotate wheels
      carriage.traverse(child => {
        if (child.userData.isWheel) {
          child.rotation.y -= speed * 2;
        }
      });

      return Math.abs(newX - targetX) < 0.01;
    } else if (direction === 'SHUTTLE_RIGHT') {
      // Move right
      const targetX = maxX;
      const newX = currentX + (targetX - currentX) * speed;
      carriage.position.x = newX;

      // Rotate wheels
      carriage.traverse(child => {
        if (child.userData.isWheel) {
          child.rotation.y += speed * 2;
        }
      });

      return Math.abs(newX - targetX) < 0.01;
    } else if (direction === 'CENTER') {
      // Move to center
      const targetX = 0;
      const newX = currentX + (targetX - currentX) * speed;
      carriage.position.x = newX;

      return Math.abs(newX - targetX) < 0.01;
    }

    return false;
  }
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = Transfer_Shuttle;
}

if (typeof window !== 'undefined') {
  window.Transfer_Shuttle = Transfer_Shuttle;
}
