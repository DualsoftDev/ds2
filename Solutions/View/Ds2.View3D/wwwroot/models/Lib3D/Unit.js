/**
 * Unit - 전진/후진 유닛 (Cartoon Style)
 * 명령어: ADV (Advance), RET (Retract)
 * 용도: 전진/후진 동작을 수행하는 기본 유닛
 * @version 2.0 - Cartoon/Toon rendering with animation
 */

class Unit {
  /**
   * Create toon gradient for strong cel-shading effect
   */
  static createToonGradient(THREE) {
    const colors = new Uint8Array(3);
    colors[0] = 50;
    colors[1] = 180;
    colors[2] = 255;
    const gradientMap = new THREE.DataTexture(colors, colors.length, 1, THREE.LuminanceFormat);
    gradientMap.needsUpdate = true;
    return gradientMap;
  }

  /**
   * Create thick outline for cartoon effect
   */
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

  static create(THREE, options = {}) {
    const {
      baseColor = 0x60a5fa, // Bright blue
      targetHeight = 2.0
    } = options;

    const gradientMap = this.createToonGradient(THREE);
    const unit = new THREE.Group();
    unit.userData.deviceType = 'Unit';

    // Base platform with cartoon style
    const baseGeometry = new THREE.BoxGeometry(1.5, 0.35, 1.0);
    const base = new THREE.Mesh(
      baseGeometry,
      new THREE.MeshToonMaterial({
        color: 0x64748b,
        gradientMap: gradientMap
      })
    );
    base.position.y = 0.175;
    base.castShadow = true;
    unit.add(base);
    const baseOutline = this.createOutline(baseGeometry.clone(), 0x000000, 1.15);
    baseOutline.position.y = 0.175;
    unit.add(baseOutline);

    // Guide rails (left/right) with bright colors
    const railGeometry = new THREE.BoxGeometry(1.2, 0.12, 0.1);
    [-0.4, 0.4].forEach(z => {
      const rail = new THREE.Mesh(
        railGeometry.clone(),
        new THREE.MeshToonMaterial({
          color: baseColor,
          gradientMap: gradientMap,
          emissive: baseColor,
          emissiveIntensity: 0.2
        })
      );
      rail.position.set(0, 0.4, z);
      rail.castShadow = true;
      unit.add(rail);
      const railOutline = this.createOutline(railGeometry.clone(), 0x000000, 1.20);
      railOutline.position.set(0, 0.4, z);
      unit.add(railOutline);
    });

    // Moving carriage with bright cartoon colors
    const carriageGeometry = new THREE.BoxGeometry(0.6, 0.4, 0.9);
    const carriage = new THREE.Mesh(
      carriageGeometry,
      new THREE.MeshToonMaterial({
        color: 0xfbbf24, // Bright amber
        gradientMap: gradientMap,
        emissive: 0xf59e0b,
        emissiveIntensity: 0.3
      })
    );
    carriage.position.set(0, 0.7, 0);
    carriage.castShadow = true;
    carriage.userData.isCarriage = true;
    unit.add(carriage);
    const carriageOutline = this.createOutline(carriageGeometry.clone(), 0x000000, 1.18);
    carriageOutline.position.set(0, 0.7, 0);
    carriageOutline.userData.isCarriageOutline = true;
    unit.add(carriageOutline);

    // Mounting plate on carriage
    const plateGeometry = new THREE.BoxGeometry(0.7, 0.12, 1.0);
    const plate = new THREE.Mesh(
      plateGeometry,
      new THREE.MeshToonMaterial({
        color: 0x94a3b8, // Brighter slate
        gradientMap: gradientMap
      })
    );
    plate.position.y = 1.0;
    plate.castShadow = true;
    unit.add(plate);
    const plateOutline = this.createOutline(plateGeometry.clone(), 0x000000, 1.15);
    plateOutline.position.y = 1.0;
    unit.add(plateOutline);

    // Cylinder (actuator) with bright colors
    const cylinderGeometry = new THREE.CylinderGeometry(0.1, 0.1, 0.8, 16);
    const cylinder = new THREE.Mesh(
      cylinderGeometry,
      new THREE.MeshToonMaterial({
        color: 0x475569,
        gradientMap: gradientMap,
        emissive: 0x1e293b,
        emissiveIntensity: 0.2
      })
    );
    cylinder.rotation.z = Math.PI / 2;
    cylinder.position.set(0, 0.7, -0.5);
    cylinder.castShadow = true;
    unit.add(cylinder);
    const cylinderOutline = this.createOutline(cylinderGeometry.clone(), 0x000000, 1.15);
    cylinderOutline.rotation.z = Math.PI / 2;
    cylinderOutline.position.set(0, 0.7, -0.5);
    unit.add(cylinderOutline);

    // Piston rod with cartoon style
    const rodGeometry = new THREE.CylinderGeometry(0.05, 0.05, 0.6, 12);
    const rod = new THREE.Mesh(
      rodGeometry,
      new THREE.MeshToonMaterial({
        color: 0xe5e7eb, // Bright silver
        gradientMap: gradientMap
      })
    );
    rod.rotation.z = Math.PI / 2;
    rod.position.set(0.2, 0.7, -0.5);
    rod.castShadow = true;
    rod.userData.isPiston = true;
    unit.add(rod);
    const rodOutline = this.createOutline(rodGeometry.clone(), 0x000000, 1.20);
    rodOutline.rotation.z = Math.PI / 2;
    rodOutline.position.set(0.2, 0.7, -0.5);
    rodOutline.userData.isPistonOutline = true;
    unit.add(rodOutline);

    // ADV/RET label with bold cartoon style
    const canvas = document.createElement('canvas');
    canvas.width = 256;
    canvas.height = 128;
    const ctx = canvas.getContext('2d');

    ctx.fillStyle = '#334155';
    ctx.fillRect(0, 0, 256, 128);

    ctx.strokeStyle = '#60a5fa';
    ctx.lineWidth = 6;
    ctx.strokeRect(6, 6, 244, 116);

    ctx.font = 'bold 44px Arial';
    ctx.textAlign = 'center';

    // ADV text
    ctx.strokeStyle = '#000000';
    ctx.lineWidth = 8;
    ctx.strokeText('ADV', 128, 50);
    ctx.fillStyle = '#22d3ee';
    ctx.fillText('ADV', 128, 50);

    // RET text
    ctx.strokeStyle = '#000000';
    ctx.lineWidth = 8;
    ctx.strokeText('RET', 128, 100);
    ctx.fillStyle = '#fb7185';
    ctx.fillText('RET', 128, 100);

    const texture = new THREE.CanvasTexture(canvas);
    const label = new THREE.Mesh(
      new THREE.PlaneGeometry(0.5, 0.25),
      new THREE.MeshBasicMaterial({ map: texture, transparent: true })
    );
    label.position.set(-0.8, 0.8, 0);
    label.rotation.y = Math.PI / 2;
    unit.add(label);

    // State indicator with cartoon glow
    const indicator = new THREE.Mesh(
      new THREE.BoxGeometry(1.7, 1.3, 1.2),
      new THREE.MeshToonMaterial({
        color: 0x22d3ee,
        transparent: true,
        opacity: 0.25,
        emissive: 0x22d3ee,
        emissiveIntensity: 0.5,
        gradientMap: gradientMap
      })
    );
    indicator.position.y = 0.65;
    indicator.userData.isStateIndicator = true;
    unit.add(indicator);

    // Scale to target height
    const bbox = new THREE.Box3().setFromObject(unit);
    const scale = targetHeight / (bbox.max.y - bbox.min.y);
    unit.scale.set(scale, scale, scale);

    return unit;
  }

  static updateState(unit, state) {
    const colors = {
      'R': 0x22d3ee,  // Bright cyan
      'G': 0xfde047,  // Bright yellow
      'F': 0x60a5fa,  // Bright blue
      'H': 0xa78bfa   // Bright purple
    };
    const color = colors[state] || 0x22d3ee;
    unit.traverse(child => {
      if (child.userData.isStateIndicator && child.material) {
        child.material.color.setHex(color);
        child.material.emissive.setHex(color);
      }
    });
  }

  /**
   * Animate carriage movement (ADV: forward, RET: backward)
   * @param {THREE.Group} unit - Unit object
   * @param {string} direction - 'ADV' or 'RET'
   * @param {number} speed - Animation speed (default: 0.1)
   */
  static animate(unit, direction, speed) {
    speed = speed || 0.1;

    // Find carriage and its outline
    let carriage = null;
    let carriageOutline = null;
    let piston = null;
    let pistonOutline = null;

    unit.traverse(child => {
      if (child.userData.isCarriage) carriage = child;
      if (child.userData.isCarriageOutline) carriageOutline = child;
      if (child.userData.isPiston) piston = child;
      if (child.userData.isPistonOutline) pistonOutline = child;
    });

    if (!carriage) return false;

    const scale = unit.scale.x;
    const minX = -0.3;  // Retracted position
    const maxX = 0.3;   // Advanced position

    const currentX = carriage.position.x / scale;
    let targetX = currentX;

    if (direction === 'ADV') {
      targetX = maxX;
    } else if (direction === 'RET') {
      targetX = minX;
    }

    const newX = currentX + (targetX - currentX) * speed;
    carriage.position.x = newX * scale;
    if (carriageOutline) carriageOutline.position.x = newX * scale;
    if (piston) piston.position.x = (newX + 0.2) * scale;
    if (pistonOutline) pistonOutline.position.x = (newX + 0.2) * scale;

    return Math.abs(newX - targetX) < 0.01;
  }

  /**
   * Set carriage position directly
   * @param {THREE.Group} unit - Unit object
   * @param {number} position - Position 0.0 (RET) to 1.0 (ADV)
   */
  static setPosition(unit, position) {
    position = Math.max(0, Math.min(1, position));

    let carriage = null;
    let carriageOutline = null;
    let piston = null;
    let pistonOutline = null;

    unit.traverse(child => {
      if (child.userData.isCarriage) carriage = child;
      if (child.userData.isCarriageOutline) carriageOutline = child;
      if (child.userData.isPiston) piston = child;
      if (child.userData.isPistonOutline) pistonOutline = child;
    });

    if (!carriage) return;

    const scale = unit.scale.x;
    const minX = -0.3;
    const maxX = 0.3;
    const targetX = minX + (maxX - minX) * position;

    carriage.position.x = targetX * scale;
    if (carriageOutline) carriageOutline.position.x = targetX * scale;
    if (piston) piston.position.x = (targetX + 0.2) * scale;
    if (pistonOutline) pistonOutline.position.x = (targetX + 0.2) * scale;
  }
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = Unit;
}

if (typeof window !== 'undefined') {
  window.Unit = Unit;
}
