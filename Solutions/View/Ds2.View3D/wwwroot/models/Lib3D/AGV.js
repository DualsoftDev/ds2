/**
 * AGV - 자동 운반 차량 (Cartoon Style)
 * 명령어: MOVE, TURN, STOP
 * 용도: 자동 물류 운반
 * @version 1.0 - Cartoon/Toon rendering with movement animation
 */

class AGV {
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
      baseColor = 0xfbbf24,  // Yellow
      targetHeight = 1.5
    } = options;

    const gradientMap = this.createToonGradient(THREE);
    const agv = new THREE.Group();
    agv.userData.deviceType = 'AGV';
    agv.userData.wheelRotation = 0;

    // ===== BASE PLATFORM =====
    const baseGeometry = new THREE.BoxGeometry(1.2, 0.15, 0.8);
    const base = new THREE.Mesh(
      baseGeometry,
      new THREE.MeshToonMaterial({
        color: baseColor,
        gradientMap: gradientMap,
        emissive: baseColor,
        emissiveIntensity: 0.2
      })
    );
    base.position.y = 0.3;
    base.castShadow = true;
    agv.add(base);
    const baseOutline = this.createOutline(baseGeometry.clone(), 0x000000, 1.15);
    baseOutline.position.y = 0.3;
    agv.add(baseOutline);

    // ===== WHEELS (4개) =====
    const wheelGeometry = new THREE.CylinderGeometry(0.12, 0.12, 0.15, 16);
    const wheelMaterial = new THREE.MeshToonMaterial({
      color: 0x1e293b,
      gradientMap: gradientMap
    });

    const wheelPositions = [
      [-0.45, 0.12, -0.35],
      [-0.45, 0.12, 0.35],
      [0.45, 0.12, -0.35],
      [0.45, 0.12, 0.35]
    ];

    wheelPositions.forEach(pos => {
      const wheel = new THREE.Mesh(wheelGeometry.clone(), wheelMaterial);
      wheel.rotation.z = Math.PI / 2;
      wheel.position.set(...pos);
      wheel.castShadow = true;
      wheel.userData.isWheel = true;
      agv.add(wheel);

      const wheelOutline = this.createOutline(wheelGeometry.clone(), 0x000000, 1.15);
      wheelOutline.rotation.z = Math.PI / 2;
      wheelOutline.position.set(...pos);
      agv.add(wheelOutline);
    });

    // ===== CARGO AREA =====
    const cargoGeometry = new THREE.BoxGeometry(0.9, 0.4, 0.6);
    const cargo = new THREE.Mesh(
      cargoGeometry,
      new THREE.MeshToonMaterial({
        color: 0x94a3b8,
        gradientMap: gradientMap,
        transparent: true,
        opacity: 0.7
      })
    );
    cargo.position.y = 0.58;
    cargo.castShadow = true;
    agv.add(cargo);
    const cargoOutline = this.createOutline(cargoGeometry.clone(), 0x000000, 1.12);
    cargoOutline.position.y = 0.58;
    agv.add(cargoOutline);

    // ===== SENSOR/ANTENNA =====
    const sensorGeometry = new THREE.CylinderGeometry(0.05, 0.05, 0.3, 12);
    const sensor = new THREE.Mesh(
      sensorGeometry,
      new THREE.MeshToonMaterial({
        color: 0x22d3ee,
        gradientMap: gradientMap,
        emissive: 0x06b6d4,
        emissiveIntensity: 0.5
      })
    );
    sensor.position.set(0, 0.93, 0);
    sensor.castShadow = true;
    agv.add(sensor);
    const sensorOutline = this.createOutline(sensorGeometry.clone(), 0x000000, 1.18);
    sensorOutline.position.set(0, 0.93, 0);
    agv.add(sensorOutline);

    // Sensor top
    const sensorTopGeometry = new THREE.SphereGeometry(0.08, 12, 12);
    const sensorTop = new THREE.Mesh(
      sensorTopGeometry,
      new THREE.MeshToonMaterial({
        color: 0x22d3ee,
        gradientMap: gradientMap,
        emissive: 0x06b6d4,
        emissiveIntensity: 0.7
      })
    );
    sensorTop.position.set(0, 1.12, 0);
    sensorTop.castShadow = true;
    agv.add(sensorTop);
    const sensorTopOutline = this.createOutline(sensorTopGeometry.clone(), 0x000000, 1.20);
    sensorTopOutline.position.set(0, 1.12, 0);
    agv.add(sensorTopOutline);

    // ===== LABEL =====
    const canvas = document.createElement('canvas');
    canvas.width = 256;
    canvas.height = 128;
    const ctx = canvas.getContext('2d');

    ctx.fillStyle = '#334155';
    ctx.fillRect(0, 0, 256, 128);

    ctx.strokeStyle = '#fbbf24';
    ctx.lineWidth = 6;
    ctx.strokeRect(6, 6, 244, 116);

    ctx.font = 'bold 48px Arial';
    ctx.textAlign = 'center';

    ctx.strokeStyle = '#000000';
    ctx.lineWidth = 8;
    ctx.strokeText('AGV', 128, 80);
    ctx.fillStyle = '#22d3ee';
    ctx.fillText('AGV', 128, 80);

    const texture = new THREE.CanvasTexture(canvas);
    const label = new THREE.Mesh(
      new THREE.PlaneGeometry(0.5, 0.25),
      new THREE.MeshBasicMaterial({ map: texture, transparent: true })
    );
    label.position.set(0, 0.45, 0.41);
    agv.add(label);

    // ===== STATE INDICATOR =====
    const indicator = new THREE.Mesh(
      new THREE.BoxGeometry(1.4, 1.3, 1.0),
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
    agv.add(indicator);

    const bbox = new THREE.Box3().setFromObject(agv);
    const scale = targetHeight / (bbox.max.y - bbox.min.y);
    agv.scale.set(scale, scale, scale);

    return agv;
  }

  static updateState(agv, state) {
    const colors = {
      'R': 0x22d3ee,
      'G': 0xfde047,
      'F': 0x60a5fa,
      'H': 0xa78bfa
    };
    const color = colors[state] || 0x22d3ee;
    agv.traverse(child => {
      if (child.userData.isStateIndicator && child.material) {
        child.material.color.setHex(color);
        child.material.emissive.setHex(color);
      }
    });
  }

  static animate(agv, direction, speed) {
    speed = speed || 0.05;

    if (direction === 'MOVE') {
      // 바퀴 회전
      agv.userData.wheelRotation = (agv.userData.wheelRotation || 0) + speed;
      agv.traverse(child => {
        if (child.userData.isWheel) {
          child.rotation.x = agv.userData.wheelRotation;
        }
      });
      return false;
    } else if (direction === 'TURN') {
      // AGV 본체 좌우 회전
      if (!agv.userData.turnAngle) agv.userData.turnAngle = 0;
      if (!agv.userData.turnDirection) agv.userData.turnDirection = 1;

      agv.userData.turnAngle += speed * agv.userData.turnDirection;
      agv.rotation.y = Math.sin(agv.userData.turnAngle) * 0.3;

      return false;
    } else if (direction === 'STOP') {
      // 정지
      return true;
    }

    return false;
  }
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = AGV;
}

if (typeof window !== 'undefined') {
  window.AGV = AGV;
}
