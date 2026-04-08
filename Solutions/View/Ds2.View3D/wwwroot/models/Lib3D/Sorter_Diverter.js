/**
 * Sorter_Diverter - 다이버터 (Cartoon Style)
 * 명령어: DIVERT_LEFT, DIVERT_RIGHT, CENTER
 * 용도: 제품 분류, 경로 전환
 * @version 1.0 - Cartoon/Toon rendering with diverter motion
 */

class Sorter_Diverter {
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
      baseColor = 0x10b981,  // Emerald
      targetHeight = 1.2
    } = options;

    const gradientMap = this.createToonGradient(THREE);
    const sorter = new THREE.Group();
    sorter.userData.deviceType = 'Sorter_Diverter';

    // ===== BASE FRAME =====
    const frameGeometry = new THREE.BoxGeometry(2.5, 0.2, 1.0);
    const frame = new THREE.Mesh(
      frameGeometry,
      new THREE.MeshToonMaterial({
        color: 0x64748b,
        gradientMap: gradientMap
      })
    );
    frame.position.y = 0.4;
    frame.castShadow = true;
    sorter.add(frame);
    const frameOutline = this.createOutline(frameGeometry.clone(), 0x000000, 1.12);
    frameOutline.position.y = 0.4;
    sorter.add(frameOutline);

    // ===== ROTATING DIVERTER PLATE =====
    const diverterGroup = new THREE.Group();
    diverterGroup.position.y = 0.55;
    diverterGroup.userData.isDiverter = true;
    sorter.add(diverterGroup);

    const plateGeometry = new THREE.BoxGeometry(1.0, 0.1, 0.6);
    const plate = new THREE.Mesh(
      plateGeometry,
      new THREE.MeshToonMaterial({
        color: baseColor,
        gradientMap: gradientMap,
        emissive: baseColor,
        emissiveIntensity: 0.3
      })
    );
    plate.castShadow = true;
    diverterGroup.add(plate);
    const plateOutline = this.createOutline(plateGeometry.clone(), 0x000000, 1.15);
    diverterGroup.add(plateOutline);

    // Direction arrows
    const arrowGeometry = new THREE.ConeGeometry(0.12, 0.3, 8);
    const arrowMaterial = new THREE.MeshToonMaterial({
      color: 0xfbbf24,
      gradientMap: gradientMap,
      emissive: 0xf59e0b,
      emissiveIntensity: 0.5
    });

    const leftArrow = new THREE.Mesh(arrowGeometry.clone(), arrowMaterial);
    leftArrow.position.set(-0.3, 0.15, 0);
    leftArrow.rotation.x = Math.PI / 2;
    leftArrow.rotation.z = -Math.PI / 4;
    leftArrow.castShadow = true;
    diverterGroup.add(leftArrow);

    const rightArrow = new THREE.Mesh(arrowGeometry.clone(), arrowMaterial);
    rightArrow.position.set(0.3, 0.15, 0);
    rightArrow.rotation.x = Math.PI / 2;
    rightArrow.rotation.z = Math.PI / 4;
    rightArrow.castShadow = true;
    diverterGroup.add(rightArrow);

    // ===== GUIDE RAILS =====
    const railGeometry = new THREE.BoxGeometry(2.6, 0.08, 0.08);
    const railMaterial = new THREE.MeshToonMaterial({
      color: 0x94a3b8,
      gradientMap: gradientMap
    });

    [-0.5, 0.5].forEach(z => {
      const rail = new THREE.Mesh(railGeometry.clone(), railMaterial);
      rail.position.set(0, 0.52, z);
      rail.castShadow = true;
      sorter.add(rail);
    });

    // ===== ACTUATOR =====
    const actuatorGeometry = new THREE.CylinderGeometry(0.08, 0.08, 0.5, 12);
    const actuator = new THREE.Mesh(
      actuatorGeometry,
      new THREE.MeshToonMaterial({
        color: 0x60a5fa,
        gradientMap: gradientMap,
        emissive: 0x3b82f6,
        emissiveIntensity: 0.3
      })
    );
    actuator.position.set(0, 0.2, 0);
    actuator.castShadow = true;
    sorter.add(actuator);
    const actuatorOutline = this.createOutline(actuatorGeometry.clone(), 0x000000, 1.15);
    actuatorOutline.position.set(0, 0.2, 0);
    sorter.add(actuatorOutline);

    // ===== SENSORS =====
    const sensorGeometry = new THREE.BoxGeometry(0.1, 0.1, 0.1);
    const sensorMaterial = new THREE.MeshToonMaterial({
      color: 0x22d3ee,
      gradientMap: gradientMap,
      emissive: 0x06b6d4,
      emissiveIntensity: 0.6
    });

    [-1.2, 0, 1.2].forEach(x => {
      const sensor = new THREE.Mesh(sensorGeometry.clone(), sensorMaterial);
      sensor.position.set(x, 0.58, 0.55);
      sensor.castShadow = true;
      sensor.userData.isSensor = true;
      sorter.add(sensor);
    });

    // ===== LABEL =====
    const canvas = document.createElement('canvas');
    canvas.width = 256;
    canvas.height = 128;
    const ctx = canvas.getContext('2d');

    ctx.fillStyle = '#334155';
    ctx.fillRect(0, 0, 256, 128);

    ctx.strokeStyle = '#10b981';
    ctx.lineWidth = 6;
    ctx.strokeRect(6, 6, 244, 116);

    ctx.font = 'bold 32px Arial';
    ctx.textAlign = 'center';

    ctx.strokeStyle = '#000000';
    ctx.lineWidth = 6;
    ctx.strokeText('SORTER', 128, 50);
    ctx.fillStyle = '#22d3ee';
    ctx.fillText('SORTER', 128, 50);

    ctx.strokeText('DIVERTER', 128, 95);
    ctx.fillStyle = '#10b981';
    ctx.fillText('DIVERTER', 128, 95);

    const texture = new THREE.CanvasTexture(canvas);
    const label = new THREE.Mesh(
      new THREE.PlaneGeometry(0.8, 0.4),
      new THREE.MeshBasicMaterial({ map: texture, transparent: true })
    );
    label.position.set(0, 0.9, 0);
    sorter.add(label);

    // ===== STATE INDICATOR =====
    const indicator = new THREE.Mesh(
      new THREE.BoxGeometry(2.7, 0.8, 1.2),
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
    sorter.add(indicator);

    const bbox = new THREE.Box3().setFromObject(sorter);
    const scale = targetHeight / (bbox.max.y - bbox.min.y);
    sorter.scale.set(scale, scale, scale);

    return sorter;
  }

  static updateState(sorter, state) {
    const colors = {
      'R': 0x22d3ee,
      'G': 0xfde047,
      'F': 0x60a5fa,
      'H': 0xa78bfa
    };
    const color = colors[state] || 0x22d3ee;
    sorter.traverse(child => {
      if (child.userData.isStateIndicator && child.material) {
        child.material.color.setHex(color);
        child.material.emissive.setHex(color);
      }
    });
  }

  static animate(sorter, direction, speed) {
    speed = speed || 0.05;

    let diverter = null;

    sorter.traverse(child => {
      if (child.userData.isDiverter) diverter = child;
      if (child.userData.isSensor && child.material) {
        // Blink sensors
        const time = Date.now() * 0.003;
        child.material.emissiveIntensity = 0.3 + Math.sin(time) * 0.3;
      }
    });

    if (!diverter) return false;

    if (direction === 'DIVERT_LEFT') {
      // Angle left
      const targetAngle = -Math.PI / 6;
      const currentAngle = diverter.rotation.y;
      const newAngle = currentAngle + (targetAngle - currentAngle) * speed;
      diverter.rotation.y = newAngle;
      return Math.abs(newAngle - targetAngle) < 0.01;
    } else if (direction === 'DIVERT_RIGHT') {
      // Angle right
      const targetAngle = Math.PI / 6;
      const currentAngle = diverter.rotation.y;
      const newAngle = currentAngle + (targetAngle - currentAngle) * speed;
      diverter.rotation.y = newAngle;
      return Math.abs(newAngle - targetAngle) < 0.01;
    } else if (direction === 'CENTER') {
      // Return to center
      const targetAngle = 0;
      const currentAngle = diverter.rotation.y;
      const newAngle = currentAngle + (targetAngle - currentAngle) * speed;
      diverter.rotation.y = newAngle;
      return Math.abs(newAngle - targetAngle) < 0.01;
    }

    return false;
  }
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = Sorter_Diverter;
}

if (typeof window !== 'undefined') {
  window.Sorter_Diverter = Sorter_Diverter;
}
