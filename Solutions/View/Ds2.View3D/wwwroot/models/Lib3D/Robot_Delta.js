/**
 * Robot_Delta - 델타 로봇 (Cartoon Style)
 * 명령어: PICK, PLACE, HOME
 * 용도: 고속 피킹, 분류
 * @version 1.0 - Cartoon/Toon rendering with delta mechanism animation
 */

class Robot_Delta {
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
      baseColor = 0x10b981,  // Green
      targetHeight = 3.0
    } = options;

    const gradientMap = this.createToonGradient(THREE);
    const robot = new THREE.Group();
    robot.userData.deviceType = 'Robot_Delta';
    robot.userData.armAngles = [0, 0, 0];

    // ===== TOP FRAME =====
    const frameGeometry = new THREE.CylinderGeometry(1.0, 1.0, 0.3, 6);
    const frame = new THREE.Mesh(
      frameGeometry,
      new THREE.MeshToonMaterial({
        color: 0x475569,
        gradientMap: gradientMap
      })
    );
    frame.position.y = 2.85;
    frame.castShadow = true;
    robot.add(frame);
    const frameOutline = this.createOutline(frameGeometry.clone(), 0x000000, 1.12);
    frameOutline.position.y = 2.85;
    robot.add(frameOutline);

    // ===== 3 ARMS (120도 간격) =====
    for (let i = 0; i < 3; i++) {
      const angle = (i * Math.PI * 2) / 3;
      const armGroup = new THREE.Group();
      armGroup.position.set(
        Math.cos(angle) * 0.6,
        2.7,
        Math.sin(angle) * 0.6
      );
      armGroup.userData.isArm = true;
      armGroup.userData.armIndex = i;
      robot.add(armGroup);

      // Upper arm
      const upperArmGeometry = new THREE.BoxGeometry(0.15, 1.2, 0.15);
      const upperArm = new THREE.Mesh(
        upperArmGeometry,
        new THREE.MeshToonMaterial({
          color: baseColor,
          gradientMap: gradientMap,
          emissive: baseColor,
          emissiveIntensity: 0.3
        })
      );
      upperArm.position.y = -0.6;
      upperArm.castShadow = true;
      armGroup.add(upperArm);
      const upperArmOutline = this.createOutline(upperArmGeometry.clone(), 0x000000, 1.18);
      upperArmOutline.position.y = -0.6;
      armGroup.add(upperArmOutline);

      // Lower arm
      const lowerArmGeometry = new THREE.BoxGeometry(0.12, 1.0, 0.12);
      const lowerArm = new THREE.Mesh(
        lowerArmGeometry,
        new THREE.MeshToonMaterial({
          color: 0xfbbf24,
          gradientMap: gradientMap,
          emissive: 0xf59e0b,
          emissiveIntensity: 0.2
        })
      );
      lowerArm.position.y = -1.5;
      lowerArm.castShadow = true;
      armGroup.add(lowerArm);
      const lowerArmOutline = this.createOutline(lowerArmGeometry.clone(), 0x000000, 1.18);
      lowerArmOutline.position.y = -1.5;
      armGroup.add(lowerArmOutline);
    }

    // ===== END EFFECTOR PLATFORM =====
    const effectorGroup = new THREE.Group();
    effectorGroup.position.y = 1.5;
    effectorGroup.userData.isEffector = true;
    robot.add(effectorGroup);

    const effectorGeometry = new THREE.CylinderGeometry(0.25, 0.25, 0.15, 16);
    const effector = new THREE.Mesh(
      effectorGeometry,
      new THREE.MeshToonMaterial({
        color: 0x22d3ee,
        gradientMap: gradientMap,
        emissive: 0x06b6d4,
        emissiveIntensity: 0.5
      })
    );
    effector.castShadow = true;
    effectorGroup.add(effector);
    const effectorOutline = this.createOutline(effectorGeometry.clone(), 0x000000, 1.18);
    effectorGroup.add(effectorOutline);

    // Vacuum cup
    const vacuumGeometry = new THREE.ConeGeometry(0.15, 0.3, 16);
    const vacuum = new THREE.Mesh(
      vacuumGeometry,
      new THREE.MeshToonMaterial({
        color: 0x60a5fa,
        gradientMap: gradientMap,
        transparent: true,
        opacity: 0.8
      })
    );
    vacuum.position.y = -0.22;
    vacuum.castShadow = true;
    effectorGroup.add(vacuum);
    const vacuumOutline = this.createOutline(vacuumGeometry.clone(), 0x000000, 1.15);
    vacuumOutline.position.y = -0.22;
    effectorGroup.add(vacuumOutline);

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

    ctx.font = 'bold 36px Arial';
    ctx.textAlign = 'center';

    ctx.strokeStyle = '#000000';
    ctx.lineWidth = 8;
    ctx.strokeText('DELTA', 128, 50);
    ctx.fillStyle = '#22d3ee';
    ctx.fillText('DELTA', 128, 50);

    ctx.font = 'bold 32px Arial';
    ctx.strokeText('ROBOT', 128, 95);
    ctx.fillStyle = '#10b981';
    ctx.fillText('ROBOT', 128, 95);

    const texture = new THREE.CanvasTexture(canvas);
    const label = new THREE.Mesh(
      new THREE.PlaneGeometry(0.8, 0.4),
      new THREE.MeshBasicMaterial({ map: texture, transparent: true })
    );
    label.position.set(0, 2.2, 0);
    robot.add(label);

    // ===== STATE INDICATOR =====
    const indicator = new THREE.Mesh(
      new THREE.BoxGeometry(2.5, 3.2, 2.5),
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
    robot.add(indicator);

    const bbox = new THREE.Box3().setFromObject(robot);
    const scale = targetHeight / (bbox.max.y - bbox.min.y);
    robot.scale.set(scale, scale, scale);

    return robot;
  }

  static updateState(robot, state) {
    const colors = {
      'R': 0x22d3ee,
      'G': 0xfde047,
      'F': 0x60a5fa,
      'H': 0xa78bfa
    };
    const color = colors[state] || 0x22d3ee;
    robot.traverse(child => {
      if (child.userData.isStateIndicator && child.material) {
        child.material.color.setHex(color);
        child.material.emissive.setHex(color);
      }
    });
  }

  static animate(robot, direction, speed) {
    speed = speed || 0.05;

    let effector = null;
    const arms = [];

    robot.traverse(child => {
      if (child.userData.isEffector) effector = child;
      if (child.userData.isArm) arms.push(child);
    });

    if (!effector) return false;

    const minY = 0.5;
    const maxY = 2.0;
    const currentY = effector.position.y;

    if (direction === 'PICK') {
      // Move down
      const targetY = minY;
      const newY = currentY + (targetY - currentY) * speed;
      effector.position.y = newY;

      // Rotate arms
      arms.forEach((arm, i) => {
        arm.rotation.z = Math.sin(Date.now() * 0.002 + i) * 0.1;
      });

      return Math.abs(newY - targetY) < 0.01;
    } else if (direction === 'PLACE') {
      // Move up
      const targetY = maxY;
      const newY = currentY + (targetY - currentY) * speed;
      effector.position.y = newY;

      arms.forEach((arm, i) => {
        arm.rotation.z = Math.sin(Date.now() * 0.002 + i) * 0.1;
      });

      return Math.abs(newY - targetY) < 0.01;
    } else if (direction === 'HOME') {
      // Return to middle
      const targetY = 1.5;
      const newY = currentY + (targetY - currentY) * speed;
      effector.position.y = newY;

      arms.forEach(arm => {
        arm.rotation.z *= 0.9;
      });

      return Math.abs(newY - targetY) < 0.01;
    }

    return false;
  }
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = Robot_Delta;
}

if (typeof window !== 'undefined') {
  window.Robot_Delta = Robot_Delta;
}
