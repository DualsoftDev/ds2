/**
 * Gate_Vertical - 수직 게이트 (Cartoon Style)
 * 명령어: RAISE, LOWER, STOP
 * 용도: 수직 차단막, 안전 게이트
 * @version 1.0 - Cartoon/Toon rendering with vertical gate motion
 */

class Gate_Vertical {
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
      baseColor = 0xfb923c,  // Orange
      targetHeight = 2.5
    } = options;

    const gradientMap = this.createToonGradient(THREE);
    const gate = new THREE.Group();
    gate.userData.deviceType = 'Gate_Vertical';

    // ===== SUPPORT POSTS =====
    const postGeometry = new THREE.BoxGeometry(0.2, 2.5, 0.2);
    const postMaterial = new THREE.MeshToonMaterial({
      color: 0x64748b,
      gradientMap: gradientMap
    });

    const leftPost = new THREE.Mesh(postGeometry.clone(), postMaterial);
    leftPost.position.set(-1.2, 1.25, 0);
    leftPost.castShadow = true;
    gate.add(leftPost);
    const leftPostOutline = this.createOutline(postGeometry.clone(), 0x000000, 1.12);
    leftPostOutline.position.set(-1.2, 1.25, 0);
    gate.add(leftPostOutline);

    const rightPost = new THREE.Mesh(postGeometry.clone(), postMaterial);
    rightPost.position.set(1.2, 1.25, 0);
    rightPost.castShadow = true;
    gate.add(rightPost);
    const rightPostOutline = this.createOutline(postGeometry.clone(), 0x000000, 1.12);
    rightPostOutline.position.set(1.2, 1.25, 0);
    gate.add(rightPostOutline);

    // ===== GATE PANEL (moves vertically) =====
    const panelGroup = new THREE.Group();
    panelGroup.position.y = 0.1;
    panelGroup.userData.isGatePanel = true;
    gate.add(panelGroup);

    const panelGeometry = new THREE.BoxGeometry(2.2, 1.8, 0.08);
    const panel = new THREE.Mesh(
      panelGeometry,
      new THREE.MeshToonMaterial({
        color: baseColor,
        gradientMap: gradientMap,
        emissive: baseColor,
        emissiveIntensity: 0.3,
        transparent: true,
        opacity: 0.9
      })
    );
    panel.position.y = 0.9;
    panel.castShadow = true;
    panelGroup.add(panel);
    const panelOutline = this.createOutline(panelGeometry.clone(), 0x000000, 1.10);
    panelOutline.position.y = 0.9;
    panelGroup.add(panelOutline);

    // Warning stripes
    const stripeGeometry = new THREE.BoxGeometry(2.2, 0.15, 0.09);
    const stripeMaterial = new THREE.MeshToonMaterial({
      color: 0xfde047,
      gradientMap: gradientMap,
      emissive: 0xfbbf24,
      emissiveIntensity: 0.5
    });

    for (let i = 0; i < 4; i++) {
      const stripe = new THREE.Mesh(stripeGeometry.clone(), stripeMaterial);
      stripe.position.set(0, 0.3 + i * 0.4, 0.05);
      stripe.castShadow = true;
      panelGroup.add(stripe);
    }

    // ===== GUIDE RAILS =====
    const railGeometry = new THREE.BoxGeometry(0.08, 2.4, 0.08);
    const railMaterial = new THREE.MeshToonMaterial({
      color: 0x94a3b8,
      gradientMap: gradientMap
    });

    [-1.15, 1.15].forEach(x => {
      const rail = new THREE.Mesh(railGeometry.clone(), railMaterial);
      rail.position.set(x, 1.2, 0);
      rail.castShadow = true;
      gate.add(rail);
    });

    // ===== MOTOR HOUSING =====
    const motorGeometry = new THREE.BoxGeometry(0.5, 0.4, 0.4);
    const motor = new THREE.Mesh(
      motorGeometry,
      new THREE.MeshToonMaterial({
        color: 0x60a5fa,
        gradientMap: gradientMap,
        emissive: 0x3b82f6,
        emissiveIntensity: 0.3
      })
    );
    motor.position.set(1.5, 2.3, 0);
    motor.castShadow = true;
    gate.add(motor);
    const motorOutline = this.createOutline(motorGeometry.clone(), 0x000000, 1.15);
    motorOutline.position.set(1.5, 2.3, 0);
    gate.add(motorOutline);

    // ===== WARNING LIGHTS =====
    const lightGeometry = new THREE.CylinderGeometry(0.1, 0.1, 0.15, 12);
    const lightMaterial = new THREE.MeshStandardMaterial({
      color: 0xef4444,
      emissive: 0xef4444,
      emissiveIntensity: 1.0
    });

    [-1.2, 1.2].forEach(x => {
      const light = new THREE.Mesh(lightGeometry.clone(), lightMaterial);
      light.position.set(x, 2.55, 0);
      light.rotation.x = Math.PI / 2;
      light.castShadow = true;
      light.userData.isWarningLight = true;
      gate.add(light);
    });

    // ===== LABEL =====
    const canvas = document.createElement('canvas');
    canvas.width = 256;
    canvas.height = 128;
    const ctx = canvas.getContext('2d');

    ctx.fillStyle = '#334155';
    ctx.fillRect(0, 0, 256, 128);

    ctx.strokeStyle = '#fb923c';
    ctx.lineWidth = 6;
    ctx.strokeRect(6, 6, 244, 116);

    ctx.font = 'bold 36px Arial';
    ctx.textAlign = 'center';

    ctx.strokeStyle = '#000000';
    ctx.lineWidth = 8;
    ctx.strokeText('VERTICAL', 128, 50);
    ctx.fillStyle = '#22d3ee';
    ctx.fillText('VERTICAL', 128, 50);

    ctx.font = 'bold 32px Arial';
    ctx.strokeText('GATE', 128, 95);
    ctx.fillStyle = '#fb923c';
    ctx.fillText('GATE', 128, 95);

    const texture = new THREE.CanvasTexture(canvas);
    const label = new THREE.Mesh(
      new THREE.PlaneGeometry(0.8, 0.4),
      new THREE.MeshBasicMaterial({ map: texture, transparent: true })
    );
    label.position.set(0, 0.3, 0.1);
    gate.add(label);

    // ===== STATE INDICATOR =====
    const indicator = new THREE.Mesh(
      new THREE.BoxGeometry(2.8, 2.8, 0.3),
      new THREE.MeshToonMaterial({
        color: 0x22d3ee,
        transparent: true,
        opacity: 0.2,
        emissive: 0x22d3ee,
        emissiveIntensity: 0.4,
        gradientMap: gradientMap
      })
    );
    indicator.position.set(0, 1.25, 0);
    indicator.userData.isStateIndicator = true;
    gate.add(indicator);

    const bbox = new THREE.Box3().setFromObject(gate);
    const scale = targetHeight / (bbox.max.y - bbox.min.y);
    gate.scale.set(scale, scale, scale);

    return gate;
  }

  static updateState(gate, state) {
    const colors = {
      'R': 0x22d3ee,
      'G': 0xfde047,
      'F': 0x60a5fa,
      'H': 0xa78bfa
    };
    const color = colors[state] || 0x22d3ee;
    gate.traverse(child => {
      if (child.userData.isStateIndicator && child.material) {
        child.material.color.setHex(color);
        child.material.emissive.setHex(color);
      }
    });
  }

  static animate(gate, direction, speed) {
    speed = speed || 0.04;

    let panel = null;

    gate.traverse(child => {
      if (child.userData.isGatePanel) panel = child;
      if (child.userData.isWarningLight && child.material) {
        // Blink warning lights
        const time = Date.now() * 0.003;
        child.material.opacity = 0.5 + Math.sin(time) * 0.5;
      }
    });

    if (!panel) return false;

    const minY = 0.1;
    const maxY = 2.4;
    const currentY = panel.position.y;

    if (direction === 'RAISE') {
      // Raise gate
      const targetY = maxY;
      const newY = currentY + (targetY - currentY) * speed;
      panel.position.y = newY;
      return Math.abs(newY - targetY) < 0.01;
    } else if (direction === 'LOWER') {
      // Lower gate
      const targetY = minY;
      const newY = currentY + (targetY - currentY) * speed;
      panel.position.y = newY;
      return Math.abs(newY - targetY) < 0.01;
    } else if (direction === 'STOP') {
      // Hold position
      return false;
    }

    return false;
  }
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = Gate_Vertical;
}

if (typeof window !== 'undefined') {
  window.Gate_Vertical = Gate_Vertical;
}
