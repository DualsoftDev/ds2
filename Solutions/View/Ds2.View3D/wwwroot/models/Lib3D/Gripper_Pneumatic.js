/**
 * Gripper_Pneumatic - 공압 그리퍼 (Cartoon Style)
 * 명령어: GRIP, RELEASE, HOLD
 * 용도: 물체 파지, 조립
 * @version 1.0 - Cartoon/Toon rendering with pneumatic gripper animation
 */

class Gripper_Pneumatic {
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
      baseColor = 0x8b5cf6,  // Purple
      targetHeight = 1.5
    } = options;

    const gradientMap = this.createToonGradient(THREE);
    const gripper = new THREE.Group();
    gripper.userData.deviceType = 'Gripper_Pneumatic';
    gripper.userData.gripperOpen = 0.3;

    // ===== MOUNTING PLATE =====
    const mountGeometry = new THREE.BoxGeometry(0.5, 0.15, 0.4);
    const mount = new THREE.Mesh(
      mountGeometry,
      new THREE.MeshToonMaterial({
        color: 0x64748b,
        gradientMap: gradientMap
      })
    );
    mount.position.y = 0.6;
    mount.castShadow = true;
    gripper.add(mount);
    const mountOutline = this.createOutline(mountGeometry.clone(), 0x000000, 1.12);
    mountOutline.position.y = 0.6;
    gripper.add(mountOutline);

    // ===== PNEUMATIC CYLINDER =====
    const cylinderGeometry = new THREE.CylinderGeometry(0.12, 0.12, 0.4, 16);
    const cylinder = new THREE.Mesh(
      cylinderGeometry,
      new THREE.MeshToonMaterial({
        color: baseColor,
        gradientMap: gradientMap,
        emissive: baseColor,
        emissiveIntensity: 0.3
      })
    );
    cylinder.position.y = 0.3;
    cylinder.castShadow = true;
    gripper.add(cylinder);
    const cylinderOutline = this.createOutline(cylinderGeometry.clone(), 0x000000, 1.15);
    cylinderOutline.position.y = 0.3;
    gripper.add(cylinderOutline);

    // ===== GRIPPER BODY =====
    const bodyGeometry = new THREE.BoxGeometry(0.4, 0.2, 0.35);
    const body = new THREE.Mesh(
      bodyGeometry,
      new THREE.MeshToonMaterial({
        color: 0x22d3ee,
        gradientMap: gradientMap,
        emissive: 0x06b6d4,
        emissiveIntensity: 0.4
      })
    );
    body.position.y = 0.1;
    body.castShadow = true;
    gripper.add(body);
    const bodyOutline = this.createOutline(bodyGeometry.clone(), 0x000000, 1.15);
    bodyOutline.position.y = 0.1;
    gripper.add(bodyOutline);

    // ===== JAWS (2개, 좌우로 움직임) =====
    const leftJawGroup = new THREE.Group();
    leftJawGroup.userData.isLeftJaw = true;
    gripper.add(leftJawGroup);

    const rightJawGroup = new THREE.Group();
    rightJawGroup.userData.isRightJaw = true;
    gripper.add(rightJawGroup);

    const jawGeometry = new THREE.BoxGeometry(0.12, 0.5, 0.15);
    const jawMaterial = new THREE.MeshToonMaterial({
      color: 0xfbbf24,
      gradientMap: gradientMap,
      emissive: 0xf59e0b,
      emissiveIntensity: 0.3
    });

    // Left jaw
    const leftJaw = new THREE.Mesh(jawGeometry.clone(), jawMaterial);
    leftJaw.position.set(-0.3, -0.15, 0);
    leftJaw.castShadow = true;
    leftJawGroup.add(leftJaw);
    const leftJawOutline = this.createOutline(jawGeometry.clone(), 0x000000, 1.18);
    leftJawOutline.position.set(-0.3, -0.15, 0);
    leftJawGroup.add(leftJawOutline);

    // Right jaw
    const rightJaw = new THREE.Mesh(jawGeometry.clone(), jawMaterial);
    rightJaw.position.set(0.3, -0.15, 0);
    rightJaw.castShadow = true;
    rightJawGroup.add(rightJaw);
    const rightJawOutline = this.createOutline(jawGeometry.clone(), 0x000000, 1.18);
    rightJawOutline.position.set(0.3, -0.15, 0);
    rightJawGroup.add(rightJawOutline);

    // Gripper pads
    const padGeometry = new THREE.BoxGeometry(0.12, 0.15, 0.18);
    const padMaterial = new THREE.MeshToonMaterial({
      color: 0xef4444,
      gradientMap: gradientMap
    });

    const leftPad = new THREE.Mesh(padGeometry.clone(), padMaterial);
    leftPad.position.set(-0.3, -0.42, 0);
    leftPad.castShadow = true;
    leftJawGroup.add(leftPad);
    const leftPadOutline = this.createOutline(padGeometry.clone(), 0x000000, 1.20);
    leftPadOutline.position.set(-0.3, -0.42, 0);
    leftJawGroup.add(leftPadOutline);

    const rightPad = new THREE.Mesh(padGeometry.clone(), padMaterial);
    rightPad.position.set(0.3, -0.42, 0);
    rightPad.castShadow = true;
    rightJawGroup.add(rightPad);
    const rightPadOutline = this.createOutline(padGeometry.clone(), 0x000000, 1.20);
    rightPadOutline.position.set(0.3, -0.42, 0);
    rightJawGroup.add(rightPadOutline);

    // ===== LABEL =====
    const canvas = document.createElement('canvas');
    canvas.width = 256;
    canvas.height = 128;
    const ctx = canvas.getContext('2d');

    ctx.fillStyle = '#334155';
    ctx.fillRect(0, 0, 256, 128);

    ctx.strokeStyle = '#8b5cf6';
    ctx.lineWidth = 6;
    ctx.strokeRect(6, 6, 244, 116);

    ctx.font = 'bold 28px Arial';
    ctx.textAlign = 'center';

    ctx.strokeStyle = '#000000';
    ctx.lineWidth = 6;
    ctx.strokeText('PNEUMATIC', 128, 50);
    ctx.fillStyle = '#22d3ee';
    ctx.fillText('PNEUMATIC', 128, 50);

    ctx.font = 'bold 32px Arial';
    ctx.strokeText('GRIPPER', 128, 95);
    ctx.fillStyle = '#8b5cf6';
    ctx.fillText('GRIPPER', 128, 95);

    const texture = new THREE.CanvasTexture(canvas);
    const label = new THREE.Mesh(
      new THREE.PlaneGeometry(0.6, 0.3),
      new THREE.MeshBasicMaterial({ map: texture, transparent: true })
    );
    label.position.set(0, 0.8, 0.25);
    gripper.add(label);

    // ===== STATE INDICATOR =====
    const indicator = new THREE.Mesh(
      new THREE.BoxGeometry(0.9, 1.2, 0.8),
      new THREE.MeshToonMaterial({
        color: 0x22d3ee,
        transparent: true,
        opacity: 0.2,
        emissive: 0x22d3ee,
        emissiveIntensity: 0.4,
        gradientMap: gradientMap
      })
    );
    indicator.position.set(0, 0.2, 0);
    indicator.userData.isStateIndicator = true;
    gripper.add(indicator);

    const bbox = new THREE.Box3().setFromObject(gripper);
    const scale = targetHeight / (bbox.max.y - bbox.min.y);
    gripper.scale.set(scale, scale, scale);

    return gripper;
  }

  static updateState(gripper, state) {
    const colors = {
      'R': 0x22d3ee,
      'G': 0xfde047,
      'F': 0x60a5fa,
      'H': 0xa78bfa
    };
    const color = colors[state] || 0x22d3ee;
    gripper.traverse(child => {
      if (child.userData.isStateIndicator && child.material) {
        child.material.color.setHex(color);
        child.material.emissive.setHex(color);
      }
    });
  }

  static animate(gripper, direction, speed) {
    speed = speed || 0.05;

    let leftJaw = null;
    let rightJaw = null;

    gripper.traverse(child => {
      if (child.userData.isLeftJaw) leftJaw = child;
      if (child.userData.isRightJaw) rightJaw = child;
    });

    if (!leftJaw || !rightJaw) return false;

    if (!gripper.userData.gripperOpen) gripper.userData.gripperOpen = 0.3;

    if (direction === 'GRIP') {
      // Close jaws
      const target = 0.05;
      gripper.userData.gripperOpen += (target - gripper.userData.gripperOpen) * speed;
      leftJaw.position.x = -gripper.userData.gripperOpen;
      rightJaw.position.x = gripper.userData.gripperOpen;
      return Math.abs(gripper.userData.gripperOpen - target) < 0.01;
    } else if (direction === 'RELEASE') {
      // Open jaws
      const target = 0.3;
      gripper.userData.gripperOpen += (target - gripper.userData.gripperOpen) * speed;
      leftJaw.position.x = -gripper.userData.gripperOpen;
      rightJaw.position.x = gripper.userData.gripperOpen;
      return Math.abs(gripper.userData.gripperOpen - target) < 0.01;
    } else if (direction === 'HOLD') {
      // Maintain current position
      leftJaw.position.x = -gripper.userData.gripperOpen;
      rightJaw.position.x = gripper.userData.gripperOpen;
      return false;
    }

    return false;
  }
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = Gripper_Pneumatic;
}

if (typeof window !== 'undefined') {
  window.Gripper_Pneumatic = Gripper_Pneumatic;
}
