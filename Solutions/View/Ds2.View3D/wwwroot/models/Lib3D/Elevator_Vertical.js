/**
 * Elevator_Vertical - 수직 엘리베이터 (Cartoon Style)
 * 명령어: FLOOR_1, FLOOR_2, FLOOR_3
 * - FLOOR_1: 1층으로 이동
 * - FLOOR_2: 2층으로 이동
 * - FLOOR_3: 3층으로 이동
 * 용도: 층간 이송, 수직 물류
 * @version 2.0 - Direct floor positioning (1F/2F/3F only)
 */

class Elevator_Vertical {
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
      baseColor = 0x0284c7,  // Sky blue
      targetHeight = 4.0
    } = options;

    const gradientMap = this.createToonGradient(THREE);
    const elevator = new THREE.Group();
    elevator.userData.deviceType = 'Elevator_Vertical';
    elevator.userData.floors = [0.5, 1.8, 3.1];

    // ===== SHAFT STRUCTURE =====
    const shaftGeometry = new THREE.BoxGeometry(0.15, 3.5, 0.15);
    const shaftMaterial = new THREE.MeshToonMaterial({
      color: 0x64748b,
      gradientMap: gradientMap
    });

    // 4 corner shafts
    [[-0.8, -0.6], [-0.8, 0.6], [0.8, -0.6], [0.8, 0.6]].forEach(([x, z]) => {
      const shaft = new THREE.Mesh(shaftGeometry.clone(), shaftMaterial);
      shaft.position.set(x, 1.75, z);
      shaft.castShadow = true;
      elevator.add(shaft);
      const shaftOutline = this.createOutline(shaftGeometry.clone(), 0x000000, 1.12);
      shaftOutline.position.set(x, 1.75, z);
      elevator.add(shaftOutline);
    });

    // ===== FLOOR PLATFORMS (3 levels) =====
    const floorGeometry = new THREE.BoxGeometry(1.8, 0.12, 1.4);
    const floorMaterial = new THREE.MeshToonMaterial({
      color: 0x94a3b8,
      gradientMap: gradientMap
    });

    [0.5, 1.8, 3.1].forEach((y, i) => {
      const floor = new THREE.Mesh(floorGeometry.clone(), floorMaterial);
      floor.position.y = y;
      floor.castShadow = true;
      elevator.add(floor);

      // Floor number label
      const labelCanvas = document.createElement('canvas');
      labelCanvas.width = 128;
      labelCanvas.height = 128;
      const ctx = labelCanvas.getContext('2d');
      ctx.fillStyle = '#fbbf24';
      ctx.font = 'bold 80px Arial';
      ctx.textAlign = 'center';
      ctx.fillText((i + 1).toString(), 64, 90);

      const labelTexture = new THREE.CanvasTexture(labelCanvas);
      const floorLabel = new THREE.Mesh(
        new THREE.PlaneGeometry(0.2, 0.2),
        new THREE.MeshBasicMaterial({ map: labelTexture, transparent: true })
      );
      floorLabel.position.set(-0.95, y + 0.15, 0);
      floorLabel.rotation.y = Math.PI / 2;
      elevator.add(floorLabel);
    });

    // ===== ELEVATOR CAR =====
    const carGroup = new THREE.Group();
    carGroup.position.y = 0.5;
    carGroup.userData.isCar = true;
    elevator.add(carGroup);

    // Car platform
    const carPlatformGeometry = new THREE.BoxGeometry(1.5, 0.15, 1.2);
    const carPlatform = new THREE.Mesh(
      carPlatformGeometry,
      new THREE.MeshToonMaterial({
        color: baseColor,
        gradientMap: gradientMap,
        emissive: baseColor,
        emissiveIntensity: 0.3
      })
    );
    carPlatform.castShadow = true;
    carGroup.add(carPlatform);
    const carPlatformOutline = this.createOutline(carPlatformGeometry.clone(), 0x000000, 1.12);
    carGroup.add(carPlatformOutline);

    // Safety railings
    const railingGeometry = new THREE.BoxGeometry(1.5, 0.08, 0.08);
    const railingMaterial = new THREE.MeshToonMaterial({
      color: 0xfbbf24,
      gradientMap: gradientMap,
      emissive: 0xf59e0b,
      emissiveIntensity: 0.4
    });

    [[-0.6, 0.4], [0.6, 0.4]].forEach(([z, y]) => {
      const railing = new THREE.Mesh(railingGeometry.clone(), railingMaterial);
      railing.position.set(0, y, z);
      railing.castShadow = true;
      carGroup.add(railing);
    });

    // Support posts
    const postGeometry = new THREE.CylinderGeometry(0.05, 0.05, 0.8, 12);
    [[-0.7, -0.6], [-0.7, 0.6], [0.7, -0.6], [0.7, 0.6]].forEach(([x, z]) => {
      const post = new THREE.Mesh(postGeometry.clone(), railingMaterial);
      post.position.set(x, 0.4, z);
      post.castShadow = true;
      carGroup.add(post);
    });

    // ===== CABLES =====
    const cableGeometry = new THREE.CylinderGeometry(0.03, 0.03, 3.5, 8);
    const cableMaterial = new THREE.MeshToonMaterial({
      color: 0x475569,
      gradientMap: gradientMap
    });

    [[-0.7, -0.55], [-0.7, 0.55], [0.7, -0.55], [0.7, 0.55]].forEach(([x, z]) => {
      const cable = new THREE.Mesh(cableGeometry.clone(), cableMaterial);
      cable.position.set(x, 1.75, z);
      cable.castShadow = true;
      elevator.add(cable);
    });

    // ===== MOTOR HOUSING =====
    const motorGeometry = new THREE.BoxGeometry(1.2, 0.4, 1.0);
    const motor = new THREE.Mesh(
      motorGeometry,
      new THREE.MeshToonMaterial({
        color: 0x60a5fa,
        gradientMap: gradientMap,
        emissive: 0x3b82f6,
        emissiveIntensity: 0.3
      })
    );
    motor.position.set(0, 3.7, 0);
    motor.castShadow = true;
    elevator.add(motor);
    const motorOutline = this.createOutline(motorGeometry.clone(), 0x000000, 1.15);
    motorOutline.position.set(0, 3.7, 0);
    elevator.add(motorOutline);

    // ===== LABEL =====
    const canvas = document.createElement('canvas');
    canvas.width = 256;
    canvas.height = 128;
    const ctx = canvas.getContext('2d');

    ctx.fillStyle = '#334155';
    ctx.fillRect(0, 0, 256, 128);

    ctx.strokeStyle = '#0284c7';
    ctx.lineWidth = 6;
    ctx.strokeRect(6, 6, 244, 116);

    ctx.font = 'bold 32px Arial';
    ctx.textAlign = 'center';

    ctx.strokeStyle = '#000000';
    ctx.lineWidth = 6;
    ctx.strokeText('VERTICAL', 128, 50);
    ctx.fillStyle = '#22d3ee';
    ctx.fillText('VERTICAL', 128, 50);

    ctx.strokeText('ELEVATOR', 128, 95);
    ctx.fillStyle = '#0284c7';
    ctx.fillText('ELEVATOR', 128, 95);

    const texture = new THREE.CanvasTexture(canvas);
    const label = new THREE.Mesh(
      new THREE.PlaneGeometry(0.8, 0.4),
      new THREE.MeshBasicMaterial({ map: texture, transparent: true })
    );
    label.position.set(0, 2.5, 0);
    elevator.add(label);

    // ===== STATE INDICATOR =====
    const indicator = new THREE.Mesh(
      new THREE.BoxGeometry(2.0, 4.0, 1.8),
      new THREE.MeshToonMaterial({
        color: 0x22d3ee,
        transparent: true,
        opacity: 0.2,
        emissive: 0x22d3ee,
        emissiveIntensity: 0.4,
        gradientMap: gradientMap
      })
    );
    indicator.position.set(0, 1.8, 0);
    indicator.userData.isStateIndicator = true;
    elevator.add(indicator);

    const bbox = new THREE.Box3().setFromObject(elevator);
    const scale = targetHeight / (bbox.max.y - bbox.min.y);
    elevator.scale.set(scale, scale, scale);

    return elevator;
  }

  static updateState(elevator, state) {
    const colors = {
      'R': 0x22d3ee,
      'G': 0xfde047,
      'F': 0x60a5fa,
      'H': 0xa78bfa
    };
    const color = colors[state] || 0x22d3ee;
    elevator.traverse(child => {
      if (child.userData.isStateIndicator && child.material) {
        child.material.color.setHex(color);
        child.material.emissive.setHex(color);
      }
    });
  }

  /**
   * Animate elevator to specific floor
   * Only supports FLOOR_1, FLOOR_2, FLOOR_3
   */
  static animate(elevator, direction, speed) {
    speed = speed || 0.05;

    let car = null;

    elevator.traverse(child => {
      if (child.userData.isCar) car = child;
    });

    if (!car) return false;

    const floors = elevator.userData.floors || [0.5, 1.8, 3.1];
    const currentY = car.position.y;

    let targetY = currentY;

    // Only FLOOR_1, FLOOR_2, FLOOR_3 commands
    if (direction === 'FLOOR_1') {
      targetY = floors[0];  // 1층: 0.5
    } else if (direction === 'FLOOR_2') {
      targetY = floors[1];  // 2층: 1.8
    } else if (direction === 'FLOOR_3') {
      targetY = floors[2];  // 3층: 3.1
    }

    // Smooth movement to target floor
    const diff = targetY - currentY;

    if (Math.abs(diff) > 0.01) {
      car.position.y = currentY + diff * speed;
      return false;
    } else {
      // Snap to exact floor position
      car.position.y = targetY;
      return true;
    }
  }
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = Elevator_Vertical;
}

if (typeof window !== 'undefined') {
  window.Elevator_Vertical = Elevator_Vertical;
}
